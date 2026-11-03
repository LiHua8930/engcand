<#
.SYNOPSIS
    英文候选助手（EngCand）一键构建脚本 —— 离线环境可用。

.DESCRIPTION
    1. 设置离线构建所需的环境变量（DOTNET_CLI_HOME / NUGET_PACKAGES 等均指向 workspace 内目录）；
    2. 清理并重建 dist\app；
    3. dotnet publish Release / win-x64 / 框架依赖（self-contained false）；
    4. 把 data\words.tsv、phrases.tsv、word_bigrams.tsv（存在才拷）复制到 dist\app\data\；
    5. 打印构建摘要（exe、词典条数、数据目录），成功返回 0。

    注意：
    - 本项目离线，不能使用 PublishSingleFile（会因 NuGet 拉包失败）；
    - 不要修改 EngCand.csproj 里的 RuntimeIdentifier；
    - 本脚本只写 dist\，不改 src\ / data\。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
#>

[CmdletBinding()]
param(
    # 跳过清理 dist\app（增量发布，一般不需要）
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 路径解析
# ---------------------------------------------------------------------------
$projectRoot = $PSScriptRoot
if ([string]::IsNullOrEmpty($projectRoot)) { $projectRoot = (Get-Location).Path }
$projectRoot = (Resolve-Path -LiteralPath $projectRoot).Path
$workspaceRoot = Split-Path -Parent $projectRoot

$csproj      = Join-Path $projectRoot 'src\EngCand\EngCand.csproj'
$distRoot    = Join-Path $projectRoot 'dist'
$distApp     = Join-Path $distRoot 'app'
$dataDir     = Join-Path $projectRoot 'data'
$dotnetExe = (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if ([string]::IsNullOrEmpty($dotnetExe)) { $dotnetExe = 'C:\Program Files\dotnet\dotnet.exe' }

$dataFiles = @('words.tsv', 'phrases.tsv', 'word_bigrams.tsv')

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host ('=== ' + $text + ' ===') -ForegroundColor Cyan
}
function Write-Ok([string]$text)   { Write-Host ('  [OK]   ' + $text) -ForegroundColor Green }
function Write-Warn([string]$text) { Write-Host ('  [WARN] ' + $text) -ForegroundColor Yellow }
function Write-Fail([string]$text) { Write-Host ('  [FAIL] ' + $text) -ForegroundColor Red }

Write-Head '英文候选助手 · 一键构建'
Write-Host ('  项目根    : ' + $projectRoot)
Write-Host ('  workspace : ' + $workspaceRoot)
Write-Host ('  输出目录  : ' + $distApp)

# ---------------------------------------------------------------------------
# 前置检查
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    Write-Fail ('未找到 dotnet：' + $dotnetExe)
    Write-Host '  请确认已安装 .NET 10 SDK，或修改本脚本顶部的 $dotnetExe 路径。'
    exit 1
}
if (-not (Test-Path -LiteralPath $csproj)) {
    Write-Fail ('未找到工程文件：' + $csproj)
    exit 1
}

# ---------------------------------------------------------------------------
# 离线构建环境变量（必须设置，否则 dotnet 首次运行会写用户目录失败）
# ---------------------------------------------------------------------------
$buildCache = Join-Path $projectRoot '.build'
New-Item -ItemType Directory -Force -Path $buildCache | Out-Null
$env:DOTNET_CLI_HOME                = Join-Path $buildCache 'dotnet-home'
$env:NUGET_PACKAGES                 = Join-Path $buildCache 'nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT    = '1'
$env:DOTNET_NOLOGO                  = '1'
$env:DOTNET_CLI_UI_LANGUAGE          = 'en'

foreach ($dir in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
}
Write-Host ('  DOTNET_CLI_HOME = ' + $env:DOTNET_CLI_HOME)
Write-Host ('  NUGET_PACKAGES  = ' + $env:NUGET_PACKAGES)

# 如果有旧进程占着 dist\app 里的 exe，先结束它（否则无法清理/覆盖）
$old = @(Get-Process -Name 'EngCand' -ErrorAction SilentlyContinue)
if ($old.Count -gt 0) {
    Write-Warn ('检测到 EngCand 进程（PID: ' + (($old | ForEach-Object { $_.Id }) -join ', ') + '），先结束以便重建。')
    foreach ($proc in $old) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Start-Sleep -Milliseconds 800
}

# ---------------------------------------------------------------------------
# 清理 dist\app
# ---------------------------------------------------------------------------
if (-not $NoClean -and (Test-Path -LiteralPath $distApp)) {
    try {
        Remove-Item -LiteralPath $distApp -Recurse -Force -ErrorAction Stop
        Write-Ok '已清理旧 dist\app'
    }
    catch {
        Write-Fail ('无法清理 dist\app：' + $_.Exception.Message)
        Write-Host '  可能仍有 EngCand.exe 在运行，请在托盘菜单退出后重试。'
        exit 1
    }
}
if (-not (Test-Path -LiteralPath $distApp)) {
    New-Item -ItemType Directory -Force -Path $distApp | Out-Null
}

