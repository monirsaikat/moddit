using System.Diagnostics;
using System.IO.Pipes;
using StreamJsonRpc;

namespace Modit.Engine.Client;

public sealed record EngineClientOptions
{
    /// <summary>Full path of Modit.Engine.Host.exe. Null locates it next to the running application.</summary>
    public string? HostPath { get; init; }

    /// <summary>Hard private-memory limit of the host process (Job Object). 0 disables the limit.</summary>
    public long MemoryLimitBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Client for one engine host process. Starts the host on first use inside a Job Object, and when the
/// host dies (malformed file, memory limit) transparently starts a new one, re-opens the documents that
/// were open and re-issues the failed call once. Callers never see a host crash unless it repeats.
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private readonly EngineClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, OpenDocumentRequest> _openDocuments = [];
    private Connection? _connection;
    private bool _disposed;

    public EngineClient(EngineClientOptions? options = null)
    {
        _options = options ?? new EngineClientOptions();
    }

    /// <summary>Number of times a host had to be restarted after it died.</summary>
    public int RestartCount { get; private set; }

    public static string LocateHost()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("MODIT_ENGINE_HOST");
        if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "host", "Modit.Engine.Host.exe"),
            Path.Combine(baseDir, "Modit.Engine.Host.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new EngineException($"Modit.Engine.Host.exe was not found. Looked in: {string.Join("; ", candidates)}");
    }

    public Task<HostInfo> PingAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((service, ct) => service.PingAsync(ct), cancellationToken);

    public async Task<DocumentInfo> OpenDocumentAsync(
        string path, string? password = null, CancellationToken cancellationToken = default)
    {
        var request = new OpenDocumentRequest(Guid.NewGuid(), Path.GetFullPath(path), password);
        DocumentInfo info = await InvokeAsync((service, ct) => service.OpenDocumentAsync(request, ct), cancellationToken)
            .ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _openDocuments[request.DocumentId] = request;
        }
        finally
        {
            _gate.Release();
        }

        return info;
    }

    public async Task CloseDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _openDocuments.Remove(documentId);
        }
        finally
        {
            _gate.Release();
        }

        await InvokeAsync(
            async (service, ct) =>
            {
                await service.CloseDocumentAsync(documentId, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renders a whole page at <paramref name="scale"/> device pixels per PDF point.</summary>
    public Task<RenderedImage> RenderPageAsync(
        DocumentInfo document, int pageIndex, double scale, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageIndex < 0 || pageIndex >= document.PageCount)
        {
            throw new ModitException(
                ErrorCode.PdfPageOutOfRange, $"Page {pageIndex + 1} is out of range (1-{document.PageCount}).");
        }

        PageInfo page = document.Pages[pageIndex];
        int width = Math.Max(1, (int)Math.Round(page.WidthPt * scale));
        int height = Math.Max(1, (int)Math.Round(page.HeightPt * scale));
        return RenderRegionAsync(document.DocumentId, pageIndex, scale, 0, 0, width, height, cancellationToken);
    }

    /// <summary>Renders one rectangle (a tile) of a page that is scaled by <paramref name="scale"/>.</summary>
    public async Task<RenderedImage> RenderRegionAsync(
        Guid documentId, int pageIndex, double scale, int sourceXPx, int sourceYPx, int widthPx, int heightPx,
        CancellationToken cancellationToken = default)
    {
        if (widthPx <= 0 || heightPx <= 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, "Render size must be positive.");
        }

        SharedBuffer buffer = SharedBuffer.Create((long)widthPx * heightPx * 4);
        try
        {
            var request = new RenderRequest(
                documentId, pageIndex, scale, sourceXPx, sourceYPx, widthPx, heightPx, buffer.Name,
                RenderAnnotations: true);
            RenderResult result = await InvokeAsync((service, ct) => service.RenderPageAsync(request, ct), cancellationToken)
                .ConfigureAwait(false);
            return new RenderedImage(buffer, result.WidthPx, result.HeightPx, result.StrideBytes, result.ElapsedMilliseconds);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public Task<string> GetPageTextAsync(Guid documentId, int pageIndex, CancellationToken cancellationToken = default) =>
        InvokeAsync((service, ct) => service.GetPageTextAsync(documentId, pageIndex, ct), cancellationToken);

    public Task<QpdfJobResult> RunQpdfJobAsync(string jobJson, CancellationToken cancellationToken = default) =>
        InvokeAsync((service, ct) => service.RunQpdfJobAsync(jobJson, ct), cancellationToken);

    public Task<ImageInfoResult> GetImageInfoAsync(string path, CancellationToken cancellationToken = default) =>
        InvokeAsync((service, ct) => service.GetImageInfoAsync(Path.GetFullPath(path), ct), cancellationToken);

    public Task<ImageConvertResult> ConvertImageAsync(ImageConvertRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync((service, ct) => service.ConvertImageAsync(request, ct), cancellationToken);

    public Task<ImagesToPdfResult> ImagesToPdfAsync(ImagesToPdfRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync((service, ct) => service.ImagesToPdfAsync(request, ct), cancellationToken);

    public Task<PdfEditResult> EditPdfAsync(PdfEditRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync((service, ct) => service.EditPdfAsync(request, ct), cancellationToken);

    /// <summary>Test hook: kills the current host abruptly. The next call restarts it.</summary>
    public async Task CrashHostForTestingAsync()
    {
        Connection? connection = _connection;
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.Service.CrashAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            // Expected: the host died while handling the call.
        }

        await connection.Process.WaitForExitAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<T> InvokeAsync<T>(
        Func<IEngineService, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // One transparent retry after a host death. A second death is reported: the input is the likely cause.
        for (int attempt = 0; ; attempt++)
        {
            Connection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await call(connection.Service, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteInvocationException ex) when (ex.ErrorCode >= EngineRpc.ErrorCodeBase)
            {
                throw new ModitException((ErrorCode)(ex.ErrorCode - EngineRpc.ErrorCodeBase), ex.Message, ex);
            }
            catch (RemoteInvocationException ex)
            {
                throw new EngineException(ErrorCode.Unknown, $"The engine reported an unexpected error: {ex.Message}", ex);
            }
            catch (Exception ex) when (IsConnectionFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                await DropConnectionAsync(connection).ConfigureAwait(false);
                if (attempt >= 1)
                {
                    throw new EngineException(
                        ErrorCode.EngineCrashed, "The engine process stopped unexpectedly while handling this request.", ex);
                }
            }
        }
    }

    private static bool IsConnectionFailure(Exception ex) =>
        ex is ConnectionLostException or IOException or ObjectDisposedException;

    private async Task DropConnectionAsync(Connection dead)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_connection, dead))
            {
                _connection = null;
                RestartCount++;
            }

            dead.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Connection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsAlive: true } alive)
            {
                return alive;
            }

            if (_connection is not null)
            {
                // The host died between calls (crash, memory limit, killed by the user).
                _connection.Dispose();
                _connection = null;
                RestartCount++;
            }

            Connection fresh = await Connection.StartAsync(_options, cancellationToken).ConfigureAwait(false);
            try
            {
                // Restore the documents the previous host had open, under their original ids.
                foreach (OpenDocumentRequest request in _openDocuments.Values)
                {
                    await fresh.Service.OpenDocumentAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                fresh.Dispose();
                throw;
            }

            _connection = fresh;
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Connection : IDisposable
    {
        private readonly JobObject _job;
        private readonly NamedPipeClientStream _pipe;
        private readonly JsonRpc _rpc;

        private Connection(Process process, JobObject job, NamedPipeClientStream pipe, JsonRpc rpc, IEngineService service)
        {
            Process = process;
            _job = job;
            _pipe = pipe;
            _rpc = rpc;
            Service = service;
        }

        public Process Process { get; }

        public IEngineService Service { get; }

        public bool IsAlive => !_rpc.IsDisposed && !_rpc.Completion.IsCompleted && !Process.HasExited;

        public static async Task<Connection> StartAsync(EngineClientOptions options, CancellationToken cancellationToken)
        {
            string hostPath = options.HostPath ?? LocateHost();
            string pipeName = $"modit-engine-{Guid.NewGuid():N}";

            var startInfo = new ProcessStartInfo(hostPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--parent");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            JobObject? job = null;
            Process? process = null;
            NamedPipeClientStream? pipe = null;
            try
            {
                job = new JobObject(options.MemoryLimitBytes);
                process = Process.Start(startInfo)
                    ?? throw new EngineException("The engine host process could not be started.");
                job.Assign(process);

                pipe = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(options.StartTimeout);
                    try
                    {
                        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new EngineException(
                            ErrorCode.EngineTimeout,
                            $"The engine host did not start within {options.StartTimeout.TotalSeconds:0} s.");
                    }
                }

                var handler = new LengthHeaderMessageHandler(pipe, pipe, new MessagePackFormatter());
                var rpc = new JsonRpc(handler);
                IEngineService service = rpc.Attach<IEngineService>();
                rpc.StartListening();
                return new Connection(process, job, pipe, rpc, service);
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                if (process is not null)
                {
                    TryKill(process);
                    process.Dispose();
                }

                job?.Dispose();
                if (ex is ModitException or OperationCanceledException)
                {
                    throw;
                }

                throw new EngineException($"The engine host could not be started: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            _rpc.Dispose();
            _pipe.Dispose();
            TryKill(Process);
            Process.Dispose();
            _job.Dispose();
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }
}
