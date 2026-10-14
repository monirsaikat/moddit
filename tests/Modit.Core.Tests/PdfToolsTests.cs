using Modit.Core.Pdf;
using Modit.Engine;
using Modit.Engine.Client;
using Modit.TestSupport;

namespace Modit.Core.Tests;

public sealed class PdfToolsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modit-tests", Guid.NewGuid().ToString("N"));
    private readonly EngineClient _engine = new();
    private PdfService _service = null!;

    public ValueTask InitializeAsync()
    {
        _service = new PdfService(_engine);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        TestDirectory.Delete(_dir);
    }

    [Fact]
    public async Task Split_writes_numbered_files_with_the_right_pages()
    {
        string source = TestPdf.Create(_dir, "report.pdf", 7, "Report");
        string outDir = Path.Combine(_dir, "parts");
        var progress = new List<double>();

        PdfMultiResult result = await _service.SplitAsync(
            source, pagesPerFile: 3, outDir, progress: new SyncProgress(progress.Add));

        Assert.Equal(
            ["report_001.pdf", "report_002.pdf", "report_003.pdf"],
            result.OutputPaths.Select(Path.GetFileName));
        Assert.Equal([3, 3, 1], await PageCountsAsync(result.OutputPaths));
        Assert.Contains("Report page 7", await TextAsync(result.OutputPaths[2], 0), StringComparison.Ordinal);
        Assert.Equal(1.0, progress[^1]);
        Assert.Empty(Directory.GetFiles(outDir, "*.modit-tmp"));
    }

    [Fact]
    public async Task Extract_keeps_only_the_selected_pages_in_the_written_order()
    {
        string source = TestPdf.Create(_dir, "book.pdf", 6, "Book");
        string output = Path.Combine(_dir, "picked.pdf");

        PdfOperationResult result = await _service.ExtractAsync(source, PageRange.Parse("5,2"), output);

        Assert.Equal(2, result.PageCount);
        Assert.Contains("Book page 5", await TextAsync(output, 0), StringComparison.Ordinal);
        Assert.Contains("Book page 2", await TextAsync(output, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotate_turns_only_the_selected_pages()
    {
        string source = TestPdf.Create(_dir, "portrait.pdf", 3);
        string output = Path.Combine(_dir, "rotated.pdf");

        await _service.RotateAsync(source, 90, PageRange.Parse("2"), output);

        DocumentInfo info = await _service.GetInfoAsync(output);
        Assert.Equal((612f, 792f), (info.Pages[0].WidthPt, info.Pages[0].HeightPt));
        Assert.Equal((792f, 612f), (info.Pages[1].WidthPt, info.Pages[1].HeightPt));
        Assert.Equal((612f, 792f), (info.Pages[2].WidthPt, info.Pages[2].HeightPt));
    }

    [Fact]
    public async Task Rotate_rejects_angles_that_are_not_quarter_turns()
    {
        string source = TestPdf.Create(_dir, "a.pdf", 1);

        ModitException ex = await Assert.ThrowsAsync<ModitException>(
            () => _service.RotateAsync(source, 45, null, Path.Combine(_dir, "x.pdf")));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Protect_then_unlock_round_trips_and_enforces_the_password()
    {
        string source = TestPdf.Create(_dir, "plain.pdf", 2, "Secret");
        string locked = Path.Combine(_dir, "locked.pdf");
        string unlocked = Path.Combine(_dir, "unlocked.pdf");

        await _service.ProtectAsync(
            source, new ProtectOptions { UserPassword = "pa55", AllowCopying = false, AllowPrinting = false }, locked);

        ModitException needs = await Assert.ThrowsAsync<ModitException>(() => _service.GetInfoAsync(locked));
        Assert.Equal(ErrorCode.PdfEncryptedNeedsPassword, needs.Code);
        Assert.True((await _service.GetInfoAsync(locked, "pa55")).IsEncrypted);

        ModitException wrong = await Assert.ThrowsAsync<ModitException>(
            () => _service.UnlockAsync(locked, "nope", unlocked));
        Assert.Equal(ErrorCode.PdfWrongPassword, wrong.Code);
        Assert.False(File.Exists(unlocked));

        await _service.UnlockAsync(locked, "pa55", unlocked);
        DocumentInfo open = await _service.GetInfoAsync(unlocked);
        Assert.False(open.IsEncrypted);
        Assert.Contains("Secret page 2", await TextAsync(unlocked, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protect_requires_a_password()
    {
        string source = TestPdf.Create(_dir, "a.pdf", 1);

        ModitException ex = await Assert.ThrowsAsync<ModitException>(
            () => _service.ProtectAsync(source, new ProtectOptions { UserPassword = string.Empty }, Path.Combine(_dir, "x.pdf")));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Optimize_keeps_content_and_reports_sizes()
    {
        string source = TestPdf.Create(_dir, "big.pdf", 40, "Optimize me please, this text repeats on every page");
        string output = Path.Combine(_dir, "small.pdf");

        PdfOperationResult result = await _service.OptimizeAsync(source, output);

        Assert.Equal(40, result.PageCount);
        Assert.Equal(new FileInfo(source).Length, result.InputBytes);
        Assert.Equal(new FileInfo(output).Length, result.OutputBytes);
        Assert.True(result.OutputBytes < result.InputBytes, $"{result.OutputBytes} is not smaller than {result.InputBytes}");
        Assert.Contains("page 40", await TextAsync(output, 39), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_images_writes_one_png_per_selected_page_at_the_requested_resolution()
    {
        string source = TestPdf.Create(_dir, "slides.pdf", 4);
        string outDir = Path.Combine(_dir, "png");

        PdfMultiResult result = await _service.ExportImagesAsync(source, PageRange.Parse("2,4"), dpi: 36, outDir);

        Assert.Equal(["slides_p002.png", "slides_p004.png"], result.OutputPaths.Select(Path.GetFileName));
        byte[] png = await File.ReadAllBytesAsync(result.OutputPaths[0], TestContext.Current.CancellationToken);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);

        // IHDR width and height are big-endian at offsets 16 and 20: 612 x 792 pt at 36 dpi is 306 x 396 px.
        Assert.Equal(306, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(396, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
    }

    [Fact]
    public async Task No_tool_overwrites_its_own_input()
    {
        string source = TestPdf.Create(_dir, "a.pdf", 2);
        var overwrite = new OutputOptions { Overwrite = true };

        await AssertSelf(() => _service.RotateAsync(source, 90, null, source, options: overwrite));
        await AssertSelf(() => _service.OptimizeAsync(source, source, options: overwrite));
        await AssertSelf(() => _service.ProtectAsync(source, new ProtectOptions { UserPassword = "x" }, source, options: overwrite));
        Assert.Equal(2, (await _service.GetInfoAsync(source)).PageCount);

        static async Task AssertSelf(Func<Task> action) =>
            Assert.Equal(ErrorCode.InvalidArgument, (await Assert.ThrowsAsync<ModitException>(action)).Code);
    }

    private async Task<List<int>> PageCountsAsync(IEnumerable<string> paths)
    {
        var counts = new List<int>();
        foreach (string path in paths)
        {
            counts.Add((await _service.GetInfoAsync(path)).PageCount);
        }

        return counts;
    }

    private async Task<string> TextAsync(string path, int pageIndex)
    {
        DocumentInfo doc = await _engine.OpenDocumentAsync(path);
        try
        {
            return await _engine.GetPageTextAsync(doc.DocumentId, pageIndex);
        }
        finally
        {
            await _engine.CloseDocumentAsync(doc.DocumentId);
        }
    }

    /// <summary>Reports on the calling thread so assertions see every value.</summary>
    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
