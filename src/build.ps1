param(
    [string]$EngineDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'engine'),
    [string]$NodePath = '',
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)
$ErrorActionPreference = 'Stop'
$EngineDirectory = [IO.Path]::GetFullPath($EngineDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $nodeCommand = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$nodeCommand) { throw '没有在 PATH 中找到 Node.js，请安装 Node.js 或通过 -NodePath 指定 node.exe。' }
    $NodePath = $nodeCommand.Source
}
$NodePath = [IO.Path]::GetFullPath($NodePath)
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkDirectory = Split-Path -Parent $compilerPath
$wpfDirectory = Join-Path $frameworkDirectory 'WPF'
$buildDirectory = Join-Path $PSScriptRoot 'build'
$payloadDirectory = Join-Path $buildDirectory 'payload'
$payloadZip = Join-Path $buildDirectory 'SteamSmart.Payload.zip'
$iconPath = Join-Path $PSScriptRoot 'app.ico'
$outputPath = Join-Path $OutputDirectory 'Steam自动优选.exe'
$payloadNames = @(
    'node.exe',
    'smart-daemon.mjs',
    'smart-engine.mjs',
    'hosts-manager.mjs',
    'smart-config-manager.mjs',
    'smart-config-cli.mjs',
    'SteamSmartBridge.ps1',
    'smart-config.defaults.json',
    'smart-config.json'
)
if (!(Test-Path -LiteralPath $compilerPath -PathType Leaf)) { throw '没有找到 .NET Framework C# 编译器。' }
if (!(Test-Path -LiteralPath $NodePath -PathType Leaf)) { throw '没有找到要内置的 Node.js。' }
foreach ($sourceName in @('Program.cs','Bundle.cs','RuntimeController.cs','ThemeManager.cs','MainWindow.xaml','app.manifest','AssemblyInfo.cs')) {
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $sourceName) -PathType Leaf)) { throw "缺少源码：$sourceName" }
}
$windowSources = @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Name -in @('MainWindow.cs','MainWindow.xaml.cs') })
if ($windowSources.Count -eq 0) { throw '缺少窗口代码：MainWindow.cs 或 MainWindow.xaml.cs。' }
foreach ($payloadName in $payloadNames) {
    if ($payloadName -eq 'node.exe') { $payloadSource = $NodePath }
    elseif ($payloadName -eq 'smart-config.json') { $payloadSource = Join-Path $EngineDirectory 'smart-config.defaults.json' }
    else { $payloadSource = Join-Path $EngineDirectory $payloadName }
    if (!(Test-Path -LiteralPath $payloadSource -PathType Leaf)) { throw "缺少运行组件：$payloadName" }
}
foreach ($directoryPath in @($buildDirectory,$payloadDirectory,$OutputDirectory)) {
    if (!(Test-Path -LiteralPath $directoryPath)) { New-Item -ItemType Directory -Path $directoryPath -Force | Out-Null }
    if ((Get-Item -LiteralPath $directoryPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '构建输出目录不允许使用符号链接。' }
}
$unexpectedPayload = @(Get-ChildItem -LiteralPath $payloadDirectory -Force | Where-Object { $_.PSIsContainer -or ($_.Name -notin $payloadNames -and $_.Name -ne 'payload-manifest.json') })
if ($unexpectedPayload.Count -gt 0) { throw '构建目录包含额外文件，请检查 build\payload。' }
foreach ($payloadName in $payloadNames) {
    if ($payloadName -eq 'node.exe') { $payloadSource = $NodePath }
    elseif ($payloadName -eq 'smart-config.json') { $payloadSource = Join-Path $EngineDirectory 'smart-config.defaults.json' }
    else { $payloadSource = Join-Path $EngineDirectory $payloadName }
    Copy-Item -LiteralPath $payloadSource -Destination (Join-Path $payloadDirectory $payloadName) -Force
}
$payloadFiles = @(
    foreach ($payloadName in $payloadNames) {
        $payloadFile = Get-Item -LiteralPath (Join-Path $payloadDirectory $payloadName)
        [ordered]@{
            name = $payloadName
            length = $payloadFile.Length
            sha256 = (Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)
$payloadManifest = [ordered]@{ format=1; version='2.0.0'; files=$payloadFiles } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $payloadDirectory 'payload-manifest.json'), $payloadManifest, (New-Object Text.UTF8Encoding($false)))
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipOutput = [IO.File]::Open($payloadZip, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $zipArchive = New-Object IO.Compression.ZipArchive($zipOutput, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($payloadName in @($payloadNames) + @('payload-manifest.json')) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipArchive, (Join-Path $payloadDirectory $payloadName), $payloadName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zipArchive.Dispose() }
} finally { $zipOutput.Dispose() }

# The app icon is drawn locally from simple shapes at every Windows icon size.
Add-Type -AssemblyName System.Drawing
$iconImages = @()
foreach ($iconSize in @(16,32,48,256)) {
    $bitmap = New-Object Drawing.Bitmap($iconSize,$iconSize)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform(($iconSize / 64.0), ($iconSize / 64.0))
    $background = New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#12263B'))
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(2,2,18,18,180,90)
    $path.AddArc(44,2,18,18,270,90)
    $path.AddArc(44,44,18,18,0,90)
    $path.AddArc(2,44,18,18,90,90)
    $path.CloseFigure()
    $graphics.FillPath($background,$path)
    $arrow = New-Object Drawing.Pen([Drawing.ColorTranslator]::FromHtml('#42D7DE'),5)
    $arrow.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $arrow.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $arrow.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawLine($arrow,32,14,32,40)
    $graphics.DrawLine($arrow,22,31,32,41)
    $graphics.DrawLine($arrow,42,31,32,41)
    $trayPen = New-Object Drawing.Pen([Drawing.ColorTranslator]::FromHtml('#82E6B2'),4)
    $trayPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $trayPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($trayPen,17,46,17,51)
    $graphics.DrawLine($trayPen,17,51,47,51)
    $graphics.DrawLine($trayPen,47,51,47,46)
    $pngStream = New-Object IO.MemoryStream
    $bitmap.Save($pngStream,[Drawing.Imaging.ImageFormat]::Png)
    $iconImages += [pscustomobject]@{ Size=$iconSize; Bytes=$pngStream.ToArray() }
    $pngStream.Dispose()
    $trayPen.Dispose()
    $arrow.Dispose()
    $path.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
$iconStream = [IO.File]::Open($iconPath,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
$iconWriter = New-Object IO.BinaryWriter($iconStream)
try {
    $iconWriter.Write([UInt16]0)
    $iconWriter.Write([UInt16]1)
    $iconWriter.Write([UInt16]$iconImages.Count)
    $iconOffset = 6 + 16 * $iconImages.Count
    foreach ($iconImage in $iconImages) {
        $dimension = if ($iconImage.Size -eq 256) { 0 } else { $iconImage.Size }
        $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]$dimension)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([UInt16]1)
        $iconWriter.Write([UInt16]32)
        $iconWriter.Write([UInt32]$iconImage.Bytes.Length)
        $iconWriter.Write([UInt32]$iconOffset)
        $iconOffset += $iconImage.Bytes.Length
    }
    foreach ($iconImage in $iconImages) { $iconWriter.Write([byte[]]$iconImage.Bytes) }
} finally { $iconWriter.Dispose(); $iconStream.Dispose() }

$references = @(
    (Join-Path $frameworkDirectory 'System.dll'),
    (Join-Path $frameworkDirectory 'System.Core.dll'),
    (Join-Path $frameworkDirectory 'System.Xml.dll'),
    (Join-Path $frameworkDirectory 'System.Xaml.dll'),
    (Join-Path $frameworkDirectory 'System.Web.Extensions.dll'),
    (Join-Path $frameworkDirectory 'System.Windows.Forms.dll'),
    (Join-Path $frameworkDirectory 'System.Drawing.dll'),
    (Join-Path $frameworkDirectory 'System.IO.Compression.dll'),
    (Join-Path $frameworkDirectory 'System.IO.Compression.FileSystem.dll'),
    (Join-Path $wpfDirectory 'WindowsBase.dll'),
    (Join-Path $wpfDirectory 'PresentationCore.dll'),
    (Join-Path $wpfDirectory 'PresentationFramework.dll')
)
$compilerArguments = @(
    '/nologo','/target:winexe','/platform:x64','/optimize+','/langversion:5','/codepage:65001',
    ('/out:' + $outputPath),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/win32icon:' + $iconPath),
    ('/resource:' + $payloadZip + ',SteamSmart.Payload.zip'),
    ('/resource:' + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',SteamSmart.MainWindow.xaml'),
    ('/resource:' + $iconPath + ',SteamSmart.Icon.ico')
)
$compilerArguments += @($references | ForEach-Object { '/reference:' + $_ })
$compilerArguments += @('Program.cs','Bundle.cs','RuntimeController.cs','ThemeManager.cs','AssemblyInfo.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ })
$compilerArguments += @($windowSources | ForEach-Object { $_.FullName })
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码：$LASTEXITCODE" }
$executable = Get-Item -LiteralPath $outputPath
[pscustomobject]@{
    Executable=$executable.FullName
    SizeBytes=$executable.Length
    PayloadZipBytes=(Get-Item -LiteralPath $payloadZip).Length
    PayloadFileCount=$payloadNames.Count
    SHA256=(Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
