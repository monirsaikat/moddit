using System.Diagnostics;
using System.IO.Pipes;
using Modit.Engine.Host.Engines;
using StreamJsonRpc;

namespace Modit.Engine.Host;

/// <summary>
/// Entry point of the sandboxed engine host. Usage: <c>Modit.Engine.Host.exe --pipe NAME --parent PID</c>.
/// The host serves exactly one client over one named pipe and exits when the pipe closes or the
/// parent process dies, so it can never outlive the application that started it.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBadArguments = 2;
    private const int ExitNoClient = 3;

    private static async Task<int> Main(string[] args)
    {
        string? pipeName = GetOption(args, "--pipe");
        if (pipeName is null)
        {
            return ExitBadArguments;
        }

        if (int.TryParse(GetOption(args, "--parent"), out int parentPid))
        {
            WatchParent(parentPid);
        }

        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 1 << 20,
            outBufferSize: 1 << 20);

        using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ExitNoClient;
            }
        }

        using var pdfiumThread = new PdfiumThread();
        using var pdfium = new PdfiumEngine(pdfiumThread);
        var service = new EngineService(pdfium);

        var handler = new LengthHeaderMessageHandler(pipe, pipe, new MessagePackFormatter());
        using var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget<IEngineService>(service, null);
        rpc.StartListening();

        try
        {
            await rpc.Completion.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ConnectionLostException or OperationCanceledException)
        {
            // The client went away; that is the normal way for a host to end.
        }

        return ExitOk;
    }

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void WatchParent(int parentPid)
    {
        try
        {
            Process parent = Process.GetProcessById(parentPid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => Environment.Exit(ExitOk);
            if (parent.HasExited)
            {
                Environment.Exit(ExitOk);
            }
        }
        catch (ArgumentException)
        {
            // Parent already gone.
            Environment.Exit(ExitOk);
        }
    }
}
