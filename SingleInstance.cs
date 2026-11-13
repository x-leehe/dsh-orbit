using System.IO.Pipes;

namespace DshStatusWidget;

/// <summary>
/// 单实例之间的命令行通道：`DshStatus --show-window` 通过命名管道通知已运行的挂件开窗口。
/// </summary>
internal static class SingleInstance
{
    private const string PipeName = "DSH-Orbit.command";

    /// <summary>向已运行的实例发命令；没有实例在听就返回 false。</summary>
    public static bool TrySend(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>后台监听命令，回调在管道线程上触发，调用方需自行切回 UI 线程。</summary>
    public static void StartServer(Action<string> onCommand, CancellationToken token)
    {
        _ = Task.Run(
            async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                        using var reader = new StreamReader(server);
                        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            onCommand(line.Trim());
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppPaths.Log($"命令通道异常：{ex.Message}");
                        try
                        {
                            await Task.Delay(500, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            },
            token);
    }
}
