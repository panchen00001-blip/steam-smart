import fs from 'node:fs/promises';
import path from 'node:path';
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { SteamSmartEngine } from './smart-engine.mjs';
import { HostsManager } from './hosts-manager.mjs';
import { validateConfig } from './smart-config-manager.mjs';

const execFileAsync=promisify(execFile);
const args=process.argv.slice(2);
const arg=(name,fallback)=>{const i=args.indexOf(name);return i<0?fallback:args[i+1];};
const base=path.dirname(fileURLToPath(import.meta.url));
const runtime=path.resolve(arg('--runtime',path.join(base,'runtime')));
const config=validateConfig(JSON.parse((await fs.readFile(arg('--config',path.join(base,'smart-config.json')),'utf8')).replace(/^\uFEFF/,'')));
const parentPid=Number(arg('--parent-pid',0));
const recoverOnly=args.includes('--recover-only');
const runId=randomUUID();
const listen=config.listen||{host:'127.0.0.19',port:80};
const domains=[...new Set([...config.groups.flatMap(g=>g.hosts),...Object.keys(config.redirects||{})])];
await fs.mkdir(runtime,{recursive:true});
const statusFile=path.join(runtime,'status.json');
const controlFile=path.join(runtime,'control.json');
const lifecycleFile=path.join(runtime,'lifecycle.json');
const journalFile=path.join(runtime,'hosts-state.json');
const events=[];
const sockets=new Set();
const startedAt=new Date().toISOString();
let state='starting',message='正在准备 Steam 自动优选…',engine,server,manager,hasAcquired=false,attemptedAcquire=false;
let stopRequested=false,fatal=false,legacyWanted=false,statusWriting=false,restoreResult=null;
let wakeStop;
const stopPromise=new Promise(resolve=>{wakeStop=resolve;});
const alive=pid=>{try{process.kill(Number(pid),0);return true;}catch{return false;}};
async function readJson(file){try{return JSON.parse((await fs.readFile(file,'utf8')).replace(/^\uFEFF/,''));}catch{return null;}}
async function atomicJson(file,value){const temporary=`${file}.${process.pid}.tmp`;await fs.writeFile(temporary,JSON.stringify(value,null,2),'utf8');await fs.rename(temporary,file);}
function event(text){events.push({at:new Date().toISOString(),message:String(text).slice(0,240)});if(events.length>30)events.shift();}
function requestStop(reason='正在停止自动优选并恢复设置…',isFatal=false){if(stopRequested)return;stopRequested=true;fatal=isFatal;message=reason;wakeStop();}
function cleanSnapshot(){
    const source=engine?.snapshot()||{};
    return {
        totalBytes:Number(source.totalBytes)||0,probeBytes:Number(source.probeBytes)||0,
        totalRateBps:Number(source.rateBps??source.totalRateBps)||0,
        groups:(source.groups||[]).map(group=>({
            id:group.id,name:group.name,rateBps:Number(group.rateBps)||0,testing:Boolean(group.testing||source.testing?.group===group.id),
            ips:(group.ips||[]).map(ip=>({ip:ip.ip,rateBps:Number(ip.rateBps)||0,scoreMbps:Number(ip.scoreMbps??(ip.scoreBps/125000))||0,
                valid:Boolean(ip.valid??(ip.result==='direct'&&ip.scoreBps>0)),state:ip.result||ip.state||'',selected:Boolean(ip.preferred??ip.selected)}))
        })).sort((a,b)=>b.rateBps-a.rateBps)
    };
}
async function writeStatus(force=false){
    if(statusWriting&&!force)return;statusWriting=true;
    try{await atomicJson(statusFile,{runId,pid:process.pid,parentPid,startedAt,state,message,...cleanSnapshot(),events:[...events],restoreResult});}
    finally{statusWriting=false;}
}
async function bridge(action){
    if(config.skipLegacy)return {Exists:false,WasRunning:false,Restored:false};
    const powershell=path.join(process.env.SystemRoot||'C:/Windows','System32/WindowsPowerShell/v1.0/powershell.exe');
    const {stdout}=await execFileAsync(powershell,['-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(base,'SteamSmartBridge.ps1'),'-Action',action],{windowsHide:true,timeout:12000,maxBuffer:128*1024});
    return JSON.parse(stdout.trim().replace(/^\uFEFF/,''));
}
async function health(){
    return new Promise(resolve=>{
        const req=http.get({host:listen.host,port:listen.port,path:'/__steam_smart_health',headers:{Host:listen.host},agent:false},res=>{
            let body='';res.on('data',c=>{if(body.length<4096)body+=c;});res.on('end',()=>{try{const p=JSON.parse(body);resolve(p.name==='SteamSmart'?p:null);}catch{resolve(null);}});
        });
        req.on('error',()=>resolve(null));req.setTimeout(350,()=>{req.destroy();resolve(null);});
    });
}
async function closeServer(){
    if(!server)return;
    const closed=new Promise(resolve=>server.close(resolve));
    server.closeIdleConnections?.();
    const timer=setTimeout(()=>{for(const socket of sockets)socket.destroy();server.closeAllConnections?.();},2500);
    await closed;clearTimeout(timer);
}
async function restore(){
    state='stopping';message='正在恢复 Steam 转发设置…';await writeStatus(true).catch(()=>{});
    await closeServer().catch(()=>{});
    await engine?.close();
    let legacy={Restored:false};
    if(legacyWanted){
        try{legacy=await bridge('StartLegacy');}catch{event('原助手未能自动恢复，正在恢复 Steam 正常解析。');}
    }
    if(manager){
        const result=await manager.release({restoreOriginal:true,restoreLoopback:Boolean(legacy.Restored)});
        if(result.conflicts?.length)event('检测到手工修改的 Steam 域名映射，已保留该修改。');
        restoreResult={legacyRestored:Boolean(legacy.Restored),hostsReleased:!result.owned,conflicts:result.conflicts?.length||0};
    }
    await atomicJson(lifecycleFile,{active:false,runId,legacyRestoreWanted:false,at:new Date().toISOString()});
}

// A live instance owns the listener; never undo another instance's mappings.
const existing=await health();
if(existing){
    if(recoverOnly){
        await atomicJson(controlFile,{runId:existing.runId,command:'stop'});
        for(let i=0;i<60;i++){await new Promise(r=>setTimeout(r,200));if(!(await health()))break;}
    }
    process.exit(0);
}

const timers=[];
let reconciling=false;
try{
    const previous=await readJson(lifecycleFile);
    legacyWanted=Boolean(previous?.active&&previous?.legacyRestoreWanted);
    manager=new HostsManager({path:config.hostsPath,ip:listen.host,domains,statePath:journalFile});
    await writeStatus(true);
    if(recoverOnly){
        event('正在恢复上一次异常退出前的设置。');
        await restore();state='stopped';message='已恢复 Steam 转发设置。';await writeStatus(true);process.exit(0);
    }
    timers.push(setInterval(()=>{writeStatus().catch(()=>{});},1000));
    timers.push(setInterval(async()=>{
        const control=await readJson(controlFile);
        if(control?.runId===runId&&control?.command==='stop')requestStop();
        if(parentPid&&!alive(parentPid))requestStop('控制窗口已退出，正在恢复设置…');
    },500));
    process.on('SIGINT',()=>requestStop());process.on('SIGTERM',()=>requestStop());
    process.on('uncaughtException',()=>requestStop('自动优选组件发生错误，正在恢复设置…',true));
    process.on('unhandledRejection',()=>requestStop('自动优选组件发生错误，正在恢复设置…',true));
    const context=await bridge('Status');
    legacyWanted=legacyWanted||Boolean(context.WasRunning&&context.Steam);
    if(context.WasRunning&&!context.Steam)throw Object.assign(new Error(),{code:'OTHER_PLATFORM_RUNNING'});
    await atomicJson(lifecycleFile,{active:true,runId,legacyRestoreWanted:legacyWanted,legacyContext:context,at:startedAt});
    attemptedAcquire=true;
    const acquired=await manager.acquire();
    if(acquired.conflicts?.length)throw Object.assign(new Error(),{code:'HOSTS_CONFLICT'});
    hasAcquired=true;
    if(stopRequested)throw Object.assign(new Error(),{code:'CANCELLED'});
    engine=new SteamSmartEngine(config,{statePath:path.join(runtime,'ip-history.json'),onEvent:entry=>{
        // The engine owns the event schema; accept only its sanitized message.
        if(typeof entry==='string')event(entry);
        else if(typeof entry?.message==='string')event(entry.message);
        else if(entry){
            const name=String(entry.name||'Steam');
            if(entry.type==='dns_changed')event(`${name} 地址池发生变化，将验证新地址。`);
            if(entry.type==='test_started')event(`${name} 正在主动验证当前游戏的下载地址。`);
            if(entry.type==='test_finished')event(`${name} 本轮已验证 ${Number(entry.tested)||0} 个地址。`);
            if(entry.type==='slow_transfer_retry')event(`${name} 检测到持续慢连接，正在切换已验证地址。`);
            if(entry.type==='tested_ip'&&entry.result==='direct')event(`${name} ${entry.ip} 实测 ${(Number(entry.bps)/1048576).toFixed(2)} MB/s。`);
        }
    }});
    if(!config.skipWarmup)engine.refresh().catch(()=>{});
    await bridge('StopLegacy');
    if(stopRequested)throw Object.assign(new Error(),{code:'CANCELLED'});
    server=http.createServer((req,res)=>{
        if(req.method==='GET'&&req.url==='/__steam_smart_health'){
            res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'});
            res.end(JSON.stringify({name:'SteamSmart',runId,pid:process.pid}));return;
        }
        Promise.resolve(engine.handle(req,res)).catch(()=>{if(!res.headersSent)res.writeHead(502);res.end();});
    });
    server.on('connection',socket=>{sockets.add(socket);socket.on('close',()=>sockets.delete(socket));});
    server.on('clientError',(_,socket)=>socket.destroy());
    await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(listen.port,listen.host,()=>{server.off('error',reject);resolve();});});
    server.on('error',()=>requestStop('转发监听异常，正在恢复设置…',true));
    state='running';message='自动检测已开启：下载时验证节点，变慢或地址变化时重新检测。';event('Steam 自动优选已接管，原助手无需再启动 Steam 服务。');
    await writeStatus(true);
    timers.push(setInterval(async()=>{
        if(reconciling||stopRequested)return;reconciling=true;
        try{const result=await manager.reconcile();if(result.conflicts?.length)requestStop('检测到手工修改的 Steam 域名映射，正在停止接管…',true);}
        catch{requestStop('无法维护下载映射，正在恢复设置…',true);}finally{reconciling=false;}
    },2000));
    await stopPromise;
}catch(error){
    if(error.code!=='CANCELLED'){
        fatal=true;
        const reasons={EADDRINUSE:'下载端口仍被其他程序占用。',EACCES:'无法写入转发设置，请使用管理员权限启动。',EPERM:'无法写入转发设置，请使用管理员权限启动。',HOSTS_CONFLICT:'检测到已有手工下载映射，已停止接管。',OTHER_PLATFORM_RUNNING:'原助手正在运行其他平台，请先停止该平台的服务。'};
        message=reasons[error.code]||'自动优选未能启动，正在恢复原设置。';event(message);
    }
}finally{
    for(const timer of timers)clearInterval(timer);
    while(reconciling)await new Promise(r=>setTimeout(r,50));
    const finalMessage=message;
    try{
        // Restore only if this launch acquired a journal or is recovering it.
        if(hasAcquired||attemptedAcquire||recoverOnly)await restore();else await engine?.close();
        state=fatal?'error':'stopped';message=fatal?finalMessage:'自动优选已停止，Steam 转发设置已恢复。';
    }catch{
        state='error';message='恢复尚未完成，请使用“停止Steam自动优选”再次恢复。';
    }
    await writeStatus(true).catch(()=>{});
}
