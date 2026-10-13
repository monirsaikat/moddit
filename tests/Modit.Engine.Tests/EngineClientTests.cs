using System.Diagnostics;
using System.Text.Json;
using Modit.Engine;
using Modit.Engine.Client;
using Modit.TestSupport;

namespace Modit.Engine.Tests;

public sealed class EngineClientTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modit-tests", Guid.NewGuid().ToString("N"));
    private readonly EngineClient _client = new();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task Ping_reports_a_separate_process()
    {
        HostInfo info = await _client.PingAsync();

        Assert.NotEqual(Environment.ProcessId, info.ProcessId);
        Assert.Equal(0, info.OpenDocuments);
    }

    [Fact]
    public async Task Open_returns_pages_sizes_version_and_metadata()
    {
        string path = TestPdf.Create(_dir, "three.pdf", 3, title: "Quarterly report");

        DocumentInfo doc = await _client.OpenDocumentAsync(path);

        Assert.Equal(3, doc.PageCount);
        Assert.Equal("1.4", doc.PdfVersion);
        Assert.False(doc.IsEncrypted);
        Assert.All(doc.Pages, p =>
        {
            Assert.Equal(TestPdf.LetterWidthPt, p.WidthPt);
            Assert.Equal(TestPdf.LetterHeightPt, p.HeightPt);
        });
        Assert.Equal("Quarterly report", doc.Metadata["Title"]);
        Assert.Equal(new FileInfo(path).Length, doc.FileSizeBytes);
    }

    [Fact]
    public async Task Text_is_extracted_per_page()
    {
        string path = TestPdf.Create(_dir, "text.pdf", 2, textPrefix: "Invoice");
        DocumentInfo doc = await _client.OpenDocumentAsync(path);

        string page2 = await _client.GetPageTextAsync(doc.DocumentId, 1);

        Assert.Contains("Invoice page 2", page2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_produces_white_page_with_dark_content()
    {
        string path = TestPdf.Create(_dir, "render.pdf", 1);
        DocumentInfo doc = await _client.OpenDocumentAsync(path);

        using RenderedImage image = await _client.RenderPageAsync(doc, 0, scale: 1.0);

        Assert.Equal(612, image.WidthPx);
        Assert.Equal(792, image.HeightPx);
        Assert.Equal((255, 255, 255, 255), Pixel(image, 5, 5));

        // The fixture draws a black rectangle at x 72-272, y 600-640 in PDF space (origin bottom-left),
        // which is rows 152-192 from the top.
        Assert.Equal((0, 0, 0, 255), Pixel(image, 150, 170));
    }

    [Fact]
    public async Task Region_render_matches_the_same_area_of_a_full_render()
    {
        string path = TestPdf.Create(_dir, "tile.pdf", 1);
        DocumentInfo doc = await _client.OpenDocumentAsync(path);
        const double Scale = 2.0;

        using RenderedImage full = await _client.RenderPageAsync(doc, 0, Scale);
        using RenderedImage tile = await _client.RenderRegionAsync(doc.DocumentId, 0, Scale, 256, 256, 256, 256);

        int mismatches = 0;
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                if (Pixel(tile, x, y) != Pixel(full, x + 256, y + 256))
                {
                    mismatches++;
                }
            }
        }

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public async Task Host_crash_is_recovered_transparently_and_documents_stay_open()
    {
        string path = TestPdf.Create(_dir, "crash.pdf", 2);
        DocumentInfo doc = await _client.OpenDocumentAsync(path);
        int firstPid = (await _client.PingAsync()).ProcessId;

        await _client.CrashHostForTestingAsync();

        // Same document id, new process: the client restarted the host and re-opened the file.
        string text = await _client.GetPageTextAsync(doc.DocumentId, 0);
        HostInfo after = await _client.PingAsync();

        Assert.Contains("page 1", text, StringComparison.Ordinal);
        Assert.NotEqual(firstPid, after.ProcessId);
        Assert.Equal(1, after.OpenDocuments);
        Assert.Equal(1, _client.RestartCount);
    }

    [Fact]
    public async Task Missing_and_corrupt_files_map_to_stable_error_codes()
    {
        string corrupt = Path.Combine(_dir, "corrupt.pdf");
        await File.WriteAllTextAsync(corrupt, "this is not a pdf at all", TestContext.Current.CancellationToken);

        ModitException missing = await Assert.ThrowsAsync<ModitException>(
            () => _client.OpenDocumentAsync(Path.Combine(_dir, "nope.pdf")));
        ModitException damaged = await Assert.ThrowsAsync<ModitException>(() => _client.OpenDocumentAsync(corrupt));

        Assert.Equal(ErrorCode.FileNotFound, missing.Code);
        Assert.Equal(ErrorCode.PdfCorrupt, damaged.Code);
    }

    [Fact]
    public async Task Qpdf_merges_two_documents()
    {
        string a = TestPdf.Create(_dir, "a.pdf", 2, "Alpha");
        string b = TestPdf.Create(_dir, "b.pdf", 3, "Beta");
        string merged = Path.Combine(_dir, "merged.pdf");
        string job = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["inputFile"] = a,
            ["outputFile"] = merged,
            ["pages"] = new object[]
            {
                new Dictionary<string, string> { ["file"] = a, ["range"] = "1-z" },
                new Dictionary<string, string> { ["file"] = b, ["range"] = "1-z" },
            },
        });

        QpdfJobResult result = await _client.RunQpdfJobAsync(job);

        Assert.True(result.ExitCode == 0, string.Join(" | ", result.Errors));
        DocumentInfo doc = await _client.OpenDocumentAsync(merged);
        Assert.Equal(5, doc.PageCount);
        Assert.Contains("Beta page 3", await _client.GetPageTextAsync(doc.DocumentId, 4), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encrypted_documents_require_the_right_password()
    {
        string plain = TestPdf.Create(_dir, "plain.pdf", 1);
        string locked = Path.Combine(_dir, "locked.pdf");
        string job = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["inputFile"] = plain,
            ["outputFile"] = locked,
            ["encrypt"] = new Dictionary<string, object>
            {
                ["userPassword"] = "open-sesame",
                ["ownerPassword"] = "owner",
                ["256bit"] = new Dictionary<string, object>(),
            },
        });
        QpdfJobResult result = await _client.RunQpdfJobAsync(job);
        Assert.True(result.ExitCode == 0, string.Join(" | ", result.Errors));

        ModitException noPassword = await Assert.ThrowsAsync<ModitException>(() => _client.OpenDocumentAsync(locked));
        ModitException wrong = await Assert.ThrowsAsync<ModitException>(() => _client.OpenDocumentAsync(locked, "nope"));
        DocumentInfo opened = await _client.OpenDocumentAsync(locked, "open-sesame");

        Assert.Equal(ErrorCode.PdfEncryptedNeedsPassword, noPassword.Code);
        Assert.Equal(ErrorCode.PdfWrongPassword, wrong.Code);
        Assert.True(opened.IsEncrypted);
        Assert.Equal(1, opened.PageCount);
    }

    [Fact]
    public async Task Qpdf_reports_errors_for_invalid_jobs()
    {
        string job = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["inputFile"] = Path.Combine(_dir, "does-not-exist.pdf"),
            ["outputFile"] = Path.Combine(_dir, "out.pdf"),
        });

        QpdfJobResult result = await _client.RunQpdfJobAsync(job);

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Small_calls_round_trip_quickly()
    {
        await _client.PingAsync();
        const int Calls = 2000;

        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < Calls; i++)
        {
            await _client.PingAsync();
        }

        double perCallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds / Calls;
        TestContext.Current.SendDiagnosticMessage($"RPC round trip: {perCallMs:0.000} ms");

        // Budget in docs/plan/07 is 0.3 ms on the baseline machine; allow slack for shared CI runners.
        Assert.True(perCallMs < 5, $"RPC round trip took {perCallMs:0.000} ms");
    }

    private static (byte R, byte G, byte B, byte A) Pixel(RenderedImage image, int x, int y)
    {
        ReadOnlySpan<byte> p = image.Pixels.Slice((y * image.StrideBytes) + (x * 4), 4);
        return (p[2], p[1], p[0], p[3]);
    }
}
