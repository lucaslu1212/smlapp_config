# 服务端自动 Git 备份（Windows 计划任务）
#
# 行为：把仓库内变更提交，然后推送到远程；无变更则直接退出。
# 推送失败只写日志，不影响服务运行。
#
# 配置：复制 backup\backup.env.example 为 backup\backup.env（该文件不进 Git）
# 认证：优先使用 ssh-agent 中已载入的密钥；也可在 backup.env 里设置 SML_SSH_KEY。

param(
    [string]$RepoDir = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
)

$ErrorActionPreference = "Continue"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = Join-Path $RepoDir "backup.log"

function Write-Log([string]$message) {
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $message
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

# 日志超过 1 MiB 时保留最近 500 行
if ((Test-Path $logFile) -and ((Get-Item $logFile).Length -gt 1048576)) {
    $recent = Get-Content $logFile -Tail 500
    Set-Content -Path $logFile -Value $recent -Encoding UTF8
}

# 读取 backup.env（KEY=VALUE）
$envFile = Join-Path $scriptDir "backup.env"
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2].Trim(), "Process")
        }
    }
}

$branch = if ($env:SML_BRANCH) { $env:SML_BRANCH } else { "main" }
$remote = if ($env:SML_REMOTE) { $env:SML_REMOTE } else { "origin" }

if ($env:SML_SSH_KEY -and (Test-Path $env:SML_SSH_KEY)) {
    $env:GIT_SSH_COMMAND = 'ssh -i "' + $env:SML_SSH_KEY + '" -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8'
}

# 私钥带口令时，用 askpass 脚本非交互输入口令（脚本权限建议 600，不进入 Git）。
# 更安全的长效方案是用无口令部署密钥或 ssh-agent，见 README 5.2。
if ($env:SML_ASKPASS -and (Test-Path $env:SML_ASKPASS)) {
    $env:SSH_ASKPASS = $env:SML_ASKPASS
    $env:SSH_ASKPASS_REQUIRE = "force"
    if (-not $env:DISPLAY) { $env:DISPLAY = "localhost:0" }
}

Set-Location $RepoDir

# 防并发
$lock = Join-Path $RepoDir ".git\sml-backup.lock"
if (Test-Path $lock) {
    $pidText = Get-Content $lock -ErrorAction SilentlyContinue
    if ($pidText) {
        $existing = Get-Process -Id $pidText -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Log "已有备份进程 $pidText，跳过"
            exit 0
        }
    }
}
Set-Content -Path $lock -Value $PID

try {
    git add -A 2>&1 | Out-Null
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        exit 0
    }

    $message = "auto backup: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    git commit -m $message 2>&1 | Out-Null

    $output = git push $remote $branch 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Log "push ok"
    } else {
        $output | ForEach-Object { Write-Log $_ }
        Write-Log "push failed（检查远程地址、SSH key 或网络）"
    }
}
finally {
    Remove-Item $lock -ErrorAction SilentlyContinue
}
