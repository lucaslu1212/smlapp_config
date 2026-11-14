# 教室一体机管家 · 服务端（Linux / Python）

仅使用 Python 3.9+ 标准库，**无需 pip 安装任何依赖**。协议、REST 接口与数据格式与 `../windows/` 完全一致，`data/` 目录可直接互相迁移。

## 1. 目录

```
linux/
├─ sml_server.py      # 服务端主程序（HTTP/HTTPS/WSS/REST 全部内置）
├─ config.json        # 端口、证书、数据目录、初始管理员
├─ run.sh             # 启动脚本
├─ static/            # 教师端 / 与后台 /admin（与 Windows 版同一套前端）
└─ certs/             # 证书目录（首次启动自动生成自签证书）
```

## 2. 依赖与权限

- Python 3.9+（无需第三方包）
- 监听 **443/80** 需要 root，或给 Python 授予绑定低端口能力：
  ```bash
  sudo setcap 'cap_net_bind_service=+ep' "$(readlink -f "$(which python3)")"
  ```
- 首次启动需要 `openssl` 生成自签证书；生产环境建议直接放置受信任的 PEM 证书。

## 3. 快速开始

### 本地调试

```bash
cd server/linux
python3 sml_server.py --dev
```

- 教师端：<https://localhost:8443/>
- 后台：<https://localhost:8443/admin>
- 初始账号：`admin` / `Admin@12345`
- 数据在 `data-dev/`，证书自动生成到 `certs/`

### 生产启动

```bash
cd server/linux
sudo ./run.sh
```

监听 `0.0.0.0:443`（HTTPS）与 `0.0.0.0:80`（仅 301 跳转）。

### systemd（推荐）

```ini
# /etc/systemd/system/sml-server.service
[Unit]
Description=SmlApp Classroom Server
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/sml-server
ExecStart=/usr/bin/python3 /opt/sml-server/sml_server.py
Restart=always
RestartSec=3
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
User=root

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now sml-server
sudo journalctl -u sml-server -f
```

## 4. 配置（config.json）

| 键 | 说明 |
| --- | --- |
| `httpsPort` | HTTPS 端口，默认 443 |
| `httpPort` | HTTP 端口，默认 80；只用于 301，设为 0 关闭 |
| `dataDir` | 数据目录（相对本目录），默认 `data` |
| `certFile` / `keyFile` | PEM 证书与私钥路径（相对本目录） |
| `heartbeatTimeoutSeconds` | 心跳超时判离线，默认 90 |
| `bootstrap.adminUsername` / `adminPassword` | **仅首次启动**创建管理员；之后以 `data/users.json` 为准 |

> 部署后请立即登录后台修改密码。会话保存在内存，重启后需要重新登录。

### 证书

- 支持 PEM（`fullchain.pem` + `privkey.pem`），例如 Let's Encrypt：
  ```bash
  sudo certbot certonly --standalone -d sml.example.com
  # 然后修改 config.json：
  # "certFile": "/etc/letsencrypt/live/sml.example.com/fullchain.pem",
  # "keyFile":  "/etc/letsencrypt/live/sml.example.com/privkey.pem"
  ```
- 未找到证书时自动用 `openssl` 生成 3 年期自签证书（`certs/`），浏览器会提示不受信任。
- 不支持直接读取 PFX；请转换：
  ```bash
  openssl pkcs12 -in server.pfx -nodes -out server.pem
  # 从 server.pem 中拆出证书与私钥，或直接用 openssl 导出：
  openssl pkcs12 -in server.pfx -clcerts -nokeys -out cert.pem
  openssl pkcs12 -in server.pfx -nocerts -nodes -out key.pem
  ```

## 5. 与客户端对接

1. 后台 →「设备」新建设备，选择教室，得到 **接入令牌**（只显示一次）。
2. 一体机客户端设置页：
   - 服务器地址：`wss://<服务器地址>/ws/agent`
   - 设备接入令牌：粘贴上一步的令牌
3. 首次连接自动把记录与客户端本地 `deviceId` 绑定，状态从「待绑定」变为「在线」。
4. 教师端即可下发指令、配置策略、生成一次性密码、审批卸载/退出。

## 6. 数据目录

与 Windows 版完全一致：

```
data/
├─ users.json / classrooms.json / devices.json
├─ otps.json / otp-usages.json / approvals.json
├─ screens/            # 设备截图
├─ events/             # 设备事件（jsonl）
└─ audit/              # 审计日志（jsonl）
```

备份整个目录即可。账号、令牌、OTP 均只存哈希。

## 7. 与 Windows 版的差异

| 项 | Windows（ASP.NET Core） | Linux（Python） |
| --- | --- | --- |
| 依赖 | .NET 8 运行时 | Python 3.9+ 标准库 |
| 证书 | PFX（配置路径/密码），缺失时自签 | PEM（certFile/keyFile），缺失时 openssl 自签 |
| 会话 | Cookie 认证，7 天滑动续期 | Cookie + 内存会话，重启失效 |
| 数据格式 | JSON | JSON（相同） |
| 前端 | wwwroot | static（相同） |

## 8. 常见问题

| 现象 | 处理 |
| --- | --- |
| `PermissionError: [Errno 13]` | 用 root 或 `setcap`，或先用 `--dev` 测试 |
| 证书生成失败 | 安装 `openssl`，或手动放置 PEM 并修改 config.json |
| 客户端 401 | 令牌错误、设备被禁用或令牌已重置 |
| 教师看不到设备 | 设备未分配教室，或账号没有该教室权限 |
| 重启后需要重新登录 | 会话在内存中，属于预期行为 |
