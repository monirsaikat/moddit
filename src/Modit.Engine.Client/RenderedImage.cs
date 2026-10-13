namespace Modit.Engine.Client;

/// <summary>
/// A rendered page region: BGRA, 8 bits per channel, rows top to bottom. The pixels live in shared
/// memory owned by this object; dispose it once they have been uploaded or encoded.
/// </summary>
public sealed class RenderedImage : IDisposable
{
    private readonly SharedBuffer _buffer;

    internal RenderedImage(SharedBuffer buffer, int widthPx, int heightPx, int strideBytes, double renderMilliseconds)
    {
        _buffer = buffer;
        WidthPx = widthPx;
        HeightPx = heightPx;
        StrideBytes = strideBytes;
        RenderMilliseconds = renderMilliseconds;
    }

    public int WidthPx { get; }

    public int HeightPx { get; }

    public int StrideBytes { get; }

    /// <summary>Time PDFium spent rendering inside the host, excluding IPC.</summary>
    public double RenderMilliseconds { get; }

    public ReadOnlySpan<byte> Pixels => _buffer.Span[..(StrideBytes * HeightPx)];

    public void Dispose() => _buffer.Dispose();
}
