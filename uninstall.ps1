<#
.SYNOPSIS
    英文候选助手（EngCand）卸载脚本：删除桌面/开机启动快捷方式，并提示清理用户数据。

.DESCRIPTION
    - 只删除本脚本创建的快捷方式，不写注册表、不需要管理员权限；
    - 默认保留 %APPDATA%\EngCand（配置 config.json、日志 engcand.log、学习数据 learning.json），
      仅打印提示；确需彻底清理时使用 -PurgeData；
    - 不删除 dist\app 中的程序文件（如需删除，请手动删除整个项目目录或 dist 目录）。

.PARAMETER PurgeData
    同时删除 %APPDATA%\EngCand 目录（配置、日志、学习数据）。删除后不可恢复。

.PARAMETER DesktopDir
    【仅测试用】覆盖桌面目录，默认使用当前用户真实桌面。用于沙箱/CI 环境验收测试。

.PARAMETER StartupDir
    【仅测试用】覆盖「启动」目录，默认使用当前用户真实启动文件夹。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1 -PurgeData
#>

[CmdletBinding()]
param(
    [switch]$PurgeData,
    [string]$DesktopDir = '',
    [string]$StartupDir = ''
)

$ErrorActionPreference = 'Stop'

# 注意：PowerShell 变量名不区分大小写，先把测试用覆盖参数存到独立变量，
# 避免下面给 $desktopDir / $startupDir 赋默认值时覆盖掉参数值。
$desktopDirOverride = $DesktopDir
$startupDirOverride = $StartupDir

$root = $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }
$root = (Resolve-Path -LiteralPath $root).Path

$linkName = '英文候选助手.lnk'
$desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
if ([string]::IsNullOrEmpty($desktopDir)) { $desktopDir = Join-Path $env:USERPROFILE 'Desktop' }
$startupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
if ([string]::IsNullOrEmpty($startupDir)) {
    $startupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
}
# 测试用覆盖（正常卸载不要传这两个参数）
if (-not [string]::IsNullOrEmpty($desktopDirOverride)) { $desktopDir = $desktopDirOverride }
if (-not [string]::IsNullOrEmpty($startupDirOverride)) { $startupDir = $startupDirOverride }
$appDataDir = Join-Path $env:APPDATA 'EngCand'

Write-Host ''
Write-Host '=== 英文候选助手 · 卸载 ===' -ForegroundColor Cyan

$running = @(Get-Process -Name 'EngCand' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host '  [提示] EngCand 正在运行。请先在托盘图标右键菜单选择「退出」，再删除程序目录。' -ForegroundColor Yellow
}

$removed = 0
$targets = @(
    (Join-Path $desktopDir $linkName),
    (Join-Path $startupDir $linkName)
)
foreach ($lnk in $targets) {
    if (Test-Path -LiteralPath $lnk) {
        try {
            Remove-Item -LiteralPath $lnk -Force -ErrorAction Stop
            Write-Host ('  [OK] 已删除快捷方式：' + $lnk) -ForegroundColor Green
            $removed++
        }
        catch {
            Write-Host ('  [警告] 删除失败：' + $lnk + ' —— ' + $_.Exception.Message) -ForegroundColor Yellow
        }
    }
    else {
        Write-Host ('  [跳过] 不存在：' + $lnk)
    }
}

if ($PurgeData) {
    if (Test-Path -LiteralPath $appDataDir) {
        try {
            Remove-Item -LiteralPath $appDataDir -Recurse -Force -ErrorAction Stop
            Write-Host ('  [OK] 已删除用户数据：' + $appDataDir) -ForegroundColor Green
        }
        catch {
            Write-Host ('  [警告] 删除用户数据失败：' + $_.Exception.Message) -ForegroundColor Yellow
            Write-Host '         程序可能仍在运行，退出后重试，或手动删除该目录。'
        }
    }
    else {
        Write-Host ('  [跳过] 用户数据目录不存在：' + $appDataDir)
    }
}
else {
    if (Test-Path -LiteralPath $appDataDir) {
        Write-Host ''
        Write-Host '  [提示] 配置、日志和学习数据仍保留在：' -ForegroundColor Yellow
        Write-Host ('         ' + $appDataDir)
        Write-Host '         如需彻底清理，请运行：uninstall.ps1 -PurgeData'
        Write-Host '         或手动删除该目录（不影响其它软件）。'
    }
}

Write-Host ''
Write-Host ('卸载完成（共删除 ' + $removed + ' 个快捷方式）。') -ForegroundColor Green
Write-Host '程序文件仍在 dist\app；如需删除，请手动删除整个 english-cand 目录。'
Write-Host ''
exit 0
