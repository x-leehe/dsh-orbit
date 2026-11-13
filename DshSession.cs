using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DshStatusWidget;

/// <summary>
/// 给 WebView2 外壳签发 dsh 的会话 cookie。
///
/// dsh 的浏览器会话 cookie 是 <c>v1.&lt;base64url(payload)&gt;.&lt;base64url(HMAC-SHA256(secret, body))&gt;</c>，
/// 名字是 <c>dsh-auth-" + base64url(sha256(authority))&gt;`，secret 存在
/// <c>~/.dsh/.credentials.yaml</c> 的 <c>client-connection/browser-session</c> 记录里。
/// 这里只读本机文件、只签本机回环地址，等价于使用 dsh 启动时打印的 token。
/// </summary>
internal static class DshSession
{
    private const string CookiePrefix = "dsh-auth-";
    private const int SecretBytes = 32;

    /// <summary>按 authority（host:port）生成会话 cookie；读不到 secret 时返回 false。</summary>
    public static bool TryCreateCookie(string authority, TimeSpan lifetime, out string name, out string value)
    {
        name = "";
        value = "";

        var secretText = ReadSecret();
        if (secretText is null)
        {
            return false;
        }

        byte[] key;
        try
        {
            key = Base64UrlDecode(secretText);
        }
        catch
        {
            return false;
        }

        if (key.Length != SecretBytes)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            version = 1,
            authority,
            issuedAt = now.ToUnixTimeMilliseconds(),
            expiresAt = now.Add(lifetime).ToUnixTimeMilliseconds(),
        });

        var body = Base64Url(Encoding.UTF8.GetBytes(payload));
        using var hmac = new HMACSHA256(key);
        var signature = hmac.ComputeHash(Encoding.ASCII.GetBytes(body));

        name = CookiePrefix + Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(authority)));
        value = $"v1.{body}.{Base64Url(signature)}";
        return true;
    }

    /// <summary>从凭据文件里取 base64url 形式的 secret。</summary>
    private static string? ReadSecret()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }

            var path = Path.Combine(home, ".credentials.yaml");
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllLines(path);
            var inside = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("client-connection/browser-session:", StringComparison.Ordinal))
                {
                    inside = true;
                    continue;
                }

                if (!inside)
                {
                    continue;
                }

                if (line.StartsWith("secret:", StringComparison.Ordinal))
                {
                    var secret = line["secret:".Length..].Trim().Trim('"', '\'');
                    return secret.Length == 0 ? null : secret;
                }

                // 离开这条记录（下一个同级 key）就停止。
                if (!raw.StartsWith(" ", StringComparison.Ordinal) && line.Length > 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"读取会话 secret 失败：{ex.Message}");
        }

        return null;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - (text.Length % 4)) % 4;
        return Convert.FromBase64String(text + new string('=', padding));
    }
}
