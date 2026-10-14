using Modit.App.ViewModels;
using Modit.Core.Pdf;
using Modit.Engine;
using Modit.Engine.Client;
using Modit.TestSupport;

namespace Modit.Core.Tests;

/// <summary>The Organize editor's model, end to end through a real save.</summary>
public sealed class OrganizeViewModelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modit-tests", Guid.NewGuid().ToString("N"));
    private readonly EngineClient _engine = new();
    private OrganizeViewModel _model = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _model = new OrganizeViewModel(_engine, new PdfService(_engine), new KeyStrings());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _model.DisposeAsync();
        await _engine.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Edits_are_saved_in_the_order_and_rotation_shown()
    {
        await _model.AddDocumentAsync(TestPdf.Create(_dir, "a.pdf", 3, "Alpha"), null);
        Assert.False(_model.IsDirty);
        Assert.False(_model.CanUndo);

        await _model.AddDocumentAsync(TestPdf.Create(_dir, "b.pdf", 1, "Beta"), null, insertAt: 1);
        _model.Rotate([_model.Pages[0]], 90);
        _model.Move([_model.Pages[3]], -1);   // A3 before A2
        _model.Delete([_model.Pages[1]]);     // drop B1
        _model.InsertBlank(_model.Pages.Count);
        Assert.Equal(["A 1", "A 3", "A 2", "Blank"], _model.Pages.Select(p => p.Label));

        string output = Path.Combine(_dir, "out.pdf");
        await _model.SaveAsync(output, overwrite: false);

        DocumentInfo info = await new PdfService(_engine).GetInfoAsync(output);
        Assert.Equal(4, info.PageCount);
        Assert.Equal((TestPdf.LetterHeightPt, TestPdf.LetterWidthPt), (info.Pages[0].WidthPt, info.Pages[0].HeightPt));
        Assert.Equal((TestPdf.LetterWidthPt, TestPdf.LetterHeightPt), (info.Pages[3].WidthPt, info.Pages[3].HeightPt));
        Assert.False(_model.IsDirty);
    }

    [Fact]
    public async Task Undo_restores_order_and_rotation()
    {
        await _model.AddDocumentAsync(TestPdf.Create(_dir, "c.pdf", 3, "Gamma"), null);
        _model.Rotate([.. _model.Pages], 180);
        _model.Delete([_model.Pages[0]]);

        _model.Undo();
        Assert.Equal(3, _model.Pages.Count);
        Assert.All(_model.Pages, p => Assert.Equal(180, p.Rotation));

        _model.Undo();
        Assert.All(_model.Pages, p => Assert.Equal(0, p.Rotation));
        Assert.False(_model.CanUndo);
    }

    /// <summary>Returns keys instead of localized text, except the blank page label the test checks.</summary>
    private sealed class KeyStrings : IStringProvider
    {
        public string Get(string key) => key == "OrganizeBlankLabel" ? "Blank" : key;

        public string Format(string key, params object?[] arguments) => key;
    }
}
