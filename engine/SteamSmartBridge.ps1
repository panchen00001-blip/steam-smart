param([ValidateSet('Status','StopLegacy','StartLegacy')][string]$Action='Status')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SteamLegacyWindow {
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int cmd);
}
'@
$apps=@(Get-Process | Where-Object {$_.ProcessName -like 'UsbEAm*' -and $_.MainWindowHandle -ne 0})
if($apps.Count -eq 0){[pscustomobject]@{Exists=$false;WasRunning=$false;Running=$false;Restored=$false}|ConvertTo-Json -Compress;return}
if($apps.Count -ne 1){throw '发现多个下载助手窗口，请只保留一个。'}
$app=$apps[0]
$wasMinimized=[SteamLegacyWindow]::IsIconic($app.MainWindowHandle)
try {
    if($wasMinimized){[SteamLegacyWindow]::ShowWindow($app.MainWindowHandle,4)|Out-Null;Start-Sleep -Milliseconds 250}
    $window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    function Read-LegacyElements {
        @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition))
    }
    $all=Read-LegacyElements
    $isSteam=@($all|Where-Object {$_.Current.Name -eq 'Steam' -or $_.Current.Name -match '^1 选择平台 Steam(?:\s|$)'}).Count -gt 0
    $stop=@($all|Where-Object {$_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.Name -eq '停止服务'})
    $wasRunning=$stop.Count -eq 1
    $running=$wasRunning
    $restored=$false
    if($Action -eq 'StopLegacy' -and $wasRunning){
        if(!$isSteam){throw '原助手当前正在运行其他平台，请先停止该平台的转发。'}
        ([System.Windows.Automation.InvokePattern]$stop[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        for($i=0;$i -lt 20;$i++){
            Start-Sleep -Milliseconds 150
            $all=Read-LegacyElements
            if(@($all|Where-Object {$_.Current.Name -eq '启动服务'}).Count -gt 0){$running=$false;break}
        }
        if($running){throw '原助手尚未停止 Steam 转发。'}
    }
    if($Action -eq 'StartLegacy'){
        if($running -and $isSteam){$restored=$true}
        elseif($isSteam){
            $start=@($all|Where-Object {$_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.Name -eq '启动服务' -and $_.Current.IsEnabled})
            if($start.Count -eq 1){
                ([System.Windows.Automation.InvokePattern]$start[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                for($i=0;$i -lt 30;$i++){
                    Start-Sleep -Milliseconds 150
                    if(@((Read-LegacyElements)|Where-Object {$_.Current.Name -eq '停止服务'}).Count -gt 0){$running=$true;$restored=$true;break}
                }
            }
        }
    }
    [pscustomobject]@{Exists=$true;ProcessId=$app.Id;StartedAt=$app.StartTime.ToUniversalTime().ToString('o');Steam=$isSteam;WasRunning=$wasRunning;Running=$running;Restored=$restored}|ConvertTo-Json -Compress
} finally {
    if($wasMinimized -and (Get-Process -Id $app.Id -ErrorAction SilentlyContinue)){
        [SteamLegacyWindow]::ShowWindow($app.MainWindowHandle,7)|Out-Null
    }
}
