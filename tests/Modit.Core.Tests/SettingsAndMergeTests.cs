using System.IO.Compression;
using Modit.Core.Imaging;
using Modit.Core.Pdf;
using Modit.Core.Settings;
using Modit.Engine.Client;
using Modit.TestSupport;

namespace Modit.Core.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => TestDirectory.Delete(_dir);

    [Fact]
    public void Defaults_apply_when_no_file_exists_and_updates_persist()
    {
        var paths = new AppPaths(_dir, isPortable: false);
        var first = new SettingsService(paths);
        Assert.Equal(AppTheme.System, first.Current.Theme);

        first.Update(s => s with { Theme = AppTheme.Dark, UpdateChannel = UpdateChannel.Beta });

        var second = new SettingsService(paths);
        Assert.Equal(AppTheme.Dark, second.Current.Theme);
        Assert.Equal(UpdateChannel.Beta, second.Current.UpdateChannel);
        Assert.False(File.Exists(paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void A_corrupt_file_is_set_aside_and_defaults_are_used()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir, isPortable: false);
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        var service = new SettingsService(paths);

        Assert.Equal(AppTheme.System, service.Current.Theme);
        Assert.NotNull(service.CorruptBackupPath);
        Assert.True(File.Exists(service.CorruptBackupPath));
    }

    [Fact]
    public void Unknown_fields_from_a_newer_version_do_not_break_loading()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir, isPortable: false);
        File.WriteAllText(paths.SettingsFile, """{ "schemaVersion": 99, "theme": "Light", "fromTheFuture": { "x": 1 } }""");

        var service = new SettingsService(paths);

        Assert.Equal(AppTheme.Light, service.Current.Theme);
        Assert.Null(service.CorruptBackupPath);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "theme": "Dark" }""")]
    [InlineData("""{ "theme": "Dark", "skippedVersions": null }""")]
    public void Missing_or_null_collections_load_as_empty_not_null(string json)
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir, isPortable: false);
        File.WriteAllText(paths.SettingsFile, json);

        var service = new SettingsService(paths);

        Assert.Equal(AppTheme.Dark, service.Current.Theme);
        Assert.NotNull(service.Current.SkippedVersions);
        Assert.Empty(service.Current.SkippedVersions);
        Assert.True(service.Current.CheckForUpdatesAutomatically);
    }
}

public sealed class PngEncoderTests
{
    [Fact]
    public void Writes_a_decodable_png_with_rgba_pixels()
    {
        // 2x1 image: opaque red, then half-transparent blue (BGRA input).
        byte[] bgra = [0, 0, 255, 255, 255, 0, 0, 128];
        using var stream = new MemoryStream();

        PngEncoder.WriteBgra(stream, bgra, width: 2, height: 1, stride: 8);

        byte[] png = stream.ToArray();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);

        // IHDR is 8 (signature) + 8 (length, type) + 13 (data) + 4 (crc) = 33 bytes; IDAT data starts 8 later.
        int idatLength = (png[33] << 24) | (png[34] << 16) | (png[35] << 8) | png[36];
        using var zlib = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
        byte[] raw = new byte[9];
        zlib.ReadExactly(raw);

        Assert.Equal([0, 255, 0, 0, 255, 0, 0, 255, 128], raw);
    }
}

public sealed class PdfServiceMergeTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modit-tests", Guid.NewGuid().ToString("N"));
    private readonly EngineClient _engine = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        TestDirectory.Delete(_dir);
    }

    [Fact]
    public async Task Merges_selected_pages_in_order_and_leaves_no_temp_file()
    {
        string a = TestPdf.Create(_dir, "a.pdf", 3, "Alpha");
        string b = TestPdf.Create(_dir, "b.pdf", 4, "Beta");
        string output = Path.Combine(_dir, "out", "merged.pdf");
        var service = new PdfService(_engine);

        PdfOperationResult result = await service.MergeAsync(
            [new MergeSource(a), new MergeSource(b, PageRange.Parse("4,2"))], output);

        Assert.Equal(5, result.PageCount);
        var doc = await _engine.OpenDocumentAsync(output);
        Assert.Contains("Beta page 4", await _engine.GetPageTextAsync(doc.DocumentId, 3), StringComparison.Ordinal);
        Assert.Contains("Beta page 2", await _engine.GetPageTextAsync(doc.DocumentId, 4), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.modit-tmp"));
    }

    [Fact]
    public async Task Never_overwrites_an_existing_file_unless_asked()
    {
        string a = TestPdf.Create(_dir, "a.pdf", 1);
        string output = Path.Combine(_dir, "existing.pdf");
        await File.WriteAllTextAsync(output, "precious", TestContext.Current.CancellationToken);
        var service = new PdfService(_engine);

        ModitException ex = await Assert.ThrowsAsync<ModitException>(() => service.MergeAsync([new MergeSource(a)], output));
        Assert.Equal(ErrorCode.OutputWriteFailed, ex.Code);
        Assert.Equal("precious", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));

        PdfOperationResult result = await service.MergeAsync(
            [new MergeSource(a)], output, new OutputOptions { Overwrite = true });
        Assert.Equal(1, result.PageCount);
    }

    [Fact]
    public async Task A_bad_input_fails_before_anything_is_written()
    {
        string a = TestPdf.Create(_dir, "a.pdf", 2);
        string broken = Path.Combine(_dir, "broken.pdf");
        await File.WriteAllTextAsync(broken, "nope", TestContext.Current.CancellationToken);
        string output = Path.Combine(_dir, "never.pdf");
        var service = new PdfService(_engine);

        ModitException corrupt = await Assert.ThrowsAsync<ModitException>(
            () => service.MergeAsync([new MergeSource(a), new MergeSource(broken)], output));
        ModitException range = await Assert.ThrowsAsync<ModitException>(
            () => service.MergeAsync([new MergeSource(a, PageRange.Parse("5"))], output));
        ModitException self = await Assert.ThrowsAsync<ModitException>(
            () => service.MergeAsync([new MergeSource(a)], a, new OutputOptions { Overwrite = true }));

        Assert.Equal(ErrorCode.PdfCorrupt, corrupt.Code);
        Assert.Equal(ErrorCode.PdfPageOutOfRange, range.Code);
        Assert.Equal(ErrorCode.InvalidArgument, self.Code);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_dir, "*.modit-tmp"));
    }
}
