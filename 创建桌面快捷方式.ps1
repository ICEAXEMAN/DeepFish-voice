# 在桌面创建（或重新创建）「大肥鱼桌宠」快捷方式。
# 图标用插件里的 assets\大肥鱼.ico；双击快捷方式 = 运行插件根目录的「快捷方式启动.vbs」：
# 她收着就叫她出来，没在跑就启动她，托盘小图标丢了也顺手补回来。
#
#   用法：powershell -ExecutionPolicy Bypass -File 创建桌面快捷方式.ps1
#   可选：-LnkPath "D:\某处\大肥鱼桌宠.lnk" 生成到别处（自检/测试用）
param([string]$LnkPath = '')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$launcher = Join-Path $root '快捷方式启动.vbs'
$icon = Join-Path $root 'assets\大肥鱼.ico'

if (!(Test-Path -LiteralPath $launcher)) {
    throw ('没找到启动器，先确认插件是完整的：' + $launcher)
}
if ([string]::IsNullOrWhiteSpace($LnkPath)) {
    $LnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) '大肥鱼桌宠.lnk'
}
$LnkPath = [IO.Path]::GetFullPath($LnkPath)
$parent = Split-Path -Parent $LnkPath
if ($parent -and !(Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

$shell = New-Object -ComObject WScript.Shell
try {
    $lnk = $shell.CreateShortcut($LnkPath)
    $lnk.TargetPath = Join-Path $env:SystemRoot 'System32\wscript.exe'
    $lnk.Arguments = '"' + $launcher + '"'
    $lnk.WorkingDirectory = $root
    if (Test-Path -LiteralPath $icon) { $lnk.IconLocation = $icon }
    $lnk.Description = '大肥鱼桌宠：点一下叫她出来'
    $lnk.WindowStyle = 7
    $lnk.Save()
} finally {
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}

# 让资源管理器马上刷新图标（新图标有时候慢一拍）
try { & "$env:SystemRoot\System32\ie4uinit.exe" -show 2>$null } catch { }

Write-Host ('✓ 桌面快捷方式：' + $LnkPath) -ForegroundColor Green
if (!(Test-Path -LiteralPath $icon)) {
    Write-Host ('  ! 没找到图标（' + $icon + '），先用系统默认的；图标放回去再运行一次就行。') -ForegroundColor Yellow
}
Write-Host '  双击它 = 她收着就叫她出来 / 没在跑就启动她 / 托盘小图标丢了也顺手补回来。'
