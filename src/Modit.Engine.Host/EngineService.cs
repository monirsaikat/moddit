using System.Runtime.InteropServices;
using Modit.Engine.Host.Engines;
using StreamJsonRpc;

namespace Modit.Engine.Host;

/// <summary>RPC target: adapts engines to <see cref="IEngineService"/> and translates failures.</summary>
internal sealed class EngineService(PdfiumEngine pdfium) : IEngineService
{
    public Task<HostInfo> PingAsync(CancellationToken cancellationToken)
    {
        // Environment.WorkingSet is a single cheap query; System.Diagnostics.Process would enumerate
        // every process on the machine and turn a heartbeat into a multi-millisecond call.
        return Task.FromResult(new HostInfo(
            Environment.ProcessId,
            RuntimeInformation.ProcessArchitecture.ToString(),
            pdfium.OpenDocumentCount,
            Environment.WorkingSet));
    }

    public Task<DocumentInfo> OpenDocumentAsync(OpenDocumentRequest request, CancellationToken cancellationToken) =>
        Guard(() => pdfium.OpenAsync(request, cancellationToken));

    public Task CloseDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        Guard(async () =>
        {
            await pdfium.CloseAsync(documentId, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public Task<RenderResult> RenderPageAsync(RenderRequest request, CancellationToken cancellationToken) =>
        Guard(() => pdfium.RenderAsync(request, cancellationToken));

    public Task<string> GetPageTextAsync(Guid documentId, int pageIndex, CancellationToken cancellationToken) =>
        Guard(() => pdfium.GetPageTextAsync(documentId, pageIndex, cancellationToken));

    public Task<QpdfJobResult> RunQpdfJobAsync(string jobJson, CancellationToken cancellationToken) =>
        Guard(() => QpdfEngine.RunJobAsync(jobJson, cancellationToken));

    public Task<ImageInfoResult> GetImageInfoAsync(string path, CancellationToken cancellationToken) =>
        Guard(() => Task.Run(() => ImageEngine.GetInfo(path), cancellationToken));

    public Task<ImageConvertResult> ConvertImageAsync(ImageConvertRequest request, CancellationToken cancellationToken) =>
        Guard(() => Task.Run(() => ImageEngine.Convert(request, cancellationToken), cancellationToken));

    public Task<ImagesToPdfResult> ImagesToPdfAsync(ImagesToPdfRequest request, CancellationToken cancellationToken) =>
        Guard(() => Task.Run(() => ImagesToPdf.Write(request, cancellationToken), cancellationToken));

    public Task CrashAsync(CancellationToken cancellationToken)
    {
        Environment.FailFast("Modit.Engine.Host: crash requested by client (test hook).");
        return Task.CompletedTask;
    }

    /// <summary>Carries the stable <see cref="ErrorCode"/> across the pipe as a JSON-RPC error code.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (ModitException ex)
        {
            throw new LocalRpcException(ex.Message) { ErrorCode = EngineRpc.ErrorCodeBase + (int)ex.Code };
        }
    }
}
