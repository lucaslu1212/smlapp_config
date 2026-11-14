using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using SmlServer.Endpoints;
using SmlServer.Models;
using SmlServer.Services;

var builder = WebApplication.CreateBuilder(args);

var serverOptions = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
var bootstrap = builder.Configuration.GetSection("Bootstrap").Get<BootstrapOptions>() ?? new BootstrapOptions();

// 数据目录固定在程序目录下的 data/；发布后随程序走，便于整体迁移与备份。
var dataDir = Path.Combine(AppContext.BaseDirectory, serverOptions.DataDir);
Directory.CreateDirectory(dataDir);

using (var startupFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information)))
{
    var startupLogger = startupFactory.CreateLogger("startup");
    var (certificate, certificateInfo) = CertificateLoader.Load(dataDir, serverOptions, startupLogger);

    builder.WebHost.ConfigureKestrel(options =>
    {
        // 443：HTTPS（网页 + WSS）；80：只用于 301 跳转到 HTTPS
        options.ListenAnyIP(serverOptions.HttpsPort, listen => listen.UseHttps(certificate));
        if (serverOptions.HttpPort > 0 && serverOptions.HttpPort != serverOptions.HttpsPort)
            options.ListenAnyIP(serverOptions.HttpPort);
    });

    builder.Services.AddSingleton(serverOptions);
    builder.Services.AddSingleton(bootstrap);
    builder.Services.AddSingleton(certificateInfo);
    builder.Services.AddSingleton(new ServiceInfo());
    builder.Services.AddSingleton(_ => new DataStore(dataDir, bootstrap));
    builder.Services.AddSingleton(_ => new AuditService(dataDir));
    builder.Services.AddSingleton<ConnectionManager>();
    builder.Services.AddSingleton<OtpService>();
    builder.Services.AddSingleton<ApprovalService>();
    builder.Services.AddSingleton(_ => new DeviceEventService(dataDir));
    builder.Services.AddHostedService<ConnectionJanitor>();

    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "sml.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;

            // API 请求返回 401/403 而不是跳转，前端据此显示登录框
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                context.Response.Redirect("/");
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                context.Response.Redirect("/");
                return Task.CompletedTask;
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("TeacherOrAdmin", policy => policy.RequireRole(Roles.Teacher, Roles.Admin));
        options.AddPolicy("AdminOnly", policy => policy.RequireRole(Roles.Admin));
    });

    var app = builder.Build();

    var store = app.Services.GetRequiredService<DataStore>();
    var createdAdmin = store.Load();

    // 部署在 Nginx/Caddy 后面时（由代理终结 TLS），靠转发头识别原始协议，避免重定向死循环。
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    });

    // 80（或任意 HTTP 端口）→ HTTPS 301
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps)
        {
            var host = context.Request.Host.Host;
            var port = serverOptions.HttpsPort;
            var authority = port == 443 ? host : $"{host}:{port}";
            context.Response.Redirect(
                $"https://{authority}{context.Request.Path}{context.Request.QueryString}",
                permanent: true);
            return;
        }

        await next();
    });

    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/api/health", () => Results.Ok(new
    {
        ok = true,
        at = DateTimeOffset.UtcNow,
        version = typeof(ServiceInfo).Assembly.GetName().Version?.ToString(3),
    }));

    app.MapGet("/admin", () => Results.Redirect("/admin.html"));

    var connections = app.Services.GetRequiredService<ConnectionManager>();
    var otp = app.Services.GetRequiredService<OtpService>();
    var approvals = app.Services.GetRequiredService<ApprovalService>();
    var events = app.Services.GetRequiredService<DeviceEventService>();
    var audit = app.Services.GetRequiredService<AuditService>();

    app.Map("/ws/agent", async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("agent");
        await AgentEndpoint.HandleAsync(context, store, connections, otp, approvals, events, audit, serverOptions, logger);
    });

    AuthEndpoints.Map(app);
    TeacherEndpoints.Map(app);
    AdminEndpoints.Map(app);

    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup");
    logger.LogInformation("HTTPS 监听 0.0.0.0:{Https}；HTTP 监听 0.0.0.0:{Http}（301 → HTTPS）",
        serverOptions.HttpsPort, serverOptions.HttpPort);
    logger.LogInformation("数据目录：{Dir}", dataDir);
    logger.LogInformation("教师端：https://<服务器地址>:{Port}/    网站后台：https://<服务器地址>:{Port}/admin",
        serverOptions.HttpsPort, serverOptions.HttpsPort);
    logger.LogInformation("设备接入：wss://<服务器地址>:{Port}/ws/agent", serverOptions.HttpsPort);

    if (createdAdmin)
    {
        logger.LogWarning(
            "首次启动已创建管理员账号 {User}（初始密码见 appsettings.json 的 Bootstrap:AdminPassword），登录后台后请立即修改。",
            bootstrap.AdminUsername);
    }

    try
    {
        app.Run();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("服务启动失败：" + ex.Message);
        if (Math.Min(serverOptions.HttpsPort, serverOptions.HttpPort) < 1024)
        {
            Console.Error.WriteLine(
                "监听 443/80 需要管理员权限（Windows 请以管理员运行）或 CAP_NET_BIND_SERVICE（Linux）。" +
                "本地调试请设置 ASPNETCORE_ENVIRONMENT=Development，使用 8443/8080。");
        }
        throw;
    }
}
