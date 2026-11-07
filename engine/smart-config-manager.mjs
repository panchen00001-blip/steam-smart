import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { randomUUID } from 'node:crypto';

export const MAX_DOMAIN_ROWS = 256;
export const MAX_CONFIG_BYTES = 1024 * 1024;
export const MAX_ROWS_BYTES = 256 * 1024;

/** These messages contain no input values, filesystem paths or engine internals. */
export class ConfigError extends Error {
  constructor(message) { super(message); this.name = 'ConfigError'; }
}

const invalid = message => { throw new ConfigError(message); };
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);

function domainName(value) {
  if (typeof value !== 'string' || value.length < 3 || value.length > 253 ||
      !/^[a-zA-Z0-9.-]+$/.test(value) || net.isIP(value)) {
    invalid('域名格式无效，请仅填写完整域名，不要填写网址、IP、端口或空格。');
  }
  const domain = value.toLowerCase(), labels = domain.split('.');
  if (labels.length < 2 || labels.some(label => label.length > 63 ||
      !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(label)) ||
      !/[a-z]/.test(labels.at(-1))) {
    invalid('域名格式无效，请检查域名中的点、连字符和后缀。');
  }
  return domain;
}

function upstreamHostname(target) {
  if (typeof target !== 'string' || !target || target.length > 2048 || /\s/.test(target)) {
    invalid('下载源的上游地址无效，请恢复默认配置。');
  }
  let url;
  try { url = new URL(target.includes('://') ? target : `http://${target}`); }
  catch { invalid('下载源的上游地址无效，请恢复默认配置。'); }
  if (url.protocol !== 'http:' || url.username || url.password ||
      url.pathname !== '/' || url.search || url.hash || !url.hostname ||
      (url.port && (Number(url.port) < 1 || Number(url.port) > 65535))) {
    invalid('下载源的上游地址无效，请恢复默认配置。');
  }
  const hostname = url.hostname.replace(/^\[|\]$/g, '').toLowerCase();
  // Literal upstream IPs are supported by the engine; only mapped domains need DNS-loop checks.
  if (!net.isIP(hostname)) domainName(hostname);
  return hostname;
}

/** Normalize without discarding inactive source metadata. Never mutate the input. */
function normalizeConfig(config) {
  if (!record(config) || !Array.isArray(config.groups) || !config.groups.length ||
      config.groups.length > MAX_DOMAIN_ROWS) {
    invalid('配置缺少有效的下载源列表，请恢复默认配置。');
  }
  const ids = new Set(), upstreams = new Set();
  const groups = config.groups.map(group => {
    if (!record(group) || typeof group.id !== 'string' || !group.id ||
        group.id.length > 128 || /\s/.test(group.id) || ids.has(group.id) ||
        !Array.isArray(group.hosts) || (group.name !== undefined && typeof group.name !== 'string')) {
      invalid('下载源信息无效或重复，请恢复默认配置。');
    }
    ids.add(group.id);
    upstreams.add(upstreamHostname(group.target));
    if (group.hosts.length > MAX_DOMAIN_ROWS) invalid('域名数量不能超过 256 条。');
    return { ...group, hosts: group.hosts.map(domainName) };
  });
  if (config.redirects !== undefined && !record(config.redirects)) {
    invalid('转发域名配置格式无效，请恢复默认配置。');
  }
  const entries = Object.entries(config.redirects || {});
  if (groups.reduce((total, group) => total + group.hosts.length, 0) + entries.length > MAX_DOMAIN_ROWS) {
    invalid('域名数量不能超过 256 条。');
  }
  const mapped = new Set(), downloads = new Set();
  const claim = domain => {
    if (mapped.has(domain)) invalid('域名重复：每个域名只能出现一次。');
    if (upstreams.has(domain)) invalid('不能添加下载源自身的上游域名，以免产生解析循环。');
    mapped.add(domain);
  };
  for (const group of groups) {
    for (const domain of group.hosts) { claim(domain); downloads.add(domain); }
  }
  if (!downloads.size) invalid('请至少保留一个下载域名。');
  const redirects = Object.fromEntries(entries.map(([from, to]) => {
    const domain = domainName(from), destination = domainName(to);
    claim(domain);
    if (!downloads.has(destination)) invalid('转发域名必须关联到当前配置中的下载域名。');
    return [domain, destination];
  }));
  return { ...config, groups, redirects };
}

