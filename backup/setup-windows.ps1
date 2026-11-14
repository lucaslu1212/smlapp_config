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

$user = "$env:USERDOMAIN\$env:USERNAME"
$start = (Get-Date).AddMinutes(1).ToString("s")
$interval = "PT${Minutes}M"

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>SmlApp server Git backup</Description>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <StartBoundary>$start</StartBoundary>
      <Enabled>true</Enabled>
      <Repetition>
        <Interval>$interval</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -ExecutionPolicy Bypass -File "$script"</Arguments>
    </Exec>
  </Actions>
</Task>
"@

$temp = Join-Path $env:TEMP ("sml-backup-task-" + [guid]::NewGuid().ToString("N") + ".xml")
[System.IO.File]::WriteAllText($temp, $xml, [System.Text.Encoding]::Unicode)

try {
    schtasks /create /tn $TaskName /xml $temp /f
    if ($LASTEXITCODE -eq 0) {
        Write-Output "已创建计划任务 $TaskName（每 $Minutes 分钟，当前用户登录时运行）"
        Write-Output "手动执行一次：schtasks /run /tn $TaskName"
    } else {
        Write-Output "创建失败（退出码 $LASTEXITCODE），请以管理员身份运行"
    }
}
finally {
    Remove-Item $temp -ErrorAction SilentlyContinue
}
