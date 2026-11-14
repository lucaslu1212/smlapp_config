using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SmlServer.Models;

namespace SmlServer.Services;

/// <summary>
/// HTTPS 证书加载：
/// 1. 配置了 Server:CertificatePath（PFX）优先使用；
/// 2. 否则使用 data 目录下的自签证书（首次自动生成），方便内网/联调先跑起来。
/// 生产环境请配置受信任证书（如 Let's Encrypt 签发的 PFX），浏览器才不会告警。
/// </summary>
public static class CertificateLoader
{
    private const string SelfSignedPassword = "smlapp";

    public static (X509Certificate2 Certificate, CertificateInfo Info) Load(
        string dataDir, ServerOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            var path = Path.IsPathRooted(options.CertificatePath)
                ? options.CertificatePath
                : Path.Combine(AppContext.BaseDirectory, options.CertificatePath);

            if (File.Exists(path))
            {
                var cert = new X509Certificate2(path, options.CertificatePassword,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                logger.LogInformation("已加载证书 {Path}，主体 {Subject}，到期 {NotAfter:yyyy-MM-dd}",
                    path, cert.Subject, cert.NotAfter);
                return (cert, Describe(cert, "pfx"));
            }

            logger.LogWarning("证书文件不存在：{Path}，回退到自签证书", path);
        }

        var pfxPath = Path.Combine(dataDir, options.SelfSignedCertificateName);
        if (File.Exists(pfxPath))
        {
            var cached = new X509Certificate2(pfxPath, SelfSignedPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            logger.LogInformation("已加载自签证书 {Path}，到期 {NotAfter:yyyy-MM-dd}", pfxPath, cached.NotAfter);
            return (cached, Describe(cached, "self-signed"));
        }

        var generated = CreateSelfSigned();
        File.WriteAllBytes(pfxPath, generated.Export(X509ContentType.Pfx, SelfSignedPassword));
        logger.LogWarning(
            "未配置证书，已生成自签证书 {Path}。浏览器访问会提示不受信任；生产环境请配置 Server:CertificatePath。",
            pfxPath);

        var loaded = new X509Certificate2(generated.Export(X509ContentType.Pfx, SelfSignedPassword),
            SelfSignedPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        return (loaded, Describe(loaded, "self-signed"));
    }

    private static CertificateInfo Describe(X509Certificate2 cert, string source) => new()
    {
        Subject = cert.Subject,
        Thumbprint = cert.Thumbprint,
        NotBefore = cert.NotBefore,
        NotAfter = cert.NotAfter,
        IsSelfSigned = cert.Subject == cert.Issuer,
        Source = source,
    };

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SmlApp Server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(3));

        // 重新导入一次，确保私钥可用于 Kestrel
        var pfx = cert.Export(X509ContentType.Pfx, SelfSignedPassword);
        cert.Dispose();
        return new X509Certificate2(pfx, SelfSignedPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
}