/** Return the effective config accepted by the engine and hosts manager. */
export function validateConfig(config) {
  const normalized = normalizeConfig(config);
  return { ...normalized, groups: normalized.groups.filter(group => group.hosts.length > 0) };
}

/** Group order is stable; each source's downloads precede its redirect aliases. */
export function buildDomainRows(config) {
  const normalized = normalizeConfig(config);
  const aliases = new Map(normalized.groups.map(group => [group.id, []]));
  const hostGroups = new Map(normalized.groups.flatMap(group => group.hosts.map(host => [host, group.id])));
  for (const [domain, destination] of Object.entries(normalized.redirects)) {
    aliases.get(hostGroups.get(destination)).push({ domain, groupId: hostGroups.get(destination), kind: 'redirect' });
  }
  return normalized.groups.flatMap(group => [
    ...group.hosts.map(domain => ({ domain, groupId: group.id, kind: 'download' })),
    ...aliases.get(group.id),
  ]);
}

/** Rebuild editable mappings, retaining all source metadata, including inactive groups. */
export function applyDomainRows(config, rows) {
  const original = normalizeConfig(config);
  if (!Array.isArray(rows) || !rows.length) invalid('域名列表不能为空，请至少保留一个下载域名。');
  if (rows.length > MAX_DOMAIN_ROWS) invalid('域名数量不能超过 256 条。');
  const groups = original.groups.map(group => ({ ...group, hosts: [] }));
  const byId = new Map(groups.map(group => [group.id, group]));
  const seen = new Set();
  const normalizedRows = rows.map(row => {
    if (!record(row) || (row.kind !== 'download' && row.kind !== 'redirect')) {
      invalid('域名类型无效，请选择下载域名或转发域名。');
    }
    if (typeof row.groupId !== 'string' || !byId.has(row.groupId)) {
      invalid('请选择有效的下载源。');
    }
    const domain = domainName(row.domain);
    if (seen.has(domain)) invalid('域名重复：每个域名只能出现一次。');
    seen.add(domain);
    if (row.kind === 'download') byId.get(row.groupId).hosts.push(domain);
    return { domain, groupId: row.groupId, kind: row.kind };
  });
  const redirects = Object.fromEntries(normalizedRows.filter(row => row.kind === 'redirect').map(row => {
    const hosts = byId.get(row.groupId).hosts;
    if (!hosts.length) invalid('转发域名所选的下载源至少需要一个下载域名。');
    const previous = original.redirects[row.domain];
    return [row.domain, hosts.includes(previous) ? previous : hosts[0]];
  }));
  // normalizeConfig validates the final dependency graph and upstream exclusions.
  return normalizeConfig({ ...original, groups, redirects });
}

export function configView(config) {
  const normalized = normalizeConfig(config);
  return {
    groups: normalized.groups.map(group => ({ id: group.id, name: group.name || group.id, target: group.target })),
    rows: buildDomainRows(normalized),
  };
}

function decodeJson(bytes) {
  let text;
  if (bytes[0] === 0xff && bytes[1] === 0xfe) text = bytes.subarray(2).toString('utf16le');
  else if (bytes[0] === 0xfe && bytes[1] === 0xff) {
    if (bytes.length % 2) invalid('JSON 文件格式无效，请检查内容后重试。');
    text = Buffer.from(bytes.subarray(2)).swap16().toString('utf16le');
  } else text = bytes.toString('utf8').replace(/^\uFEFF/, '');
  try { return JSON.parse(text); }
  catch { invalid('JSON 文件格式无效，请检查内容后重试。'); }
}

