param([string]$ExePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\Steam自动优选.exe'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeExeSmokeWindow {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int command);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int cx,int cy,uint flags);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[void][NativeExeSmokeWindow]::SetProcessDPIAware()
$script:assertions=New-Object System.Collections.ArrayList
$script:owned=New-Object System.Collections.ArrayList
$script:ownedHandles=@{}
$script:fixture=$null
$script:stage='prepare'
$script:failure=''
$script:complete=$false
$script:cleanupComplete=$true
$script:configOriginal=''
$script:hostsOriginal=''
$utf8=New-Object Text.UTF8Encoding($false)

function Assert-Native([string]$Name,[bool]$Condition) {
    [void]$script:assertions.Add([pscustomobject]@{name=$Name;passed=$Condition})
    if(!$Condition){throw ('Assertion failed: '+$Name)}
}
function Wait-Native([scriptblock]$Predicate,[string]$Name,[int]$Seconds=20) {
    $deadline=(Get-Date).AddSeconds($Seconds)
    do {
        $value=&$Predicate
        if($value){return $value}
        Start-Sleep -Milliseconds 160
    }while((Get-Date)-lt$deadline)
    throw ('Timed out: '+$Name)
}
function Read-NativeStatus {
    try{return Get-Content -LiteralPath (Join-Path $script:fixture 'data\runtime\status.json') -Raw -Encoding UTF8 | ConvertFrom-Json}catch{return $null}
}
function Test-NativeAlive([int]$ProcessId) {
    try{$p=Get-Process -Id $ProcessId -ErrorAction Stop;return !$p.HasExited}catch{return $false}
}
function Test-NativeHostsOriginal {
    return ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $script:fixture 'hosts.fixture')))-eq$script:hostsOriginal)
}
function Find-NativeWindow([Diagnostics.Process]$Process) {
    $Process.Refresh()
    if($Process.HasExited){return $null}
    $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$Process.Id)
    $window=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
    if($window){$script:ownedHandles[$Process.Id]=[IntPtr]$window.Current.NativeWindowHandle}
    return $window
}
function Find-NativeControl($Window,[string]$Id) {
    $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
}
function Click-Native($Window,[string]$Id) {
    $control=Find-NativeControl $Window $Id
    if(!$control){throw ('Named control unavailable: '+$Id)}
    if(!$control.Current.IsEnabled){throw ('Named control disabled: '+$Id)}
    $pattern=$null
    if($control.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){([System.Windows.Automation.InvokePattern]$pattern).Invoke();return}
    if($control.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)){$control.SetFocus();[System.Windows.Forms.SendKeys]::SendWait(" ");return}
    throw ('Named control has no click pattern: '+$Id)
}
function Start-Native([switch]$StopOnly) {
    $exe=Join-Path $script:fixture 'Steam自动优选.exe'
    $arguments='--data-dir "'+(Join-Path $script:fixture 'data')+'"'
    if($StopOnly){$arguments+=' --stop'}
    $process=Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $script:fixture -WindowStyle Hidden -PassThru
    [void]$script:owned.Add($process)
    return $process
}
function Save-NativeScreenshot($Window,[string]$Name) {
    $hwnd=[IntPtr]$Window.Current.NativeWindowHandle
    [void][NativeExeSmokeWindow]::ShowWindow($hwnd,9)
    [void][NativeExeSmokeWindow]::SetWindowPos($hwnd,[IntPtr](-1),0,0,0,0,0x0043)
    [void][NativeExeSmokeWindow]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 450
    $bounds=$Window.Current.BoundingRectangle
    $left=[int][Math]::Round($bounds.Left);$top=[int][Math]::Round($bounds.Top)
    $width=[int][Math]::Round($bounds.Width);$height=[int][Math]::Round($bounds.Height)
    $screen=[System.Windows.Forms.SystemInformation]::VirtualScreen
    Assert-Native ('screenshot_'+$Name+'_contains_only_owned_window') ($width-gt300-and$height-gt200-and$left-ge$screen.Left-and$top-ge$screen.Top-and($left+$width)-le$screen.Right-and($top+$height)-le$screen.Bottom)
    $bitmap=New-Object Drawing.Bitmap($width,$height)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try{
        $graphics.CopyFromScreen($left,$top,0,0,(New-Object Drawing.Size($width,$height)))
        $bitmap.Save((Join-Path $script:fixture ($Name+'.png')),[Drawing.Imaging.ImageFormat]::Png)
    }finally{
        $graphics.Dispose();$bitmap.Dispose()
        [void][NativeExeSmokeWindow]::SetWindowPos($hwnd,[IntPtr](-2),0,0,0,0,0x0003)
    }
}
function Stop-NativeFixtures {
    if(!$script:fixture){return}
    foreach($process in @($script:owned)){
        try{
            $process.Refresh()
            if($process.HasExited){continue}
            if($script:ownedHandles.ContainsKey($process.Id)){
                [void][NativeExeSmokeWindow]::PostMessage($script:ownedHandles[$process.Id],0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
            }else{[void]$process.CloseMainWindow()}
        }catch{}
    }
    $deadline=(Get-Date).AddSeconds(10)
    do{
        $alive=@($script:owned|Where-Object{try{$_.Refresh();!$_.HasExited}catch{$false}})
        if(!$alive.Count){break}
        Start-Sleep -Milliseconds 200
    }while((Get-Date)-lt$deadline)
    $nodes=@(Get-CimInstance Win32_Process -Filter "Name = 'node.exe'"|Where-Object{$_.CommandLine-and$_.CommandLine.Contains($script:fixture)})
    if($nodes.Count){
        try{$stop=Start-Native -StopOnly;[void]$stop.WaitForExit(20000)}catch{$script:cleanupComplete=$false}
    }
    foreach($process in @($script:owned)){
        try{$process.Refresh();if(!$process.HasExited){[void]$process.CloseMainWindow();[void]$process.WaitForExit(5000);$process.Refresh();if(!$process.HasExited){$script:cleanupComplete=$false}}}catch{}
    }
    $nodes=@(Get-CimInstance Win32_Process -Filter "Name = 'node.exe'"|Where-Object{$_.CommandLine-and$_.CommandLine.Contains($script:fixture)})
    if($nodes.Count){$script:cleanupComplete=$false}
}

try {
    # Do not signal, hide or stop an existing production UI.
    $mutex=New-Object Threading.Mutex($false,'Local\SteamSmartUI')
    $free=$false
    try{try{$free=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$free=$true};if(!$free){throw 'An existing SteamSmart UI owns the application mutex.'}}
    finally{if($free){$mutex.ReleaseMutex()};$mutex.Dispose()}
    $base=[IO.Path]::GetFullPath($PSScriptRoot)
    $script:fixture=[IO.Path]::GetFullPath((Join-Path $base ('native-ui-fixture-'+[Guid]::NewGuid().ToString('N'))))
    if(!$script:fixture.StartsWith($base.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture directory.'}
    [void][IO.Directory]::CreateDirectory($script:fixture)
    [void][IO.Directory]::CreateDirectory((Join-Path $script:fixture 'data'))
    Copy-Item -LiteralPath $ExePath -Destination (Join-Path $script:fixture 'Steam自动优选.exe')
    Assert-Native 'fixture_contains_single_exe_without_loose_engine_files' (@(Get-ChildItem -LiteralPath $script:fixture -File).Count-eq1)
    $settings=Get-Content -LiteralPath (Join-Path (Split-Path -Parent $base) 'engine\smart-config.defaults.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$port=[int]$listener.LocalEndpoint.Port;$listener.Stop()
    $settings.listen=[pscustomobject]@{host='127.0.0.1';port=$port}
    $settings.hostsPath=Join-Path $script:fixture 'hosts.fixture'
    $settings|Add-Member -NotePropertyName skipLegacy -NotePropertyValue $true -Force
    $settings|Add-Member -NotePropertyName skipWarmup -NotePropertyValue $true -Force
    [IO.File]::WriteAllText($settings.hostsPath,"# native EXE UI fixture`r`n192.0.2.9 unrelated.fixture.example # untouched`r`n",$utf8)
    [IO.File]::WriteAllText((Join-Path $script:fixture 'data\smart-config.json'),(ConvertTo-Json -InputObject $settings -Depth 30),$utf8)
    $script:hostsOriginal=[Convert]::ToBase64String([IO.File]::ReadAllBytes($settings.hostsPath))
    $script:configOriginal=[Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $script:fixture 'data\smart-config.json')))
    $script:stage='initial_launch'
    $first=Start-Native
    $window=Wait-Native {Find-NativeWindow $first} 'owned EXE main window' 30
    $initial=Wait-Native {$s=Read-NativeStatus;if($s-and$s.state-eq'running'-and$s.parentPid-eq$first.Id){$s}} 'Loaded automatic startup' 30
    Assert-Native 'loaded_event_automatically_starts_daemon' (Test-NativeAlive $initial.pid)
    Assert-Native 'only_fake_hosts_are_mapped' ([IO.File]::ReadAllText($settings.hostsPath).Contains('127.0.0.1 dl.steam.clngaa.com #SteamAutoOptimizer'))
    Assert-Native 'unrelated_fake_hosts_mapping_preserved' ([IO.File]::ReadAllText($settings.hostsPath).Contains('192.0.2.9 unrelated.fixture.example # untouched'))
    Assert-Native 'initial_no_download_or_probe_traffic' ($initial.totalBytes-eq0-and$initial.probeBytes-eq0)
    [void](Wait-Native {$b=Find-NativeControl $window 'StopButton';$b-and$b.Current.IsEnabled-and!$b.Current.IsOffscreen} 'enabled Stop button')
    Save-NativeScreenshot $window 'overview'
    $script:stage='manual_stop'
    Click-Native $window 'StopButton'
    $stopped=Wait-Native {$s=Read-NativeStatus;if($s-and$s.state-eq'stopped'-and$s.restoreResult.hostsReleased-and!(Test-NativeAlive $s.pid)){$s}} 'manual stop restoration'
    Assert-Native 'manual_stop_restores_fake_hosts_exactly' (Test-NativeHostsOriginal)
    $first.Refresh();Assert-Native 'manual_stop_keeps_exe_window_open' (!$first.HasExited-and[NativeExeSmokeWindow]::IsWindowVisible([IntPtr]$window.Current.NativeWindowHandle))
    [void](Wait-Native {$b=Find-NativeControl $window 'StartButton';$b-and$b.Current.IsEnabled-and!$b.Current.IsOffscreen} 'enabled Start button after stop')
    $script:stage='manual_restart'
    Click-Native $window 'StartButton'
    $again=Wait-Native {$s=Read-NativeStatus;if($s-and$s.state-eq'running'-and$s.runId-ne$initial.runId){$s}} 'restart from same window'
    Assert-Native 'start_button_creates_new_daemon_session' ($again.parentPid-eq$first.Id-and(Test-NativeAlive $again.pid))
    $script:stage='domain_navigation'
    Click-Native $window 'NavDomains'
    $grid=Wait-Native {$g=Find-NativeControl $window 'DomainsGrid';if($g-and!$g.Current.IsOffscreen){$g}} 'domain grid'
    $gridPattern=[System.Windows.Automation.GridPattern]$grid.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern)
    [void](Wait-Native {$gridPattern.Current.RowCount-eq20} '20 domain rules')
    Assert-Native 'domain_navigation_renders_all_20_rules' ($gridPattern.Current.RowCount-eq20)
    Assert-Native 'domain_grid_has_domain_source_and_usage_columns' ($gridPattern.Current.ColumnCount-eq3)
    Save-NativeScreenshot $window 'domains'
    $script:stage='tray_and_second_launch'
    $windowPattern=[System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    [void](Wait-Native {![NativeExeSmokeWindow]::IsWindowVisible($script:ownedHandles[$first.Id])} 'minimize hides window to tray')
    $duringTray=Read-NativeStatus
    Assert-Native 'minimize_to_tray_keeps_daemon_running' ($duringTray.state-eq'running'-and$duringTray.runId-eq$again.runId-and(Test-NativeAlive $duringTray.pid))
    $second=Start-Native
    [void](Wait-Native {$second.Refresh();$second.HasExited} 'second launch exits')
    Assert-Native 'second_launch_exits_without_new_daemon' ($second.ExitCode-eq0-and(Read-NativeStatus).runId-eq$again.runId)
    [void](Wait-Native {[NativeExeSmokeWindow]::IsWindowVisible($script:ownedHandles[$first.Id])} 'second launch shows existing window')
    $window=Find-NativeWindow $first
    Assert-Native 'second_launch_wakes_same_owned_exe_instance' ($window-and$window.Current.ProcessId-eq$first.Id)
    $script:stage='close_x'
    $windowPattern=[System.Windows.Automation.WindowPattern]$window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.Close()
    [void](Wait-Native {$first.Refresh();$first.HasExited} 'window X closes EXE after cleanup')
    $closed=Read-NativeStatus
    Assert-Native 'window_close_exits_exe' $first.HasExited
    Assert-Native 'window_close_confirms_restore_then_exits' ($closed.state-eq'stopped'-and$closed.restoreResult.hostsReleased)
    Assert-Native 'window_close_restores_fake_hosts_exactly' (Test-NativeHostsOriginal)
    Assert-Native 'window_close_leaves_no_daemon' (!(Test-NativeAlive $again.pid))
    $script:stage='parent_watch'
    $crashParent=Start-Native
    $crashWindow=Wait-Native {Find-NativeWindow $crashParent} 'second owned main window'
    $crashState=Wait-Native {$s=Read-NativeStatus;if($s-and$s.state-eq'running'-and$s.parentPid-eq$crashParent.Id){$s}} 'parent-watch fixture starts'
    $ownedIdentity=Get-CimInstance Win32_Process -Filter ('ProcessId = '+$crashParent.Id)
    Assert-Native 'forced_exit_targets_only_owned_fixture_exe' ($ownedIdentity-and[IO.Path]::GetFullPath($ownedIdentity.ExecutablePath)-eq[IO.Path]::GetFullPath((Join-Path $script:fixture 'Steam自动优选.exe')))
    $crashParent.Kill();[void]$crashParent.WaitForExit(5000)
    $watch=Wait-Native {$s=Read-NativeStatus;if($s-and$s.pid-eq$crashState.pid-and$s.state-eq'stopped'-and$s.restoreResult.hostsReleased-and!(Test-NativeAlive $s.pid)){$s}} 'daemon parent watch restores after owned EXE forced exit' 15
    Assert-Native 'parent_watch_stops_daemon_after_forced_exe_exit' (!(Test-NativeAlive $crashState.pid))
    Assert-Native 'parent_watch_restores_fake_hosts_exactly' (Test-NativeHostsOriginal)
    Assert-Native 'entire_ui_smoke_generated_no_download_or_probe_traffic' ($watch.totalBytes-eq0-and$watch.probeBytes-eq0)
    Assert-Native 'ui_smoke_preserves_config_exactly' ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $script:fixture 'data\smart-config.json')))-eq$script:configOriginal)
    $script:complete=$true;$script:stage='complete'
}catch{
    $script:failure='Native EXE smoke failed at stage '+$script:stage+'.'
    if($_.Exception.Message-like'Assertion failed:*'-or$_.Exception.Message-like'Timed out:*'-or$_.Exception.Message-like'Named control*'-or$_.Exception.Message-like'An existing SteamSmart*'){$script:failure=$_.Exception.Message}
    if($script:fixture){[IO.File]::WriteAllText((Join-Path $script:fixture 'harness-diagnostic.txt'),($_|Out-String),$utf8)}
}finally{Stop-NativeFixtures}
$report=[pscustomobject]@{
    ok=($script:complete-and!$script:failure-and$script:cleanupComplete)
    stage=$script:stage;failure=$script:failure;assertions=@($script:assertions)
    fixture=if($script:fixture){[IO.Path]::GetFileName($script:fixture)}else{''}
    fixtureProcessesStopped=$script:cleanupComplete
    exeSha256=if($script:fixture){(Get-FileHash -LiteralPath (Join-Path $script:fixture 'Steam自动优选.exe') -Algorithm SHA256).Hash}else{''}
    screenshots=@('overview.png','domains.png')
}
if($script:fixture){[IO.File]::WriteAllText((Join-Path $script:fixture 'native-ui-result.json'),(ConvertTo-Json -InputObject $report -Depth 8),$utf8)}
ConvertTo-Json -InputObject $report -Depth 8
if(!$report.ok){exit 1}
