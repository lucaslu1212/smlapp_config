# 教室一体机管家 · 服务端

**只保留 Python 版**（原 ASP.NET Core 版已弃用）。协议、REST 接口、数据格式与 Windows 客户端对齐；同一套代码可在 Linux / Windows 上运行。

## 1. 目录

```
server/
├─ README.md                  # 本文件
├─ .gitignore
├─ linux/
│  ├─ sml_server.py           # 服务端主程序（标准库，无第三方依赖）
│  ├─ config.json             # 端口、证书、数据目录、初始管理员
│  ├─ run.sh                  # Linux 启动脚本
│  ├─ static/                 # 教师端 / 与后台 /admin
│  └─ README.md               # 详细部署说明（证书、systemd、常见问题）
└─ backup/
   ├─ git-backup.sh           # 自动备份（Linux / Git Bash）
   ├─ git-backup.ps1          # 自动备份（Windows 计划任务）
   ├─ backup.env.example      # 本地配置模板（复制为 backup.env）
   ├─ setup-linux.sh          # 安装 systemd timer
   ├─ setup-windows.ps1       # 注册 Windows 计划任务
   └─ sml-backup.service/.timer
```

## 2. 运行要求

- Python 3.9+（不需要 pip 安装任何包）
- 监听 **443/80**：Linux 需要 root 或 `setcap cap_net_bind_service`；Windows 需要管理员
- 首次启动需要 `openssl`（用于生成自签证书），或手动提供 PEM 证书

## 3. 快速开始

### Linux（生产）

```bash
cd server/linux
sudo ./run.sh
# 教师端 https://<服务器地址>/    后台 https://<服务器地址>/admin
```

systemd 常驻配置见 [`linux/README.md`](linux/README.md)。

### Windows（Python 版）

```powershell
cd server\linux
python sml_server.py --dev      # 8443/8080，数据在 data-dev/
# 生产（443/80 需要管理员）：
python sml_server.py
```

> Windows 上如果 443 被 IIS/其他程序占用，可修改 `config.json` 的 `httpsPort`/`httpPort`。

初始管理员：`admin` / `Admin@12345`（首次启动创建，登录后请立即修改）。

## 4. 与 Windows 客户端对接

1. 后台 →「设备」新建设备 → 复制 **接入令牌**（只显示一次）
2. 客户端设置页：
   - 服务器地址：`wss://<服务器地址>/ws/agent`
   - 设备接入令牌：粘贴令牌
3. 首次连接时服务端把记录与客户端本地 `deviceId` 绑定（「待绑定」→「在线」）
4. 教师端即可下发指令、改策略、生成应急密码、审批退出/卸载

协议字段详见 `../windows/docs/API.md`（§13 OTP、§14 卸载授权、§15 退出授权）。

## 5. Git 自动备份

仓库根就是本目录（`server/`）。备份脚本会把变更提交并推送，**推送失败不影响服务运行**。

### 5.1 远程仓库

远程地址暂时用 `git@12345` 占位，等真实仓库确定后替换：

```bash
cd server
git remote set-url origin git@<真实主机>:<组>/<仓库>.git
```

> 仓库建议设为 **私有**：`data/` 里有账号哈希、设备令牌哈希、审计与审批记录。

### 5.2 认证（二选一）

**方式 A：ssh-agent（当前私钥带口令）**

私钥 `C:\Users\Lucas\.ssh\id_rsa` 带口令，非交互推送需要先让 agent 载入一次：

```powershell
# 管理员 PowerShell（只需一次）
Set-Service ssh-agent -StartupType Automatic
Start-Service ssh-agent

# 当前用户（输入一次口令）
ssh-add C:\Users\Lucas\.ssh\id_rsa
```

之后计划任务以同一用户运行时可直接复用 agent。

**方式 B：无口令部署密钥（推荐，长期无人值守）**

```bash
ssh-keygen -t ed25519 -N "" -f ~/.ssh/sml_backup
cat ~/.ssh/sml_backup.pub   # 添加到远程仓库的 Deploy Key（允许写入）
```

在 `backup/backup.env` 中设置：

```
SML_SSH_KEY=/root/.ssh/sml_backup
```

Windows 下为 `SML_SSH_KEY=C:/Users/Lucas/.ssh/sml_backup`。

> 请勿把口令写进脚本或仓库；`backup.env` 已被 `.gitignore` 排除。

### 5.3 配置本地参数

```bash
cd server/backup
cp backup.env.example backup.env
# 按需修改 SML_BRANCH / SML_REMOTE / SML_SSH_KEY
```

### 5.4 安装定时任务

**Linux（systemd，每 30 分钟）**

```bash
cd server/backup
chmod +x git-backup.sh setup-linux.sh
sudo ./setup-linux.sh /opt/sml-server   # 换成实际部署目录
systemctl status sml-backup.timer
```

**Windows（计划任务，每 30 分钟）**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File server\backup\setup-windows.ps1
schtasks /query /tn SmlServerGitBackup
```

### 5.5 手动执行与日志

```bash
# Linux / Git Bash
bash server/backup/git-backup.sh

# Windows
powershell -NoProfile -ExecutionPolicy Bypass -File server\backup\git-backup.ps1
```

日志：`server/backup.log`（自动轮转，保留最近 500 行）。

### 5.6 备份范围

| 内容 | 是否进入 Git |
| --- | --- |
| `linux/sml_server.py`、`static/`、`config.json`、README | ✅ |
| `linux/data/*.json`（设备、账号、OTP、审批） | ✅ |
| `linux/data/audit/`（审计 JSONL） | ✅ |
| `linux/data/events/`（事件，量大） | ❌（`.gitignore` 排除） |
| `linux/data/screens/`（截图，体积大） | ❌（`.gitignore` 排除） |
| `linux/certs/`（私钥） | ❌（必须排除） |
| `backup/backup.env`、`backup.log` | ❌ |

> 如需把截图或事件也备份，去掉 `.gitignore` 对应行，但要注意仓库体积。

## 6. 常见问题

| 现象 | 处理 |
| --- | --- |
| push 报 `Permission denied (publickey)` | agent 未载入密钥，或部署密钥未添加到仓库；见 5.2 |
| push 报 `Could not resolve hostname 12345` | 仍在用占位地址；执行 `git remote set-url` 换真实地址 |
| 计划任务没有运行 | Windows 需当前用户登录；`schtasks /query /tn SmlServerGitBackup /v /fo list` 查看上次结果 |
| 仓库太大 | 确认 `data/screens`、`data/events`、`certs` 被忽略；可 `git rm -r --cached` 后提交 |
| 443 启动失败 | 端口被占用或权限不足；Linux 用 root/setcap，Windows 用管理员 |
