using Modit.Core.Convert;
using Modit.Core.Imaging;
using Modit.Core.Pdf;
using Modit.Engine;
using Modit.Engine.Client;

namespace Modit.Core.Tests;

public sealed class ImageConvertTests : IAsyncLifetime
{
    private const int Width = 120;
    private const int Height = 80;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modit-tests", Guid.NewGuid().ToString("N"));
    private readonly EngineClient _engine = new();
    private ImageConvertService _service = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _service = new ImageConvertService(_engine);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("png", ".png")]
    [InlineData("jpeg", ".jpg")]
    [InlineData("jpg", ".jpg")]
    [InlineData("webp", ".webp")]
    [InlineData("avif", ".avif")]
    [InlineData("tiff", ".tif")]
    [InlineData("gif", ".gif")]
    [InlineData("bmp", ".bmp")]
    public async Task Png_converts_to_every_writable_format_and_reads_back(string target, string extension)
    {
        string source = WritePng("photo.png", transparent: true);
        string outDir = Path.Combine(_dir, "out");

        ConvertBatchResult result = await _service.ConvertAsync(
            [source], new ImageConvertOptions { TargetFormat = target, OutputDirectory = outDir });

        ConvertItemResult item = Assert.Single(result.Items);
        Assert.True(item.Status == ConvertItemStatus.Converted, item.Message);
        Assert.Equal(Path.Combine(outDir, "photo" + extension), item.Output);
        ImageInfoResult info = await _engine.GetImageInfoAsync(item.Output!);
        Assert.Equal(ImageFormats.Find(target)!.Id, info.Format);
        Assert.Equal((Width, Height), (info.Width, info.Height));
        Assert.Empty(Directory.GetFiles(outDir, "*.modit-tmp"));
    }

    [Fact]
    public async Task Jpeg_output_fills_transparency_with_the_background()
    {
        string source = WritePng("logo.png", transparent: true);

        ConvertBatchResult result = await _service.ConvertAsync([source], new ImageConvertOptions { TargetFormat = "jpeg" });

        ImageInfoResult info = await _engine.GetImageInfoAsync(result.Items[0].Output!);
        Assert.False(info.HasAlpha);
        Assert.Equal(3, info.Bands);
    }

    [Fact]
    public async Task Size_limits_shrink_and_keep_the_aspect_ratio()
    {
        string source = WritePng("wide.png");

        ConvertBatchResult result = await _service.ConvertAsync(
            [source], new ImageConvertOptions { TargetFormat = "webp", MaxWidthPx = 60 });

        Assert.Equal((60, 40), (result.Items[0].Width, result.Items[0].Height));
    }

    [Fact]
    public async Task Small_images_are_never_enlarged()
    {
        string source = WritePng("small.png");

        ConvertBatchResult result = await _service.ConvertAsync(
            [source], new ImageConvertOptions { TargetFormat = "png", MaxWidthPx = 1000, OutputDirectory = Path.Combine(_dir, "o") });

        Assert.Equal((Width, Height), (result.Items[0].Width, result.Items[0].Height));
    }

    [Fact]
    public async Task Svg_renders_at_96_dpi_by_default_and_re_renders_for_larger_sizes()
    {
        string svg = WriteSvg("icon.svg", 100, 50);

        ConvertBatchResult natural = await _service.ConvertAsync(
            [svg], new ImageConvertOptions { TargetFormat = "png", OutputDirectory = Path.Combine(_dir, "a") });
        ConvertBatchResult large = await _service.ConvertAsync(
            [svg], new ImageConvertOptions { TargetFormat = "png", MaxWidthPx = 400, OutputDirectory = Path.Combine(_dir, "b") });

        Assert.Equal((100, 50), (natural.Items[0].Width, natural.Items[0].Height));
        Assert.Equal((400, 200), (large.Items[0].Width, large.Items[0].Height));
    }

    [Fact]
    public async Task Ico_holds_the_standard_sizes_up_to_the_source_size()
    {
        string source = WritePng("app.png", transparent: true);

        ConvertBatchResult result = await _service.ConvertAsync([source], new ImageConvertOptions { TargetFormat = "ico" });

        Assert.True(result.Items[0].Status == ConvertItemStatus.Converted, result.Items[0].Message);
        ImageInfoResult info = await _engine.GetImageInfoAsync(result.Items[0].Output!);
        Assert.Equal("ico", info.Format);
        Assert.Equal(5, info.Frames); // 16, 24, 32, 48, 64: every standard size not above 120 px
        Assert.Equal((64, 64), (info.Width, info.Height));
    }

    [Fact]
    public async Task Bmp_and_ico_are_also_readable_as_input()
    {
        string source = WritePng("pic.png");
        ConvertBatchResult bmp = await _service.ConvertAsync([source], new ImageConvertOptions { TargetFormat = "bmp" });
        ConvertBatchResult ico = await _service.ConvertAsync([source], new ImageConvertOptions { TargetFormat = "ico" });

        ConvertBatchResult back = await _service.ConvertAsync(
            [bmp.Items[0].Output!, ico.Items[0].Output!],
            new ImageConvertOptions { TargetFormat = "png", OutputDirectory = Path.Combine(_dir, "back") });

        Assert.All(back.Items, i => Assert.Equal(ConvertItemStatus.Converted, i.Status));
        Assert.Equal((Width, Height), (back.Items[0].Width, back.Items[0].Height));
        Assert.Equal((64, 64), (back.Items[1].Width, back.Items[1].Height));
    }

    [Fact]
    public async Task Same_format_in_the_same_folder_never_replaces_the_source()
    {
        string source = WritePng("same.png");
        byte[] original = await File.ReadAllBytesAsync(source);

        ConvertBatchResult result = await _service.ConvertAsync(
            [source], new ImageConvertOptions { TargetFormat = "png", Collision = CollisionPolicy.Overwrite });

        Assert.Equal(Path.Combine(_dir, "same (2).png"), result.Items[0].Output);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task Collision_policies_rename_skip_and_overwrite()
    {
        string source = WritePng("doc.png");
        string existing = Path.Combine(_dir, "doc.jpg");
        await File.WriteAllTextAsync(existing, "keep me");

        ConvertItemResult renamed = (await _service.ConvertAsync([source], new ImageConvertOptions { TargetFormat = "jpeg" })).Items[0];
        ConvertItemResult skipped = (await _service.ConvertAsync(
            [source], new ImageConvertOptions { TargetFormat = "jpeg", Collision = CollisionPolicy.Skip })).Items[0];
        Assert.Equal("keep me", await File.ReadAllTextAsync(existing));
        ConvertItemResult replaced = (await _service.ConvertAsync(
            [source], new ImageConvertOptions { TargetFormat = "jpeg", Collision = CollisionPolicy.Overwrite })).Items[0];

        Assert.Equal(Path.Combine(_dir, "doc (2).jpg"), renamed.Output);
        Assert.Equal(ConvertItemStatus.Skipped, skipped.Status);
        Assert.Equal(existing, replaced.Output);
        Assert.Equal("jpeg", FormatSniffer.Detect(existing));
    }

    [Fact]
    public async Task Two_inputs_with_the_same_name_get_distinct_outputs()
    {
        string first = WritePng("x.png");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        string second = WritePng(Path.Combine("sub", "x.png"));
        string outDir = Path.Combine(_dir, "merged-out");

        ConvertBatchResult result = await _service.ConvertAsync(
            [first, second], new ImageConvertOptions { TargetFormat = "webp", OutputDirectory = outDir });

        Assert.Equal(
            [Path.Combine(outDir, "x.webp"), Path.Combine(outDir, "x (2).webp")],
            result.Items.Select(i => i.Output));
    }

    [Fact]
    public async Task A_bad_file_fails_alone_and_the_batch_continues()
    {
        string bad = Path.Combine(_dir, "notes.png");
        await File.WriteAllTextAsync(bad, "this is not a picture");
        string good = WritePng("good.png");

        ConvertBatchResult result = await _service.ConvertAsync([bad, good], new ImageConvertOptions { TargetFormat = "jpeg" });

        Assert.Equal(ConvertItemStatus.Failed, result.Items[0].Status);
        Assert.Equal(ErrorCode.ConvertUnsupportedFormat, result.Items[0].Error);
        Assert.Equal(ConvertItemStatus.Converted, result.Items[1].Status);
        Assert.Equal((1, 1), (result.Failed, result.Converted));
    }

    [Fact]
    public async Task Unknown_target_format_is_rejected_before_any_work()
    {
        string source = WritePng("a.png");

        var ex = await Assert.ThrowsAsync<ModitException>(() =>
            _service.ConvertAsync([source], new ImageConvertOptions { TargetFormat = "psd" }));

        Assert.Equal(ErrorCode.ConvertUnsupportedFormat, ex.Code);
    }

    [Fact]
    public async Task Images_become_one_pdf_page_each_in_order()
    {
        string png = WritePng("one.png", transparent: true);
        string jpeg = (await _service.ConvertAsync([WritePng("two.png")], new ImageConvertOptions { TargetFormat = "jpeg" }))
            .Items[0].Output!;
        string svg = WriteSvg("three.svg", 96, 48);
        string output = Path.Combine(_dir, "album.pdf");

        PdfOperationResult result = await new PdfService(_engine).FromImagesAsync([png, jpeg, svg], output);

        Assert.Equal(3, result.PageCount);
        DocumentInfo info = await new PdfService(_engine).GetInfoAsync(output);
        Assert.Equal(Width, info.Pages[0].WidthPt, 1); // 72 dpi when the file states none
        Assert.Equal(72, info.Pages[2].WidthPt, 1);    // 96 CSS px = 72 pt
        Assert.False(File.Exists(output + ".modit-tmp"));
    }

    [Fact]
    public async Task Images_on_a4_are_centered_and_turn_landscape_for_wide_images()
    {
        string png = WritePng("wide.png");
        string output = Path.Combine(_dir, "a4.pdf");

        await new PdfService(_engine).FromImagesAsync(
            [png], output, new ImagesToPdfOptions { PageSize = PdfPageSize.A4, MarginPt = 36 });

        DocumentInfo info = await new PdfService(_engine).GetInfoAsync(output);
        Assert.Equal(841.89, info.Pages[0].WidthPt, 1);
        Assert.Equal(595.28, info.Pages[0].HeightPt, 1);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "gif")]
    [InlineData(new byte[] { (byte)'I', (byte)'I', (byte)'*', 0 }, "tiff")]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0 }, "ico")]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' }, "pdf")]
    [InlineData(new byte[] { 1, 2, 3, 4 }, null)]
    public void Sniffer_recognizes_formats_by_content(byte[] header, string? expected) =>
        Assert.Equal(expected, FormatSniffer.Detect(header));

    [Fact]
    public void Sniffer_recognizes_webp_avif_and_svg()
    {
        Assert.Equal("webp", FormatSniffer.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal("avif", FormatSniffer.Detect([0, 0, 0, 0x1C, .. "ftypavif"u8]));
        Assert.Equal("heic", FormatSniffer.Detect([0, 0, 0, 0x18, .. "ftypheic"u8]));
        Assert.Equal("svg", FormatSniffer.Detect("\n<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Null(FormatSniffer.Detect("<html><body>no</body></html>"u8));
    }

    [Fact]
    public void Formats_are_found_by_id_or_extension()
    {
        Assert.Equal("jpeg", ImageFormats.Find("JPG")!.Id);
        Assert.Equal("tiff", ImageFormats.Find(".tiff")!.Id);
        Assert.Null(ImageFormats.Find("psd"));
        Assert.True(ImageFormats.IsInput(@"C:\x\photo.HEIC"));
        Assert.False(ImageFormats.IsInput(@"C:\x\report.pdf"));
    }

    private string WritePng(string name, bool transparent = false)
    {
        // A color gradient; with transparency the right half fades out.
        byte[] bgra = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * 4;
                bgra[i] = (byte)(x * 2);
                bgra[i + 1] = (byte)(y * 3);
                bgra[i + 2] = 200;
                bgra[i + 3] = transparent && x > Width / 2 ? (byte)(255 - ((x - (Width / 2)) * 4)) : (byte)255;
            }
        }

        string path = Path.Combine(_dir, name);
        using FileStream stream = File.Create(path);
        PngEncoder.WriteBgra(stream, bgra, Width, Height, Width * 4);
        return path;
    }

    private string WriteSvg(string name, int width, int height)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
              <rect width="{width}" height="{height}" fill="#1b66c9"/>
              <circle cx="{width / 2}" cy="{height / 2}" r="{height / 3}" fill="#ffffff"/>
            </svg>
            """);
        return path;
    }
}
