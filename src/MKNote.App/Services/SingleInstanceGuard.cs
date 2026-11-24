using System.IO.Pipes;
using System.Text;

namespace MKNote.App.Services;

/// <summary>
/// 严格单实例守护服务：结合命名互斥量（Mutex）与命名管道（NamedPipe）唤醒通道。
/// 确保应用在系统内禁止双开或多开；当用户重复启动程序时，自动唤醒已在前台或隐藏于托盘的主窗口。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\MKNoteNext_StrictSingleInstance_Mutex_9F4E2A7C";
    private const string PipeName = "MKNoteNext_StrictSingleInstance_Pipe_9F4E2A7C";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _ownsMutex;

    public bool TryAcquirePrimaryInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        return createdNew;
    }

    public static void SignalExistingInstanceToWake()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1200);
            var payload = "WAKE"u8.ToArray();
            client.Write(payload, 0, payload.Length);
            client.Flush();
        }
        catch
        {
            // Ignore if existing instance is still initializing
        }
    }

    public void StartListeningForWakeups(Action onWakeup)
    {
        if (!_ownsMutex) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);
                    var buf = new byte[64];
                    var n = await server.ReadAsync(buf, token);
                    var msg = Encoding.UTF8.GetString(buf, 0, n);
                    if (msg.Contains("WAKE", StringComparison.Ordinal))
                    {
                        onWakeup();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
    }
}
