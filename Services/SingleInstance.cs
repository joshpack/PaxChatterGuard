using System.IO.Pipes;

namespace PaxChatterGuard.Services;

/// <summary>
/// "Open the window" from a second copy: the running copy listens on a named pipe, and a copy started while it runs
/// just connects to it and exits. Connecting is the whole message, so it also reaches a copy running as administrator
/// (everyone may connect to read).
/// </summary>
public static class SingleInstance
{
    public static string DefaultPipeName => "PaxChatterGuard.show." + Environment.UserName;

    /// <summary>Tells the running copy to open its window. True if it answered.</summary>
    public static bool SignalRunning(string? pipeName = null, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.In);
            client.Connect(timeoutMs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="show"/> (on a worker thread) each time another copy is started. Dispose to stop.</summary>
    public static IDisposable Listen(Action show, string? pipeName = null)
    {
        var cts = new CancellationTokenSource();
        var name = pipeName ?? DefaultPipeName;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(cts.Token);
                    show();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Pipe busy or broken: try again shortly.
                    try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { return; }
                }
            }
        });
        return cts;
    }
}
