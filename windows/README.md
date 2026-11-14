# 教室一体机管家 · 服务端（Windows / ASP.NET Core）

同一套数据格式与协议也提供了 Linux Python 版本（见 `../linux/`），两端数据目录可互相迁移。

## 1. 目录

```
windows/
└─ SmlServer/
   ├─ Program.cs              # 启动、端口、证书、HTTP→HTTPS、Cookie 认证
   ├─ appsettings.json        # 生产配置：443/80、证书、数据目录
   ├─ appsettings.Development.json  # 本地调试：8443/8080
   ├─ Models/                 # 协议、存储模型（与客户端字段一致）
   ├─ Services/               # 存储、密码、OTP、审批、连接管理、证书
   ├─ Endpoints/              # /ws/agent、教师端 API、后台 API
   └─ wwwroot/                # 教师端 / 与后台 /admin 两个前端
```

## 2. 运行要求

- Windows 10/11 或 Windows Server
- **.NET 8 ASP.NET Core Runtime**（开发机装 SDK 8/10 即可）
- 监听 **443/80 需要管理员权限**（Windows 的端口保留规则）；开发调试用 8443/8080 不需要

## 3. 快速开始

### 开发调试

```powershell
cd server\windows\SmlServer
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run
```

- 教师端：<https://localhost:8443/>
- 网站后台：<https://localhost:8443/admin>
- 初始账号：`admin` / `Admin@12345`（来自 `appsettings.json` 的 `Bootstrap`，首次启动写入 `data/users.json`）
- 自签证书会自动生成到 `data/smlapp-server.pfx`，浏览器会提示「不受信任」，点继续即可

### 生产部署

```powershell
dotnet publish SmlServer\SmlServer.csproj -c Release -r win-x64 --self-contained true -o D:\SmlServer
# 以管理员身份运行 D:\SmlServer\SmlServer.exe
```

默认监听 `0.0.0.0:443`（HTTPS）与 `0.0.0.0:80`（仅用于 301 跳转）。

## 4. 配置（appsettings.json）

| 键 | 说明 |
| --- | --- |
| `Server:HttpsPort` | HTTPS 端口，默认 443 |
| `Server:HttpPort` | HTTP 端口，默认 80；只用于 301 到 HTTPS，设为 0 可关闭 |
| `Server:DataDir` | 数据目录（相对程序目录），默认 `data` |
| `Server:CertificatePath` | PFX 路径（绝对路径或相对程序目录）；留空则用自签证书 |
| `Server:CertificatePassword` | PFX 密码 |
| `Server:HeartbeatTimeoutSeconds` | 心跳超时判离线，默认 90 秒 |
| `Bootstrap:AdminUsername` / `AdminPassword` | **仅首次启动**创建管理员时使用；之后以 `data/users.json` 为准 |
| `Server:SelfSignedCertificateName` | 自签证书文件名（data 目录内） |

> 部署后请立即登录后台修改管理员密码，并删除/修改 `Bootstrap` 中的初始密码。

### 证书

- 生产建议用受信任证书（Let's Encrypt / 学校 CA），转成 PFX：
  ```bash
  openssl pkcs12 -export -out server.pfx -inkey privkey.pem -in fullchain.pem
  ```
- 配置 `Server:CertificatePath`、`Server:CertificatePassword` 后重启。
- 未配置时自动生成 3 年期自签证书（`data/smlapp-server.pfx`，密码 `smlapp`），仅适合内网/联调。

### 反向代理（可选）

若用 Nginx/Caddy 终结 TLS，把 Kestrel 改成监听 127.0.0.1 的 HTTP 端口，并保留转发头（程序已启用 `ForwardedHeaders`，信任 loopback 代理）：

```nginx
server {
    listen 443 ssl;
    server_name sml.example.com;
    ssl_certificate     /etc/ssl/sml/fullchain.pem;
    ssl_certificate_key /etc/ssl/sml/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto https;
    }
}
```

## 5. 两个前端

| 入口 | 角色 | 功能 |
| --- | --- | --- |
| `/` | 教师 | 设备列表、喊人/通知/锁屏/关机/音量/截屏、策略下发、生成一次性密码、审批卸载/退出 |
| `/admin` | 管理员 | 设备注册与令牌重置、教室、账号、审计、证书状态 |

- 登录使用 Cookie（HttpOnly），有效期 7 天，滑动续期。
- 教师只能看到自己任教教室的设备；管理员可见全部。

## 6. 与 Windows 客户端对接

1. 后台 →「教室」创建班级（首次启动已自动创建「默认教室」）。
2. 后台 →「设备」新建设备，选择教室，得到 **deviceId（记录用）** 与 **接入令牌（只显示一次）**。
3. 一体机客户端 → 设置页：
   - 服务器地址：`wss://<服务器地址>/ws/agent`（生产必须 `wss://`）
   - 设备接入令牌：粘贴上一步的令牌
   - 保存 → 连接
4. 首次连接时，服务端把这条「待绑定」记录与客户端本地生成的 `deviceId` 绑定（列表状态从「待绑定」变为「在线」），并用 `hello` 上报的设备名/班级更新记录。
5. 之后即可在教师端看到设备并下发指令。

> 客户端本地 `deviceId` 是首次启动生成的 GUID，不需要手工填写；后台的 deviceId 只是记录占位，绑定后会被真实 ID 替换。
> 重置令牌会把设备恢复为「待绑定」，适合换机或重装。

## 7. 数据与备份

```
data/
├─ users.json            # 账号（PBKDF2 哈希 + 盐）
├─ classrooms.json
├─ devices.json          # 设备与令牌哈希（不存明文令牌）
├─ otps.json             # 一次性密码（只存 SHA-256）
├─ otp-usages.json       # 使用与失败记录
├─ approvals.json        # 卸载/退出审批
├─ screens/              # 设备上传的截图（jpg）
├─ events/               # 设备事件（按天 jsonl）
├─ audit/                # 审计日志（按天 jsonl）
└─ smlapp-server.pfx     # 自签证书
```

- 备份整个 `data` 目录即可；账号哈希与令牌哈希都在里面。
- 数据格式与 Linux Python 版一致，可直接迁移。

## 8. 常见问题

| 现象 | 原因 / 处理 |
| --- | --- |
| 启动报「拒绝访问」/ 端口占用 | 443/80 需要管理员运行；先 `netstat -ano \| findstr :443` 查占用 |
| 浏览器证书告警 | 正在使用自签证书；配置受信任 PFX 或在内网手动信任 |
| 客户端 401 | 令牌错误、设备被禁用，或令牌已被重置 |
| 教师看不到设备 | 设备未绑定教室，或教师账号没有该教室权限 |
| 在线审批收不到 | 设备与服务器连接已断；服务端会保留申请 10 分钟，重连后可重新发起 |

## 9. 协议

所有消息与 REST 接口与 Windows 客户端一致，详见 `../../windows/docs/API.md`（§13 一次性密码、§14 卸载授权、§15 退出授权）。