# ---------------------------------------------------------------------------
# dotnet publish
# ---------------------------------------------------------------------------
Write-Head 'dotnet publish（Release / win-x64 / 框架依赖，不含单文件）'
$publishArgs = @(
    'publish', $csproj,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'false',
    '-o', $distApp
)
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& $dotnetExe @publishArgs
$publishExit = $LASTEXITCODE
$stopwatch.Stop()

if ($publishExit -ne 0) {
    Write-Fail ('dotnet publish 失败，退出码 ' + $publishExit + '（耗时 ' + [int]$stopwatch.Elapsed.TotalSeconds + ' 秒）')
    Write-Host '  请把上面的编译错误原文反馈给开发同学；本脚本不修改 src\ 源码。'
    exit 1
}

$exePath = Join-Path $distApp 'EngCand.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Fail ('发布成功但未找到 ' + $exePath)
    exit 1
}
Write-Ok ('dotnet publish 成功，耗时 ' + [int]$stopwatch.Elapsed.TotalSeconds + ' 秒')

# ---------------------------------------------------------------------------
# 复制运行时词典
# ---------------------------------------------------------------------------
Write-Head '复制词典数据到 dist\app\data'
$dstData = Join-Path $distApp 'data'
if (-not (Test-Path -LiteralPath $dstData)) {
    New-Item -ItemType Directory -Force -Path $dstData | Out-Null
}

$copied = New-Object System.Collections.ArrayList
$missing = New-Object System.Collections.ArrayList
foreach ($name in $dataFiles) {
    $src = Join-Path $dataDir $name
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $dstData $name) -Force
        $size = (Get-Item -LiteralPath $src).Length
        [void]$copied.Add([pscustomobject]@{ Name = $name; Size = $size })
        Write-Ok ($name + '  ->  dist\app\data\' + $name + '（' + [math]::Round($size / 1KB, 1) + ' KB）')
    }
    else {
        [void]$missing.Add($name)
        Write-Warn ($name + ' 不存在，已跳过（程序仍可启动，但没有对应数据）')
    }
}

# ---------------------------------------------------------------------------
# 精简发布产物：移除调试符号（运行不需要，dist\app 只保留运行所需文件）
# ---------------------------------------------------------------------------
$pdbFiles = @(Get-ChildItem -LiteralPath $distApp -Filter '*.pdb' -File -ErrorAction SilentlyContinue)
foreach ($pdb in $pdbFiles) {
    try {
        Remove-Item -LiteralPath $pdb.FullName -Force -ErrorAction Stop
        Write-Ok ('已移除调试符号 ' + $pdb.Name + '（运行不需要）')
    }
    catch {
        Write-Warn ('无法移除 ' + $pdb.Name + '：' + $_.Exception.Message)
    }
}

# ---------------------------------------------------------------------------
# 摘要
# ---------------------------------------------------------------------------
function Get-DataCount([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return -1 }
    $n = 0
    try {
        foreach ($line in [System.IO.File]::ReadLines($path)) {
            $t = $line.Trim()
            if ($t.Length -gt 0 -and $t[0] -ne '#') { $n++ }
        }
    }
    catch { return -1 }
    return $n
}

$wordsCount   = Get-DataCount (Join-Path $dstData 'words.tsv')
$phrasesCount = Get-DataCount (Join-Path $dstData 'phrases.tsv')
$bigramsCount = Get-DataCount (Join-Path $dstData 'word_bigrams.tsv')

$exeInfo = Get-Item -LiteralPath $exePath
$version = ''
try { $version = $exeInfo.VersionInfo.FileVersion } catch { }
$appFileCount = @(Get-ChildItem -LiteralPath $distApp -File -Recurse -ErrorAction SilentlyContinue).Count

Write-Head '构建摘要'
Write-Host ('  可执行文件 : ' + $exePath)
Write-Host ('  文件大小   : ' + [math]::Round($exeInfo.Length / 1KB, 1) + ' KB  版本: ' + $version)
Write-Host ('  dist\app   : ' + $appFileCount + ' 个文件')
Write-Host ('  词典       : words=' + $wordsCount + '  phrases=' + $phrasesCount + '  bigrams=' + $bigramsCount)

$hasData = ($wordsCount -ge 1)
if (-not $hasData) {
    Write-Fail '缺少 words.tsv：程序可以启动但不弹候选。请先运行数据管线生成 data\words.tsv 后重新构建。'
    Write-Host ''
    Write-Host '构建结果：产物已生成，但词典缺失（退出码 2）' -ForegroundColor Red
    exit 2
}

if ($missing.Count -gt 0) {
    Write-Warn ('缺少数据文件：' + ($missing -join '、'))
}
Write-Ok '构建完成，可以运行 启动.bat 或 dist\app\EngCand.exe'
Write-Host ''
Write-Host '构建结果：成功（退出码 0）' -ForegroundColor Green
exit 0
