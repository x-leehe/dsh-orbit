using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DshStatusWidget;

/// <summary>
/// 本地登录中转：浏览器访问 <c>http://127.0.0.1:端口/open?to=&lt;dsh 地址&gt;</c> 时，
/// 挂件用持久 secret 签一个 dsh 会话 cookie 一起返回，再 302 跳到目标页面。
///
/// 这样即使 dsh 是外部启动的（launch token 只存在它进程内存里，拿不到），
/// 或者会话中断后重启换了端口，浏览器打开也不会再报
/// “dsh web authentication required”。
/// </summary>
internal sealed class LoopbackAuthServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public LoopbackAuthServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>把普通地址包成「先登录再跳转」的地址。</summary>
    public string Wrap(string targetUrl) =>
        $"http://127.0.0.1:{Port}/open?to={Uri.EscapeDataString(targetUrl)}";

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false)))
            {
                // 丢到空行为止
            }

            if (requestLine is null || !requestLine.StartsWith("GET ", StringComparison.Ordinal))
            {
                await WriteAsync(stream, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n").ConfigureAwait(false);
                return;
            }

            var parts = requestLine.Split(' ');
            var target = parts.Length > 1 ? parts[1] : "/";

            if (!TryParseTarget(target, out var destination, out var authority))
            {
                await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n").ConfigureAwait(false);
                return;
            }

            var headers = new StringBuilder();
            headers.Append("HTTP/1.1 302 Found\r\n");
            headers.Append("Location: ").Append(destination).Append("\r\n");
            headers.Append("Cache-Control: no-store\r\n");

            if (authority is not null &&
                DshSession.TryCreateCookie(authority, TimeSpan.FromHours(12), out var cookieName, out var cookieValue))
            {
                headers.Append("Set-Cookie: ").Append(cookieName).Append('=').Append(cookieValue)
                    .Append("; Path=/; Max-Age=43200; HttpOnly; SameSite=Lax\r\n");
            }

            headers.Append("Content-Length: 0\r\nConnection: close\r\n\r\n");
            await WriteAsync(stream, headers.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"登录中转处理失败：{ex.Message}");
        }
    }

    private static bool TryParseTarget(string target, out string destination, out string? authority)
    {
        destination = string.Empty;
        authority = null;

        const string prefix = "/open?to=";
        if (!target.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var raw = Uri.UnescapeDataString(target[prefix.Length..]);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // 只允许跳回本机，避免变成开放重定向。
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!uri.IsLoopback)
        {
            return false;
        }

        destination = uri.ToString();
        authority = uri.Authority;
        return true;
    }

    private static async Task WriteAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 关闭时的异常忽略
        }

        _cts.Dispose();
    }
}

/// <summary>统一决定「在浏览器打开 / 复制地址」用哪个链接。</summary>
internal static class AuthLinks
{
    /// <summary>挂件自己启动 dsh 时捕获到的带 token 地址。</summary>
    public static string? TokenUrl { get; set; }

    /// <summary>本地登录中转端口（0 = 未启动）。</summary>
    public static int RelayPort { get; set; }

    /// <summary>是否已经有可用的免登录链接。</summary>
    public static bool HasAuthenticatedLink => TokenUrl is not null || RelayPort > 0;

    /// <summary>把普通地址换成能直接打开的地址。</summary>
    public static string For(string plainUrl)
    {
        if (!string.IsNullOrEmpty(TokenUrl))
        {
            return TokenUrl;
        }

        return RelayPort > 0
            ? $"http://127.0.0.1:{RelayPort}/open?to={Uri.EscapeDataString(plainUrl)}"
            : plainUrl;
    }
}
