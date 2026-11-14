# 注册 Windows 计划任务：每 N 分钟执行一次 Git 备份。
# 用法（当前用户即可，不需要管理员）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File backup\setup-windows.ps1
# 卸载：
#   schtasks /delete /tn SmlServerGitBackup /f

param(
    [int]$Minutes = 30,
    [string]$TaskName = "SmlServerGitBackup",
    [string]$RepoDir = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
)

$ErrorActionPreference = "Stop"
$script = Join-Path $RepoDir "backup\git-backup.ps1"

if (-not (Test-Path $script)) {
    throw "找不到备份脚本：$script"
}

$action = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $script + '"'
schtasks /create /tn $TaskName /sc minute /mo $Minutes /tr $action /f

if ($LASTEXITCODE -eq 0) {
    Write-Output "已创建计划任务 $TaskName（每 $Minutes 分钟，当前用户登录时运行）"
    Write-Output "手动执行一次：schtasks /run /tn $TaskName"
} else {
    Write-Output "创建失败（退出码 $LASTEXITCODE），请以管理员身份运行"
}