/** A bounded read also protects against a file growing after stat(). */
async function readBytes(file, maximum) {
  const handle = await fs.open(file, 'r');
  try {
    const info = await handle.stat();
    if (!info.isFile()) invalid('配置必须是普通 JSON 文件。');
    if (info.size > maximum) invalid('配置文件过大，请减少域名或恢复默认配置。');
    const buffer = Buffer.alloc(maximum + 1);
    let length = 0;
    while (length <= maximum) {
      const { bytesRead } = await handle.read(buffer, length, buffer.length - length, null);
      if (!bytesRead) break;
      length += bytesRead;
    }
    if (length > maximum) invalid('配置文件过大，请减少域名或恢复默认配置。');
    return buffer.subarray(0, length);
  } finally { await handle.close(); }
}

export async function readJsonFile(file, maximum = MAX_CONFIG_BYTES) {
  return decodeJson(await readBytes(file, maximum));
}

export async function readConfigFile(file) {
  return normalizeConfig(await readJsonFile(file));
}

async function stageFile(file, bytes) {
  const handle = await fs.open(file, 'wx', 0o600);
  try { await handle.writeFile(bytes); await handle.sync(); }
  finally { await handle.close(); }
}

/** Validate completely before staging, then atomically replace backup and config. */
export async function saveDomainRows(file, rows) {
  const resolved = path.resolve(file);
  const previous = path.join(path.dirname(resolved), 'smart-config.previous.json');
  if (resolved.toLowerCase() === previous.toLowerCase()) invalid('不能将备份文件用作当前配置。');
  // Serialize writers without opening/truncating any existing config or backup.
  const lock = `${resolved}.save.lock`;
  let lockHandle;
  try { lockHandle = await fs.open(lock, 'wx', 0o600); }
  catch (error) {
    if (error.code === 'EEXIST') invalid('配置正在保存，请稍后重试。');
    throw error;
  }
  const nonce = `${process.pid}.${randomUUID()}`;
  const configTemporary = `${resolved}.${nonce}.tmp`;
  const backupTemporary = `${previous}.${nonce}.tmp`;
  try {
    const originalBytes = await readBytes(resolved, MAX_CONFIG_BYTES);
    const config = applyDomainRows(decodeJson(originalBytes), rows);
    const nextBytes = Buffer.from(`${JSON.stringify(config, null, 2)}\n`, 'utf8');
    if (nextBytes.length > MAX_CONFIG_BYTES) invalid('配置文件过大，请减少域名或恢复默认配置。');
    await stageFile(configTemporary, nextBytes);
    await stageFile(backupTemporary, originalBytes);
    const currentBytes = await readBytes(resolved, MAX_CONFIG_BYTES);
    if (!currentBytes.equals(originalBytes)) invalid('配置已被其他程序修改，请重新打开域名设置。');
    await fs.rename(backupTemporary, previous);
    await fs.rename(configTemporary, resolved);
    return config;
  } finally {
    await fs.unlink(configTemporary).catch(() => {});
    await fs.unlink(backupTemporary).catch(() => {});
    await lockHandle.close();
    await fs.unlink(lock).catch(() => {});
  }
}

export function safeConfigError(error) {
  if (error instanceof ConfigError) return error.message;
  if (error?.code === 'ENOENT') return '配置文件不存在，请恢复默认配置或重新启动程序。';
  if (['EACCES', 'EPERM', 'EBUSY', 'EISDIR', 'ENOTDIR'].includes(error?.code)) {
    return '无法读写配置文件，请关闭占用文件的程序后重试。';
  }
  if (error?.code === 'ENOSPC') return '磁盘空间不足，配置未保存，请清理后重试。';
  return '配置操作失败，请检查配置后重试。';
}
