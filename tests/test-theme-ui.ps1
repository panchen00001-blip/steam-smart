param(
    [string]$ExePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\Steam自动优选.exe'),
    [string]$EngineDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\build\payload')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$utf8 = New-Object Text.UTF8Encoding($false)
$fixture = Join-Path $PSScriptRoot ('theme-ui-fixture-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$images = Join-Path $fixture 'renders'
[void][IO.Directory]::CreateDirectory($images)
$assertions = New-Object Collections.ArrayList
$screenshots = New-Object Collections.ArrayList
$globalHosts = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$globalHash = (Get-FileHash -LiteralPath $globalHosts -Algorithm SHA256).Hash
$fixtureHosts = Join-Path $fixture 'hosts.fixture'
[IO.File]::WriteAllText($fixtureHosts, "# isolated theme QA; no forwarding is started`r`n", $utf8)
$fixtureHash = (Get-FileHash -LiteralPath $fixtureHosts -Algorithm SHA256).Hash
$config = Get-Content -LiteralPath (Join-Path $EngineDirectory 'smart-config.defaults.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$config.hostsPath = $fixtureHosts
$config.listen.host = '127.0.0.1'
$config.listen.port = 27991
$config | Add-Member -NotePropertyName skipLegacy -NotePropertyValue $true -Force
$config | Add-Member -NotePropertyName skipWarmup -NotePropertyValue $true -Force
[IO.File]::WriteAllText((Join-Path $fixture 'smart-config.json'), ($config | ConvertTo-Json -Depth 30), $utf8)
$configHash = (Get-FileHash -LiteralPath (Join-Path $fixture 'smart-config.json') -Algorithm SHA256).Hash
$assembly = [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($ExePath))
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$controllerType = $assembly.GetType('SteamSmart.Desktop.RuntimeController', $true)
$windowType = $assembly.GetType('SteamSmart.Desktop.MainWindow', $true)
$programType = $assembly.GetType('SteamSmart.Desktop.Program', $true)
$context = New-Object Windows.Threading.DispatcherSynchronizationContext([Windows.Threading.Dispatcher]::CurrentDispatcher)
[Threading.SynchronizationContext]::SetSynchronizationContext($context)
$script:controller = $null
$script:window = $null
$script:view = $null
$script:theme = $null
$failure = ''

function Assert-Theme([string]$Name, [bool]$Value) {
    [void]$assertions.Add([pscustomobject]@{ name=$Name; passed=$Value })
    if (!$Value) { throw ('Assertion failed: ' + $Name) }
}
function Pump-Theme {
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Windows.Threading.DispatcherPriority]::Background, [Action]{})
    Start-Sleep -Milliseconds 25
}
function Read-ThemeSetting {
    return (Get-Content -LiteralPath (Join-Path $fixture 'ui-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json).theme
}
function Open-ThemeFixture {
    $script:controller = $controllerType.GetConstructor([type[]]@([string],[string])).Invoke([object[]]@([string]$EngineDirectory,[string]$fixture))
    $script:window = $windowType.GetConstructor([type[]]@($controllerType)).Invoke([object[]]@($script:controller))
    $script:view = $windowType.GetField('view', $flags).GetValue($script:window)
    $script:theme = $windowType.GetField('theme', $flags).GetValue($script:window)
    # The window remains unshown: Loaded never runs and no CLI or daemon is started.
    Assert-Theme 'program_autostart_false_in_reflection_fixture' (!$programType.GetProperty('AutoStart').GetValue($null, $null))
    Assert-Theme 'fixture_window_not_shown' (!$script:window.IsVisible)
    $model = [Activator]::CreateInstance($assembly.GetType('SteamSmart.Desktop.ConfigView'))
    foreach ($group in @($config.groups)) {
        $option = [Activator]::CreateInstance($assembly.GetType('SteamSmart.Desktop.SourceOption'))
        $option.id=$group.id; $option.name=$group.name; $option.target=$group.target
        $model.groups.Add($option)
        foreach ($domain in @($group.hosts)) {
            $row = [Activator]::CreateInstance($assembly.GetType('SteamSmart.Desktop.DomainRow'))
            $row.domain=$domain; $row.groupId=$group.id; $row.kind='download'
            $model.rows.Add($row)
        }
    }
    foreach ($redirect in @($config.redirects.PSObject.Properties)) {
        $group = @($config.groups | Where-Object { @($_.hosts) -contains $redirect.Value })[0]
        $row = [Activator]::CreateInstance($assembly.GetType('SteamSmart.Desktop.DomainRow'))
        $row.domain=$redirect.Name; $row.groupId=$group.id; $row.kind='redirect'
        $model.rows.Add($row)
    }
    $windowType.GetMethod('ReplaceDomains', $flags).Invoke($script:window, [object[]]@($model, $false)) | Out-Null
    Assert-Theme 'fixture_domain_rows_renderable' ($script:view.FindName('DomainsGrid').Items.Count -ge 20)
}
function Close-ThemeFixture {
    if ($script:window) {
        $windowType.GetField('allowClose', $flags).SetValue($script:window, $true)
        $script:window.Close()
        $script:window=$null
    }
    if ($script:controller) { $script:controller.Dispose(); $script:controller=$null }
}
function Select-ThemeMode([string]$Mode) {
    $control = $script:view.FindName('ThemeMode')
    $item = @($control.Items | Where-Object { $_.Tag -eq $Mode })
    Assert-Theme ('theme_option_exists_' + $Mode) ($item.Count -eq 1)
    $control.SelectedItem = $item[0]
    Pump-Theme
    Assert-Theme ('theme_mode_selected_' + $Mode) ($script:theme.Mode -eq $Mode)
}
function Get-ThemeLuminance($Color) {
    $rgb = @($Color.R,$Color.G,$Color.B) | ForEach-Object {
        $v = $_ / 255.0
        if ($v -le 0.04045) { $v / 12.92 } else { [Math]::Pow((($v + 0.055) / 1.055), 2.4) }
    }
    return 0.2126*$rgb[0] + 0.7152*$rgb[1] + 0.0722*$rgb[2]
}
function Get-ThemeContrast([string]$Foreground, [string]$Background) {
    $a = Get-ThemeLuminance $script:view.FindResource($Foreground).Color
    $b = Get-ThemeLuminance $script:view.FindResource($Background).Color
    return ([Math]::Max($a,$b)+0.05) / ([Math]::Min($a,$b)+0.05)
}
function Save-ThemeRender([string]$Mode,[string]$Page,[string]$Size,[int]$Width,[int]$Height) {
    $script:window.SelectPage($Page)
    $script:view.Measure((New-Object Windows.Size($Width,$Height)))
    $script:view.Arrange((New-Object Windows.Rect(0,0,$Width,$Height)))
    $script:view.UpdateLayout()
    Pump-Theme
    $script:view.UpdateLayout()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($Width,$Height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($script:view)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $path=Join-Path $images ($Mode+'-'+$Page+'-'+$Size+'.png')
    $stream=[IO.File]::Create($path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    [void]$screenshots.Add($path)
    $selector=$script:view.FindName('ThemeMode')
    $point=$selector.TransformToAncestor($script:view).Transform((New-Object Windows.Point(0,0)))
    Assert-Theme ('theme_selector_visible_'+$Mode+'_'+$Page+'_'+$Size) ($point.X -ge 0 -and $point.Y -ge 0 -and $point.X+$selector.ActualWidth -le $Width+1 -and $point.Y+$selector.ActualHeight -le $Height+1)
    if ($Page -eq 'domains') {
        $button=$script:view.FindName('ApplyDomainsButton')
        $point=$button.TransformToAncestor($script:view).Transform((New-Object Windows.Point(0,0)))
        Assert-Theme ('domain_apply_inside_viewport_'+$Mode+'_'+$Size) ($point.Y+$button.ActualHeight -le $Height+1)
        if ($Size -eq 'states') {
            $grid=$script:view.FindName('DomainsGrid')
            $grid.SelectedItem=$grid.Items[0]
            $grid.CurrentCell=New-Object Windows.Controls.DataGridCellInfo($grid.Items[0],$grid.Columns[0])
            $editing=$grid.BeginEdit()
            Assert-Theme ('domain_edit_can_begin_'+$Mode) $editing
            $grid.UpdateLayout()
            Save-ThemeRender $Mode 'domains' 'editing' $Width $Height
            [void]$grid.CancelEdit()
            [void]$grid.CancelEdit()
        }
    }
}
function Save-ThemePopup([string]$Mode) {
    $combo=$script:view.FindName('ThemeMode')
    [void]$combo.ApplyTemplate()
    $surface=$combo.Template.FindName('PopupSurface',$combo)
    Assert-Theme ('theme_popup_available_'+$Mode) ($null -ne $surface)
    Assert-Theme ('theme_popup_surface_matches_'+$Mode) ($surface.Background.Color -eq $script:view.FindResource('Surface').Color)
    $surface.Measure((New-Object Windows.Size(180,220)))
    $width=[int][Math]::Ceiling([Math]::Max(180,$surface.DesiredSize.Width))
    $height=[int][Math]::Ceiling([Math]::Max(120,$surface.DesiredSize.Height))
    $surface.Arrange((New-Object Windows.Rect(0,0,$width,$height)))
    $surface.UpdateLayout()
    $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($surface)
    $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $path=Join-Path $images ($Mode+'-theme-popup.png')
    $stream=[IO.File]::Create($path)
    try {$encoder.Save($stream)} finally {$stream.Dispose()}
    [void]$screenshots.Add($path)
}
function Add-ThemeStateSamples {
    $status=[Activator]::CreateInstance($assembly.GetType('SteamSmart.Desktop.StatusSnapshot'))
    $status.state='error';$status.message='isolated visual test';$status.totalRateBps=31457280;$status.totalBytes=4294967296
    $group=[Activator]::CreateInstance($assembly.GetType('SteamSmart.Desktop.GroupSnapshot'))
    $group.id='steam_xinliu';$group.name='新流云1';$group.rateBps=31457280
    foreach ($selected in @($true,$false)) {
        $ip=[Activator]::CreateInstance($assembly.GetType('SteamSmart.Desktop.IpSnapshot'))
        $ip.ip=if($selected){'192.0.2.10'}else{'192.0.2.11'}
        $ip.selected=$selected;$ip.valid=$true;$ip.state='valid';$ip.scoreMbps=250;$ip.rateBps=if($selected){31457280}else{0}
        $group.ips.Add($ip)
    }
    $status.groups.Add($group)
    $entry=[Activator]::CreateInstance($assembly.GetType('SteamSmart.Desktop.EventSnapshot'))
    $entry.at=[DateTimeOffset]::Now.ToString('o');$entry.message='已验证当前下载来源的节点（离屏界面示例，不执行测速）。'
    $status.events.Add($entry)
    $controllerType.GetField('_status',$flags).SetValue($script:controller,$status)
    $windowType.GetMethod('RefreshStatus',$flags).Invoke($script:window,@()) | Out-Null
    $windowType.GetMethod('ShowRuntimeMessage',$flags).Invoke($script:window,[object[]]@('错误提示样式测试（隔离界面）',$true,$true)) | Out-Null
    $windowType.GetMethod('DomainFeedback',$flags).Invoke($script:window,[object[]]@('域名校验错误样式测试（隔离界面）',$true)) | Out-Null
}

try {
    Open-ThemeFixture
    Assert-Theme 'new_install_defaults_to_dark' ($script:theme.Mode -eq 'dark' -and $script:theme.IsDark)
    foreach ($mode in @('light','dark')) {
        Select-ThemeMode $mode
        Assert-Theme ('theme_setting_persisted_'+$mode) ((Read-ThemeSetting) -eq $mode)
        Assert-Theme ('is_dark_matches_'+$mode) ($script:theme.IsDark -eq ($mode -eq 'dark'))
        $background=$script:view.FindResource('PageBackground').Color
        Assert-Theme ('page_background_matches_'+$mode) (($mode -eq 'dark' -and $background.R -lt 80) -or ($mode -eq 'light' -and $background.R -gt 200))
        Assert-Theme ('primary_text_contrast_'+$mode) ((Get-ThemeContrast 'TextPrimary' 'Surface') -ge 4.5)
        foreach ($page in @('overview','domains','logs','help')) { Save-ThemeRender $mode $page 'default' 1184 761 }
        foreach ($page in @('overview','domains')) { Save-ThemeRender $mode $page 'minimum' 1044 661 }
        Save-ThemePopup $mode
    }
    Add-ThemeStateSamples
    foreach ($mode in @('light','dark')) {
        Select-ThemeMode $mode
        Assert-Theme ('runtime_error_remembered_after_switch_'+$mode) ($script:view.FindName('RuntimeMessage').Text -eq '错误提示样式测试（隔离界面）')
        Assert-Theme ('runtime_error_color_after_switch_'+$mode) ($script:view.FindName('RuntimeMessage').Foreground.Color -eq $script:view.FindResource('Danger').Color)
        Assert-Theme ('runtime_error_surface_after_switch_'+$mode) ($script:view.FindName('MessageBar').Background.Color -eq $script:view.FindResource('DangerSurface').Color)
        Assert-Theme ('domain_error_color_after_switch_'+$mode) ($script:view.FindName('DomainFeedback').Foreground.Color -eq $script:view.FindResource('Danger').Color)
        $preferred=@($script:view.FindName('NodesGrid').Items | Where-Object {$_.Preferred})[0]
        Assert-Theme ('success_node_surface_after_switch_'+$mode) ($preferred.StatusBackground.Color -eq $script:view.FindResource('SuccessSurface').Color)
        Assert-Theme ('success_node_text_after_switch_'+$mode) ($preferred.StatusForeground.Color -eq $script:view.FindResource('Success').Color)
        foreach($page in @('overview','domains','logs')) {Save-ThemeRender $mode $page 'states' 1184 761}
    }
    Select-ThemeMode 'light'
    Close-ThemeFixture
    Open-ThemeFixture
    Assert-Theme 'light_choice_survives_window_restart' ($script:theme.Mode -eq 'light' -and !$script:theme.IsDark)
    Select-ThemeMode 'system'
    Assert-Theme 'system_choice_persisted' ((Read-ThemeSetting) -eq 'system')
    Close-ThemeFixture
    Open-ThemeFixture
    Assert-Theme 'system_choice_survives_window_restart' ($script:theme.Mode -eq 'system')
    Select-ThemeMode 'dark'
    Close-ThemeFixture
    Assert-Theme 'global_hosts_unchanged' ((Get-FileHash -LiteralPath $globalHosts -Algorithm SHA256).Hash -eq $globalHash)
    Assert-Theme 'fixture_hosts_unchanged' ((Get-FileHash -LiteralPath $fixtureHosts -Algorithm SHA256).Hash -eq $fixtureHash)
    Assert-Theme 'download_config_unchanged' ((Get-FileHash -LiteralPath (Join-Path $fixture 'smart-config.json') -Algorithm SHA256).Hash -eq $configHash)
    $fixtureNodes=@(Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($fixture) })
    Assert-Theme 'no_fixture_node_processes_started' ($fixtureNodes.Count -eq 0)
} catch {
    $failure=$_.Exception.Message
    [IO.File]::WriteAllText((Join-Path $fixture 'diagnostic.txt'),($_ | Out-String),$utf8)
} finally { Close-ThemeFixture }
$report=[pscustomobject]@{ok=(!$failure);failure=$failure;assertions=@($assertions);screenshots=@($screenshots);fixture=$fixture;executable=[IO.Path]::GetFullPath($ExePath)}
[IO.File]::WriteAllText((Join-Path $fixture 'theme-result.json'),($report | ConvertTo-Json -Depth 8),$utf8)
$report | ConvertTo-Json -Depth 8
if ($failure) { exit 1 }
