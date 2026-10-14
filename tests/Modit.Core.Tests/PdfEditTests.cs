using Modit.Core.Imaging;
using Modit.Core.Pdf;
using Modit.Engine;
using Modit.Engine.Client;
using Modit.TestSupport;

namespace Modit.Core.Tests;

public sealed class PdfEditTests : IAsyncLifetime
{
    private const float Letter = TestPdf.LetterWidthPt;
    private const float LetterTall = TestPdf.LetterHeightPt;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modit-tests", Guid.NewGuid().ToString("N"));
    private readonly EngineClient _engine = new();
    private PdfService _service = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _service = new PdfService(_engine);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        TestDirectory.Delete(_dir);
    }

    [Fact]
    public async Task Organize_reorders_mixes_documents_rotates_and_inserts_blank_pages()
    {
        string a = TestPdf.Create(_dir, "a.pdf", 3, "Alpha");
        string b = TestPdf.Create(_dir, "b.pdf", 2, "Beta");
        string output = Path.Combine(_dir, "organized.pdf");

        PdfOperationResult result = await _service.OrganizeAsync(
            [new OrganizeSource(a), new OrganizeSource(b)],
            [new OrganizePage(0, 2), new OrganizePage(1, 0, RotationDegrees: 90), OrganizePage.Blank(200, 300), new OrganizePage(0, 0)],
            output);

        Assert.Equal(4, result.PageCount);
        DocumentInfo info = await _service.GetInfoAsync(output);
        Assert.Contains("Alpha page 3", await TextAsync(output, 0), StringComparison.Ordinal);
        Assert.Contains("Beta page 1", await TextAsync(output, 1), StringComparison.Ordinal);
        Assert.Equal((LetterTall, Letter), (info.Pages[1].WidthPt, info.Pages[1].HeightPt)); // turned a quarter
        Assert.Equal((200f, 300f), (info.Pages[2].WidthPt, info.Pages[2].HeightPt));
        Assert.True(string.IsNullOrWhiteSpace(await TextAsync(output, 2)));
        Assert.Contains("Alpha page 1", await TextAsync(output, 3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_pages_keeps_the_rest_in_order()
    {
        string source = TestPdf.Create(_dir, "five.pdf", 5, "Five");
        string output = Path.Combine(_dir, "kept.pdf");

        PdfOperationResult result = await _service.RemovePagesAsync(source, PageRange.Parse("2,4"), output);

        Assert.Equal(3, result.PageCount);
        Assert.Contains("Five page 5", await TextAsync(output, 2), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ModitException>(() =>
            _service.RemovePagesAsync(source, PageRange.All, Path.Combine(_dir, "none.pdf")));
    }

    [Fact]
    public async Task Text_stamp_numbers_pages_and_leaves_the_source_alone()
    {
        string source = TestPdf.Create(_dir, "report.pdf", 3, "Report");
        byte[] original = await File.ReadAllBytesAsync(source);
        string output = Path.Combine(_dir, "numbered.pdf");

        await _service.EditAsync(source, new PdfEditOptions { Text = Stamp("Page {page} of {pages}", "bottom-center") }, output);

        Assert.Contains("Page 2 of 3", await TextAsync(output, 1), StringComparison.Ordinal);
        Assert.Contains("Report page 2", await TextAsync(output, 1), StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Text_stamp_on_selected_pages_only_with_a_start_number()
    {
        string source = TestPdf.Create(_dir, "doc.pdf", 3, "Doc");
        string output = Path.Combine(_dir, "partly.pdf");

        await _service.EditAsync(
            source,
            new PdfEditOptions { Pages = PageRange.Parse("2-3"), Text = Stamp("No. {page}", "top-right") with { StartNumber = 10 } },
            output);

        Assert.DoesNotContain("No.", await TextAsync(output, 0), StringComparison.Ordinal);
        Assert.Contains("No. 11", await TextAsync(output, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_ascii_text_embeds_a_font_and_reads_back()
    {
        string source = TestPdf.Create(_dir, "intl.pdf", 1, "Intl");
        string output = Path.Combine(_dir, "intl-stamped.pdf");

        await _service.EditAsync(source, new PdfEditOptions { Text = Stamp("Gültig – Ωmega Привет", "center") }, output);

        // Known PDFium behavior: in embedded Arial the space glyph is shared with U+00A0, and text extraction
        // reports the no-break space. The page shows a normal space.
        string text = (await TextAsync(output, 0)).Replace(' ', ' ');
        Assert.Contains("Gültig – Ωmega Привет", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Image_stamp_lands_where_asked_on_a_normal_page()
    {
        string source = TestPdf.Create(_dir, "plain.pdf", 1, "Plain");
        string output = Path.Combine(_dir, "signed.pdf");

        await _service.EditAsync(
            source, new PdfEditOptions { Image = new PdfImageStamp(RedPng(), 100, "bottom-right", 0) }, output);

        // 100 x 50 pt red block in the bottom-right corner; check a pixel inside it and one outside.
        Assert.True(IsRed(await PixelAsync(output, 0, Letter - 50, LetterTall - 25)));
        Assert.False(IsRed(await PixelAsync(output, 0, Letter - 150, LetterTall - 25)));
    }

    [Fact]
    public async Task Stamps_follow_the_page_as_the_reader_sees_it_on_rotated_pages()
    {
        string source = TestPdf.Create(_dir, "turn.pdf", 1, "Turn");
        string rotated = Path.Combine(_dir, "turned.pdf");
        await _service.RotateAsync(source, 90, null, rotated);
        string output = Path.Combine(_dir, "turned-signed.pdf");

        await _service.EditAsync(
            rotated, new PdfEditOptions { Image = new PdfImageStamp(RedPng(), 100, "bottom-right", 0) }, output);

        // The rotated page shows 792 wide and 612 high; the block must be at its visible bottom-right.
        Assert.True(IsRed(await PixelAsync(output, 0, LetterTall - 50, Letter - 25)));
        Assert.False(IsRed(await PixelAsync(output, 0, 50, Letter - 25)));
    }

    [Fact]
    public async Task Crop_trims_the_visible_page_including_rotated_pages()
    {
        string source = TestPdf.Create(_dir, "crop.pdf", 1, "Crop");
        string cropped = Path.Combine(_dir, "cropped.pdf");
        await _service.EditAsync(source, new PdfEditOptions { Crop = new PdfCrop(72, 72, 72, 72) }, cropped);

        string rotated = Path.Combine(_dir, "crop-turned.pdf");
        await _service.RotateAsync(source, 90, null, rotated);
        string croppedRotated = Path.Combine(_dir, "crop-turned-cropped.pdf");
        await _service.EditAsync(rotated, new PdfEditOptions { Crop = new PdfCrop(LeftPt: 100, 0, 0, 0) }, croppedRotated);

        PageInfo page = (await _service.GetInfoAsync(cropped)).Pages[0];
        Assert.Equal((Letter - 144, LetterTall - 144), (page.WidthPt, page.HeightPt));
        PageInfo turned = (await _service.GetInfoAsync(croppedRotated)).Pages[0];
        Assert.Equal((LetterTall - 100, Letter), (turned.WidthPt, turned.HeightPt));
    }

    [Fact]
    public async Task Cropping_away_the_whole_page_is_refused()
    {
        string source = TestPdf.Create(_dir, "tiny.pdf", 1, "Tiny");

        var ex = await Assert.ThrowsAsync<ModitException>(() => _service.EditAsync(
            source, new PdfEditOptions { Crop = new PdfCrop(400, 0, 400, 0) }, Path.Combine(_dir, "gone.pdf")));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Editing_a_protected_file_warns_that_the_copy_has_no_password()
    {
        string source = TestPdf.Create(_dir, "secret.pdf", 2, "Secret");
        string locked = Path.Combine(_dir, "locked.pdf");
        await _service.ProtectAsync(source, new ProtectOptions { UserPassword = "pw" }, locked);
        string output = Path.Combine(_dir, "locked-stamped.pdf");

        PdfOperationResult result = await _service.EditAsync(
            locked, new PdfEditOptions { Text = Stamp("DRAFT", "center"), Flatten = true }, output, password: "pw");

        Assert.Single(result.Warnings);
        Assert.False((await _service.GetInfoAsync(output)).IsEncrypted);
        Assert.Contains("DRAFT", await TextAsync(output, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_export_separates_pages_with_form_feeds()
    {
        string source = TestPdf.Create(_dir, "words.pdf", 3, "Words");
        string output = Path.Combine(_dir, "words.txt");

        PdfOperationResult result = await _service.ExportTextAsync(source, output);

        string text = await File.ReadAllTextAsync(output);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(2, text.Count(c => c == '\f'));
        Assert.Contains("Words page 3", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2, 4 }, "1-3,5")]
    [InlineData(new[] { 3 }, "4")]
    [InlineData(new[] { 0, 2, 4, 5 }, "1,3,5-6")]
    public void Index_lists_become_compact_ranges(int[] indexes, string expected) =>
        Assert.Equal(expected, PdfService.ToRangeText(indexes));

    private static PdfTextStamp Stamp(string text, string position) =>
        new(text, 12, "#202020", 1, 0, position, 24, 1, string.Empty, false);

    private string RedPng()
    {
        const int width = 200;
        const int height = 100;
        byte[] bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (0, 0, 255, 255);
        }

        string path = Path.Combine(_dir, "red.png");
        using FileStream stream = File.Create(path);
        PngEncoder.WriteBgra(stream, bgra, width, height, width * 4);
        return path;
    }

    private static bool IsRed((byte B, byte G, byte R) pixel) => pixel.R > 200 && pixel.G < 60 && pixel.B < 60;

    /// <summary>Color at (x, y) in points from the visible top-left, rendered at 1 px per point.</summary>
    private async Task<(byte B, byte G, byte R)> PixelAsync(string path, int page, double x, double y)
    {
        DocumentInfo info = await _engine.OpenDocumentAsync(path);
        try
        {
            using RenderedImage image = await _engine.RenderPageAsync(info, page, 1.0);
            int offset = ((int)y * image.StrideBytes) + ((int)x * 4);
            ReadOnlySpan<byte> pixels = image.Pixels;
            return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
        }
        finally
        {
            await _engine.CloseDocumentAsync(info.DocumentId);
        }
    }

    private async Task<string> TextAsync(string path, int page)
    {
        DocumentInfo info = await _engine.OpenDocumentAsync(path);
        try
        {
            return await _engine.GetPageTextAsync(info.DocumentId, page);
        }
        finally
        {
            await _engine.CloseDocumentAsync(info.DocumentId);
        }
    }
}
