using System.Diagnostics;
using System.Net.Http;

namespace DshOrbit;

/// <summary>无界面冒烟测试：拉起 dsh → 抓 token URL → 停止，验证挂件的启动链路。</summary>
internal static class Program
{
    private const int TestPort = 3998;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var failures = 0;

        var exe = DshProcessHost.LocateDshExe(new WidgetConfig());
        Console.WriteLine($"dsh.exe : {exe ?? "(未找到)"}");
        if (exe is null)
        {
            return 1;
        }

        var config = new WidgetConfig
        {
            Profile = "widgettest",
            LaunchArguments = $"--profile widgettest --no-open --port {TestPort}",
            WorkingDirectory = Path.GetTempPath(),
        };

        Check("0. 准备一次性测试 profile（复制 web 并剥掉 QQ 机器人）", EnsureTestProfile(out var profileError),
            ref failures);
        if (profileError is not null)
        {
            Console.WriteLine($"      {profileError}");
        }

        Check("1. 启动前端口空闲", !IsPortListening(TestPort), ref failures);

        using var host = new DshProcessHost();
        var started = host.Start(config, out var error);
        Check($"2. 进程启动（{error ?? "ok"}）", started, ref failures);
        if (!started)
        {
            return 1;
        }

        string? url = null;
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (!string.IsNullOrEmpty(host.AuthenticatedUrl))
            {
                url = host.AuthenticatedUrl;
                break;
            }

            await Task.Delay(500);
        }

        Console.WriteLine($"   耗时 {waited.Elapsed.TotalSeconds:0.0}s  URL={(url is null ? "(未捕获)" : MaskToken(url))}");
        Check("3. 捕获到带 token 的访问地址", url is not null && url.Contains("token="), ref failures);

        if (url is not null)
        {
            Check("4. URL 端口正确", url.Contains($":{TestPort}"), ref failures);

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                Check($"5. 用该 URL 打开首页得到 200（实际 {(int)response.StatusCode}，{body.Length} 字节）",
                    response.IsSuccessStatusCode && body.Contains("<div id=\"root\"", StringComparison.OrdinalIgnoreCase),
                    ref failures);
            }
            catch (Exception ex)
            {
                Check($"5. 打开首页失败：{ex.Message}", false, ref failures);
            }
        }

        Check("6. 端口在监听", IsPortListening(TestPort), ref failures);

        var version = await DshInfo.ReadVersionAsync(exe, TimeSpan.FromSeconds(20), CancellationToken.None);
        Console.WriteLine($"   dsh 版本 : {version}");
        Check("7. 读到 dsh 版本", !string.IsNullOrWhiteSpace(version), ref failures);

        var installed = DshInfo.ReadInstalledPlugins("web");
        Console.WriteLine($"   已安装插件（web）: {installed?.Total} 个 -> {string.Join(", ", installed?.Names ?? Array.Empty<string>())}");
        Check("8. 已安装插件清单可读（同「插件」页口径）", installed is { Total: >= 0 }, ref failures);

        var counts = await DshInfo.CountPluginsAsync(exe, "widgettest", TimeSpan.FromSeconds(40), CancellationToken.None);
        Console.WriteLine($"   （参考）组合配置条目数 : {counts?.Total} 项 / 显式停用 {counts?.Disabled}");

        host.Stop();
        await Task.Delay(1500);
        Check("9. 停止后端口释放", !IsPortListening(TestPort), ref failures);

        Console.WriteLine(failures == 0 ? "\n全部通过 ✅" : $"\n失败 {failures} 项 ❌");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 准备一次性测试 profile：复制 web，去掉 QQ 机器人（避免二次登录影响正在用的实例），
    /// 只保留 base + web-app 两个组合包。
    /// </summary>
    private static bool EnsureTestProfile(out string? error)
    {
        error = null;

        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }

        var source = Path.Combine(home, "profiles", "web");
        var target = Path.Combine(home, "profiles", "widgettest");

        if (Directory.Exists(target))
        {
            return true;
        }

        if (!Directory.Exists(source))
        {
            error = $"找不到源 profile：{source}";
            return false;
        }

        try
        {
            CopyDirectory(source, target);

            File.WriteAllText(Path.Combine(target, "package.json"), """
            {
              "name": "dsh-profile-widgettest",
              "private": true,
              "dsh": {
                "profile": {
                  "bundles": [
                    "@deepseek-ai/dsh-base",
                    "@deepseek-ai/dsh-web-app"
                  ]
                }
              },
              "dependencies": {}
            }
            """);

            File.WriteAllText(Path.Combine(target, "cordis.patch.yml"), """
            # 冒烟测试用 profile：从 web 复制，去掉会对外连接的插件。
            - id: permission
              name: "@deepseek-ai/dsh-permission-presets"
              config:
                presets:
                  read-only:
                    sandbox: read-only
                    approval: ask
                  workspace-write:
                    sandbox: workspace-write
                    approval: ask
                  danger-full-access:
                    sandbox: danger-full-access
                    approval: never
                defaultPreset: danger-full-access
            - id: agent-default-model
              name: "@deepseek-ai/dsh-agent-default-model"
              config:
                provider: deepseek-official
                model: deepseek-flash
                reasoningEffort: high
            """);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void Check(string name, bool ok, ref int failures)
    {
        Console.WriteLine($"{(ok ? "  ok " : "  FAIL")} {name}");
        if (!ok)
        {
            failures++;
        }
    }

    private static string MaskToken(string url)
    {
        var index = url.IndexOf("token=", StringComparison.Ordinal);
        return index < 0 ? url : url[..(index + 6)] + "***";
    }

    private static bool IsPortListening(int port) =>
        Native.ListenLoopbackPorts().Any(row => row.Port == port);
}
