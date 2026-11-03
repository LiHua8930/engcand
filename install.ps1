<#
.SYNOPSIS
    英文候选助手（EngCand）安装脚本：创建桌面快捷方式，可选开机启动。

.DESCRIPTION
    - 只创建快捷方式（.lnk），不写注册表、不需要管理员权限、不联网；
    - 快捷方式指向 dist\app\EngCand.exe，工作目录为 dist\app；
    - 加 -AutoStart 时，同时在「启动」文件夹创建快捷方式，登录 Windows 后自动启动；
    - 卸载请运行同目录的 uninstall.ps1。

.PARAMETER AutoStart
    同时创建开机启动快捷方式（当前用户的「启动」文件夹）。

.PARAMETER DesktopDir
    【仅测试用】覆盖桌面目录，默认使用当前用户真实桌面。用于沙箱/CI 环境验收测试。

.PARAMETER StartupDir
    【仅测试用】覆盖「启动」目录，默认使用当前用户真实启动文件夹。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -AutoStart
#>

[CmdletBinding()]
param(
    [switch]$AutoStart,
    [string]$DesktopDir = '',
    [string]$StartupDir = ''
)

$ErrorActionPreference = 'Stop'

# 注意：PowerShell 变量名不区分大小写，先把测试用覆盖参数存到独立变量，
# 避免下面给 $desktopDir / $startupDir 赋默认值时覆盖掉参数值。
$desktopDirOverride = $DesktopDir
$startupDirOverride = $StartupDir

# ---------------------------------------------------------------------------
# 路径解析
# ---------------------------------------------------------------------------
$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }
$root = (Resolve-Path -LiteralPath $root).Path

$distApp = Join-Path $root 'dist\app'
$exePath = Join-Path $distApp 'EngCand.exe'
$linkName = '英文候选助手.lnk'

$desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
if ([string]::IsNullOrEmpty($desktopDir)) { $desktopDir = Join-Path $env:USERPROFILE 'Desktop' }
$startupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
if ([string]::IsNullOrEmpty($startupDir)) {
    $startupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
}
# 测试用覆盖（正常安装不要传这两个参数）
if (-not [string]::IsNullOrEmpty($desktopDirOverride)) { $desktopDir = $desktopDirOverride }
if (-not [string]::IsNullOrEmpty($startupDirOverride)) { $startupDir = $startupDirOverride }

Write-Host ''
Write-Host '=== 英文候选助手 · 安装 ===' -ForegroundColor Cyan
Write-Host ('  程序目录 : ' + $distApp)

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Host ('  [错误] 未找到 ' + $exePath) -ForegroundColor Red
    Write-Host '         请先运行 build.ps1 完成构建，再安装快捷方式。'
    exit 1
}

# ---------------------------------------------------------------------------
# 创建快捷方式
# ---------------------------------------------------------------------------
function New-AppShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$LinkPath
    )
    $parent = Split-Path -Parent $LinkPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($LinkPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $distApp
    $shortcut.IconLocation = $exePath + ',0'
    $shortcut.Description = '英文候选助手 - 英文输入候选（托盘常驻）'
    $shortcut.WindowStyle = 1
    $shortcut.Save()
}

try {
    $desktopLink = Join-Path $desktopDir $linkName
    New-AppShortcut -LinkPath $desktopLink
    Write-Host ('  [OK] 桌面快捷方式：' + $desktopLink) -ForegroundColor Green
}
catch {
    Write-Host ('  [错误] 创建桌面快捷方式失败：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}

if ($AutoStart) {
    try {
        $startupLink = Join-Path $startupDir $linkName
        New-AppShortcut -LinkPath $startupLink
        Write-Host ('  [OK] 开机启动项：' + $startupLink) -ForegroundColor Green
    }
    catch {
        Write-Host ('  [警告] 创建开机启动项失败：' + $_.Exception.Message) -ForegroundColor Yellow
        Write-Host '         桌面快捷方式已创建，程序仍可手动启动。'
    }
}
else {
    Write-Host '  [提示] 未创建开机启动项；需要开机自启请加 -AutoStart 参数。'
}

Write-Host ''
Write-Host '安装完成。' -ForegroundColor Green
Write-Host '  启动方式 1：双击桌面「英文候选助手」'
Write-Host '  启动方式 2：双击项目根目录的 启动.bat'
Write-Host ('  启动方式 3：直接运行 ' + $exePath)
Write-Host '  卸载方法  ：运行 uninstall.ps1（加 -PurgeData 可同时删除配置/日志/学习数据）'
Write-Host ''
exit 0
