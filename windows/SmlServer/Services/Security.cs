using System.Security.Cryptography;
using System.Text;

namespace SmlServer.Services;

/// <summary>密码与令牌的哈希工具。</summary>
public static class Security
{
    private const int Pbkdf2Iterations = 100_000;

    public static (string Hash, string Salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string hashBase64, string saltBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>生成设备接入令牌（32 字节，base64url 无填充）。</summary>
    public static string NewDeviceToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool TokenMatches(string token, string expectedHexHash)
    {
        try
        {
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var expected = Convert.FromHexString(expectedHexHash);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>与 Windows 客户端一致的归一化：去空白/连字符后转大写。</summary>
    public static string NormalizeOtpCode(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch) || ch == '-') continue;
            sb.Append(ch);
        }
        return sb.ToString().ToUpperInvariant();
    }

    public static string HashOtpCode(string normalized)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();

    /// <summary>生成易读的一次性密码明文：8 位，去除 0/O/1/I/L。</summary>
    public static string NewOtpPlain()
    {
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}
