using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modit.Core.Convert;
using Modit.Core.Pdf;
using Modit.Engine;

namespace Modit.App.ViewModels;

public enum ToolKind
{
    Merge,
    Split,
    Extract,
    Rotate,
    Protect,
    Unlock,
    Optimize,
    Images,
    ConvertImages,
    ImagesToPdf,
    Organize,
    Watermark,
    PageNumbers,
    AddImage,
    Crop,
    Flatten,
    RemovePages,
    ToText,
}

/// <summary>One input file of a tool, with the optional page selection used by Merge.</summary>
public sealed partial class ToolInputFile(string path) : ObservableObject
{
    public string FilePath { get; } = path;

    public string Name { get; } = Path.GetFileName(path);

    public string Folder { get; } = Path.GetDirectoryName(path) ?? string.Empty;

    /// <summary>Segoe Fluent Icons glyph: a PDF page for PDFs, a picture for images.</summary>
    public string Glyph { get; } =
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase) ? "\uEA90" : "\uEB9F";

    /// <summary>Page selection for this file, for example <c>1-3,7</c>. Empty means all pages.</summary>
    [ObservableProperty]
    public partial string Pages { get; set; } = string.Empty;
}

/// <summary>
/// State and behavior of one tool tab (Merge, Split, ...). The same view renders every tool; the
/// <c>Show*</c> properties decide which options are visible. Running a tool never changes its inputs.
/// </summary>
public sealed partial class ToolViewModel : ObservableObject, IDisposable
{
    private readonly PdfService _service;
    private readonly ImageConvertService _converter;
    private readonly IStringProvider _strings;
    private bool _outputChosenByUser;
    private CancellationTokenSource? _cancellation;

    public ToolViewModel(ToolKind kind, PdfService service, ImageConvertService converter, IStringProvider strings)
    {
        Kind = kind;
        _service = service;
        _converter = converter;
        _strings = strings;
        Title = strings.Get($"Tool{kind}Title");
        Description = strings.Get($"Tool{kind}Description");
        AddFilesLabel = strings.Get(AcceptsImages ? "ToolAddImages" : "ToolAddPdf");
        PagesHeader = strings.Get(kind == ToolKind.RemovePages ? "ToolPagesToRemove" : "ToolPagesHeader");
        switch (kind)
        {
            case ToolKind.Watermark:
                PositionIndex = StampPositions.All.ToList().IndexOf("center");
                StampFontSize = 60;
                break;
            case ToolKind.PageNumbers:
                PositionIndex = StampPositions.All.ToList().IndexOf("bottom-center");
                StampFontSize = 11;
                MarginMm = 10;
                break;
            case ToolKind.AddImage:
                PositionIndex = StampPositions.All.ToList().IndexOf("bottom-right");
                MarginMm = 15;
                break;
        }

        EmptyFilesText = strings.Get(AcceptsImages ? "ToolFilesEmptyImages" : "ToolFilesEmptyPdf");
        Files.CollectionChanged += (_, _) =>
        {
            SuggestOutput();
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(CanAddFiles));
            RunCommand.NotifyCanExecuteChanged();
        };
    }

    public ToolKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    public ObservableCollection<ToolInputFile> Files { get; } = [];

    public string AddFilesLabel { get; }

    public string EmptyFilesText { get; }

    public bool AllowsMultipleFiles => Kind is ToolKind.Merge or ToolKind.ConvertImages or ToolKind.ImagesToPdf;

    /// <summary>True for tools whose input is images rather than PDFs.</summary>
    public bool AcceptsImages => Kind is ToolKind.ConvertImages or ToolKind.ImagesToPdf;

    /// <summary>True when the order of the input files matters, so the list offers up and down buttons.</summary>
    public bool ShowReorder => Kind is ToolKind.Merge or ToolKind.ImagesToPdf;

    /// <summary>True when the tool writes several files into a folder instead of one file.</summary>
    public bool ProducesFolder => Kind is ToolKind.Split or ToolKind.Images or ToolKind.ConvertImages;

    public bool ShowInputPassword => !AcceptsImages;

    public bool ShowTargetFormat => Kind == ToolKind.ConvertImages;

    public bool ShowResize => Kind == ToolKind.ConvertImages;

    public bool ShowPageLayout => Kind == ToolKind.ImagesToPdf;

    public IReadOnlyList<string> TargetFormatNames { get; } = [.. ImageFormats.Writable.Select(f => f.DisplayName)];

    private ImageFormatInfo SelectedFormat =>
        ImageFormats.Writable[Math.Clamp(TargetFormatIndex, 0, ImageFormats.Writable.Count - 1)];

    public bool ShowLossless => ShowTargetFormat && SelectedFormat.HasLossless;

    public bool ShowQuality => ShowTargetFormat && SelectedFormat.HasQuality && !(Lossless && SelectedFormat.HasLossless);

    public bool ShowPerFilePages => Kind == ToolKind.Merge;

    public bool ShowPages => Kind is ToolKind.Extract or ToolKind.Rotate or ToolKind.Images or ToolKind.Watermark
        or ToolKind.PageNumbers or ToolKind.AddImage or ToolKind.Crop or ToolKind.RemovePages or ToolKind.ToText;

    /// <summary>Header of the page selection box: "Pages", or "Pages to remove".</summary>
    public string PagesHeader { get; }

    public bool ShowWatermark => Kind == ToolKind.Watermark;

    public bool ShowPageNumbers => Kind == ToolKind.PageNumbers;

    public bool ShowStampImage => Kind == ToolKind.AddImage;

    /// <summary>Position and margin, shared by the three stamp tools.</summary>
    public bool ShowStampLayout => Kind is ToolKind.Watermark or ToolKind.PageNumbers or ToolKind.AddImage;

    public bool ShowStampMargin => Kind is ToolKind.PageNumbers or ToolKind.AddImage;

    public bool ShowFontSize => Kind is ToolKind.Watermark or ToolKind.PageNumbers;

    public bool ShowCrop => Kind == ToolKind.Crop;

    /// <summary>File extension of the result when it is a single file.</summary>
    public string OutputExtension => Kind == ToolKind.ToText ? ".txt" : ".pdf";

    public bool ShowPagesPerFile => Kind == ToolKind.Split;

    public bool ShowAngle => Kind == ToolKind.Rotate;

    public bool ShowProtection => Kind == ToolKind.Protect;

    public bool ShowFastWebView => Kind == ToolKind.Optimize;

    public bool ShowDpi => Kind == ToolKind.Images;

    public bool HasFiles => Files.Count > 0;

    public bool CanAddFiles => AllowsMultipleFiles || Files.Count == 0;

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Overwrite { get; set; }

    [ObservableProperty]
    public partial string PagesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double PagesPerFile { get; set; } = 1;

    /// <summary>0 = 90° clockwise, 1 = 180°, 2 = 90° counter-clockwise.</summary>
    [ObservableProperty]
    public partial int AngleIndex { get; set; }

    [ObservableProperty]
    public partial string InputPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UserPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OwnerPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AllowPrinting { get; set; } = true;

    [ObservableProperty]
    public partial bool AllowCopying { get; set; } = true;

    [ObservableProperty]
    public partial bool AllowEditing { get; set; } = true;

    [ObservableProperty]
    public partial bool FastWebView { get; set; }

    [ObservableProperty]
    public partial double Dpi { get; set; } = 150;

    /// <summary>Index into <see cref="ImageFormats.Writable"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQuality), nameof(ShowLossless))]
    public partial int TargetFormatIndex { get; set; }

    [ObservableProperty]
    public partial double Quality { get; set; } = 85;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQuality))]
    public partial bool Lossless { get; set; }

    /// <summary>0 keeps the original width.</summary>
    [ObservableProperty]
    public partial double MaxWidth { get; set; }

    [ObservableProperty]
    public partial double MaxHeight { get; set; }

    [ObservableProperty]
    public partial bool StripMetadata { get; set; }

    /// <summary>0 = each page as large as its image, 1 = A4, 2 = Letter.</summary>
    [ObservableProperty]
    public partial int PageSizeIndex { get; set; }

    [ObservableProperty]
    public partial double MarginMm { get; set; }

    [ObservableProperty]
    public partial string WatermarkText { get; set; } = "CONFIDENTIAL";

    /// <summary>Percent, 0 to 100.</summary>
    [ObservableProperty]
    public partial double StampOpacity { get; set; } = 25;

    [ObservableProperty]
    public partial double StampAngle { get; set; } = 45;

    /// <summary>0 gray, 1 red, 2 blue, 3 black.</summary>
    [ObservableProperty]
    public partial int StampColorIndex { get; set; }

    [ObservableProperty]
    public partial double StampFontSize { get; set; } = 12;

    /// <summary>Index into <see cref="StampPositions.All"/>.</summary>
    [ObservableProperty]
    public partial int PositionIndex { get; set; }

    [ObservableProperty]
    public partial string NumberFormat { get; set; } = "{page} / {pages}";

    [ObservableProperty]
    public partial double StartNumber { get; set; } = 1;

    [ObservableProperty]
    public partial string StampImagePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ImageWidthMm { get; set; } = 40;

    [ObservableProperty]
    public partial double CropTopMm { get; set; }

    [ObservableProperty]
    public partial double CropRightMm { get; set; }

    [ObservableProperty]
    public partial double CropBottomMm { get; set; }

    [ObservableProperty]
    public partial double CropLeftMm { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>0 to 100, or -1 while the amount of work is unknown.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    [ObservableProperty]
    public partial bool ResultIsError { get; set; }

    [ObservableProperty]
    public partial string ResultTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultMessage { get; set; } = string.Empty;

    /// <summary>The file or folder produced by the last successful run.</summary>
    [ObservableProperty]
    public partial string? ResultPath { get; set; }

    /// <summary>Adds PDFs, ignoring duplicates and, for single-file tools, anything after the first.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            bool accepted = AcceptsImages
                ? ImageFormats.IsInput(path)
                : string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
            if (!accepted)
            {
                continue;
            }

            if (!AllowsMultipleFiles)
            {
                Files.Clear();
            }
            else if (Files.Any(f => string.Equals(f.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Files.Add(new ToolInputFile(Path.GetFullPath(path)));
            if (!AllowsMultipleFiles)
            {
                break;
            }
        }
    }

    public void Remove(ToolInputFile file) => Files.Remove(file);

    public void Move(ToolInputFile file, int delta)
    {
        int index = Files.IndexOf(file);
        int target = index + delta;
        if (index >= 0 && target >= 0 && target < Files.Count)
        {
            Files.Move(index, target);
        }
    }

    /// <summary>Called when the user picks the destination in a dialog; stops automatic suggestions.</summary>
    public void SetOutputFromPicker(string path, bool replaceConfirmedByDialog)
    {
        _outputChosenByUser = true;
        OutputPath = path;
        if (replaceConfirmedByDialog)
        {
            // The Windows save dialog has already asked "replace it?".
            Overwrite = true;
        }
    }

    public string SuggestedFileName => Path.GetFileName(BuildSuggestion() ?? "output.pdf");

    /// <summary>Stops a running operation; called when the tool's tab closes.</summary>
    public void Dispose()
    {
        CancellationTokenSource? running = _cancellation;
        _cancellation = null;
        running?.Cancel();
        running?.Dispose();
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    private bool CanRun() => Files.Count > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        HasResult = false;
        IsRunning = true;
        Progress = -1;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            (string resultPath, string message) = await ExecuteAsync(cancellation.Token).ConfigureAwait(true);
            ResultIsError = false;
            ResultTitle = _strings.Get("ToolResultDone");
            ResultMessage = message;
            ResultPath = resultPath;

            // The suggested name is now taken; offer a fresh one for the next run.
            _outputChosenByUser = false;
            SuggestOutput();
        }
        catch (OperationCanceledException)
        {
            ShowError(_strings.Get("ToolResultCancelled"), string.Empty);
        }
        catch (ModitException ex)
        {
            ShowError(_strings.Get("ToolResultFailed"), Describe(ex));
        }
        finally
        {
            cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            HasResult = true;
        }
    }

    private async Task<(string Path, string Message)> ExecuteAsync(CancellationToken ct)
    {
        string source = Files[0].FilePath;
        string? password = string.IsNullOrEmpty(InputPassword) ? null : InputPassword;
        var options = new OutputOptions { Overwrite = Overwrite };
        var progress = new Progress<double>(value => Progress = value * 100);
        PageRange? pages = string.IsNullOrWhiteSpace(PagesText) ? null : PageRange.Parse(PagesText);

        switch (Kind)
        {
            case ToolKind.Merge:
            {
                List<MergeSource> sources =
                [
                    .. Files.Select(f => new MergeSource(
                        f.FilePath, string.IsNullOrWhiteSpace(f.Pages) ? null : PageRange.Parse(f.Pages), password)),
                ];
                PdfOperationResult result = await _service.MergeAsync(sources, OutputPath, options, ct).ConfigureAwait(true);
                return (result.OutputPath, _strings.Format("ToolMergeDone", Files.Count, result.PageCount));
            }

            case ToolKind.Split:
            {
                PdfMultiResult result = await _service.SplitAsync(
                    source, double.IsNaN(PagesPerFile) ? 1 : (int)Math.Max(1, PagesPerFile), OutputPath, password, options, progress, ct).ConfigureAwait(true);
                return (OutputPath, _strings.Format("ToolFilesDone", result.OutputPaths.Count));
            }

            case ToolKind.Extract:
            {
                if (pages is null)
                {
                    throw new ModitException(ErrorCode.InvalidArgument, _strings.Get("ToolPagesRequired"));
                }

                PdfOperationResult result = await _service.ExtractAsync(source, pages, OutputPath, password, options, ct)
                    .ConfigureAwait(true);
                return (result.OutputPath, _strings.Format("ToolPagesDone", result.PageCount));
            }

            case ToolKind.Rotate:
            {
                int degrees = AngleIndex switch { 1 => 180, 2 => 270, _ => 90 };
                PdfOperationResult result = await _service.RotateAsync(source, degrees, pages, OutputPath, password, options, ct)
                    .ConfigureAwait(true);
                return (result.OutputPath, _strings.Format("ToolPagesDone", result.PageCount));
            }

            case ToolKind.Protect:
            {
                var protection = new ProtectOptions
                {
                    UserPassword = UserPassword,
                    OwnerPassword = string.IsNullOrEmpty(OwnerPassword) ? null : OwnerPassword,
                    AllowPrinting = AllowPrinting,
                    AllowCopying = AllowCopying,
                    AllowEditing = AllowEditing,
                };
                PdfOperationResult result = await _service.ProtectAsync(source, protection, OutputPath, password, options, ct)
                    .ConfigureAwait(true);
                return (result.OutputPath, _strings.Get("ToolProtectDone"));
            }

            case ToolKind.Unlock:
            {
                PdfOperationResult result = await _service.UnlockAsync(source, InputPassword, OutputPath, options, ct)
                    .ConfigureAwait(true);
                return (result.OutputPath, _strings.Get("ToolUnlockDone"));
            }

            case ToolKind.Optimize:
            {
                PdfOperationResult result = await _service.OptimizeAsync(source, OutputPath, FastWebView, password, options, ct)
                    .ConfigureAwait(true);
                double saved = result.InputBytes > 0
                    ? 100.0 * (result.InputBytes - result.OutputBytes) / result.InputBytes
                    : 0;
                return (result.OutputPath, saved > 0.05
                    ? _strings.Format("ToolOptimizeDone", FormatSize(result.InputBytes), FormatSize(result.OutputBytes), saved)
                    : _strings.Get("ToolOptimizeNoGain"));
            }

            case ToolKind.ConvertImages:
                return await ConvertImagesAsync(ct).ConfigureAwait(true);

            case ToolKind.Watermark:
            {
                string[] colors = ["#808080", "#C42B1C", "#1B66C9", "#000000"];
                var stamp = new PdfTextStamp(
                    WatermarkText, Whole(StampFontSize), colors[Math.Clamp(StampColorIndex, 0, colors.Length - 1)],
                    Math.Clamp(double.IsNaN(StampOpacity) ? 25 : StampOpacity, 0, 100) / 100, double.IsNaN(StampAngle) ? 0 : StampAngle,
                    SelectedPosition, Points(10), 1, string.Empty, Bold: true);
                return await EditAsync(source, new PdfEditOptions { Text = stamp, Pages = pages }, password, options, ct)
                    .ConfigureAwait(true);
            }

            case ToolKind.PageNumbers:
            {
                var stamp = new PdfTextStamp(
                    NumberFormat, Whole(StampFontSize), "#000000", 1, 0, SelectedPosition, Points(MarginMm),
                    Math.Max(0, Whole(StartNumber)), string.Empty, Bold: false);
                return await EditAsync(source, new PdfEditOptions { Text = stamp, Pages = pages }, password, options, ct)
                    .ConfigureAwait(true);
            }

            case ToolKind.AddImage:
            {
                if (string.IsNullOrWhiteSpace(StampImagePath))
                {
                    throw new ModitException(ErrorCode.InvalidArgument, _strings.Get("ToolImageRequired"));
                }

                var stamp = new PdfImageStamp(StampImagePath, Math.Max(1, Points(ImageWidthMm)), SelectedPosition, Points(MarginMm));
                return await EditAsync(source, new PdfEditOptions { Image = stamp, Pages = pages }, password, options, ct)
                    .ConfigureAwait(true);
            }

            case ToolKind.Crop:
            {
                var crop = new PdfCrop(Points(CropLeftMm), Points(CropTopMm), Points(CropRightMm), Points(CropBottomMm));
                if (crop is { LeftPt: 0, TopPt: 0, RightPt: 0, BottomPt: 0 })
                {
                    throw new ModitException(ErrorCode.InvalidArgument, _strings.Get("ToolCropRequired"));
                }

                return await EditAsync(source, new PdfEditOptions { Crop = crop, Pages = pages }, password, options, ct)
                    .ConfigureAwait(true);
            }

            case ToolKind.Flatten:
                return await EditAsync(source, new PdfEditOptions { Flatten = true }, password, options, ct).ConfigureAwait(true);

            case ToolKind.RemovePages:
            {
                if (pages is null)
                {
                    throw new ModitException(ErrorCode.InvalidArgument, _strings.Get("ToolRemoveRequired"));
                }

                PdfOperationResult result = await _service.RemovePagesAsync(source, pages, OutputPath, password, options, ct)
                    .ConfigureAwait(true);
                return (result.OutputPath, _strings.Format("ToolPagesDone", result.PageCount));
            }

            case ToolKind.ToText:
            {
                PdfOperationResult result = await _service.ExportTextAsync(source, OutputPath, pages, password, options, ct)
                    .ConfigureAwait(true);
                string message = _strings.Format("ToolTextDone", result.PageCount);
                return (result.OutputPath, result.Warnings.Count > 0 ? $"{message} {result.Warnings[0]}" : message);
            }

            case ToolKind.ImagesToPdf:
            {
                var layout = new ImagesToPdfOptions
                {
                    PageSize = (PdfPageSize)Math.Clamp(PageSizeIndex, 0, 2),
                    MarginPt = Whole(MarginMm) * 72 / 25.4,
                };
                PdfOperationResult result = await _service.FromImagesAsync(
                    [.. Files.Select(f => f.FilePath)], OutputPath, layout, options, ct).ConfigureAwait(true);
                return (result.OutputPath, _strings.Format("ToolPagesDone", result.PageCount));
            }

            default:
            {
                PdfMultiResult result = await _service.ExportImagesAsync(
                    source, pages, Dpi, OutputPath, password, options, progress, ct).ConfigureAwait(true);
                return (OutputPath, _strings.Format("ToolFilesDone", result.OutputPaths.Count));
            }
        }
    }

    private async Task<(string Path, string Message)> ConvertImagesAsync(CancellationToken ct)
    {
        var options = new ImageConvertOptions
        {
            TargetFormat = SelectedFormat.Id,
            Quality = (int)Math.Clamp(double.IsNaN(Quality) ? 85 : Quality, 1, 100),
            Lossless = Lossless && SelectedFormat.HasLossless,
            MaxWidthPx = Whole(MaxWidth),
            MaxHeightPx = Whole(MaxHeight),
            KeepMetadata = !StripMetadata,
            OutputDirectory = OutputPath,
            Collision = Overwrite ? CollisionPolicy.Overwrite : CollisionPolicy.Rename,
        };
        var progress = new Progress<ConvertProgress>(p => Progress = p.Total == 0 ? -1 : 100.0 * p.Done / p.Total);
        ConvertBatchResult batch = await _converter.ConvertAsync(
            [.. Files.Select(f => f.FilePath)], options, progress, ct).ConfigureAwait(true);

        ConvertItemResult? firstFailure = batch.Items.FirstOrDefault(i => i.Status == ConvertItemStatus.Failed);
        if (batch.Converted == 0 && firstFailure is not null)
        {
            throw new ModitException(firstFailure.Error, firstFailure.Message ?? string.Empty);
        }

        string message = _strings.Format("ToolConvertDone", batch.Converted);
        if (firstFailure is not null)
        {
            string reason = Describe(new ModitException(firstFailure.Error, firstFailure.Message ?? string.Empty));
            message += " " + _strings.Format("ToolConvertSomeFailed", batch.Failed, $"{Path.GetFileName(firstFailure.Input)}: {reason}");
        }

        // One result opens directly; several open their folder.
        string resultPath = batch.Converted == 1
            ? batch.Items.First(i => i.Status == ConvertItemStatus.Converted).Output!
            : OutputPath;
        return (resultPath, message);
    }

    private string SelectedPosition => StampPositions.All[Math.Clamp(PositionIndex, 0, StampPositions.All.Count - 1)];

    /// <summary>Millimetres (as typed) to PDF points.</summary>
    private static double Points(double millimetres) => Whole(millimetres * 100) / 100.0 * 72 / 25.4;

    private async Task<(string Path, string Message)> EditAsync(
        string source, PdfEditOptions edit, string? password, OutputOptions options, CancellationToken ct)
    {
        PdfOperationResult result = await _service.EditAsync(source, edit, OutputPath, password, options, ct).ConfigureAwait(true);
        string message = _strings.Format("ToolPagesDone", result.PageCount);
        if (result.Warnings.Count > 0)
        {
            message += " " + _strings.Get("ToolEditNoPassword");
        }

        return (result.OutputPath, message);
    }

    /// <summary>A NumberBox reports NaN when cleared; treat that, and negatives, as 0.</summary>
    private static int Whole(double value) => double.IsNaN(value) || value < 0 ? 0 : (int)Math.Round(value);

    private string Describe(ModitException ex) => ex.Code switch
    {
        ErrorCode.ConvertUnsupportedFormat => _strings.Get("ToolErrorNotAnImage"),
        ErrorCode.ConvertInputCorrupt => _strings.Get("ToolErrorImageDamaged"),
        ErrorCode.PdfEncryptedNeedsPassword => _strings.Get("ToolErrorNeedsPassword"),
        ErrorCode.PdfWrongPassword => _strings.Get("ToolErrorWrongPassword"),
        ErrorCode.PdfCorrupt => _strings.Get("ErrorPdfCorrupt"),
        ErrorCode.FileNotFound => _strings.Get("ErrorFileNotFound"),
        ErrorCode.FileAccessDenied => _strings.Get("ErrorFileAccessDenied"),
        ErrorCode.EngineCrashed or ErrorCode.EngineUnavailable or ErrorCode.EngineTimeout => _strings.Get("ErrorEngine"),
        _ => ex.Message,
    };

    private void ShowError(string title, string message)
    {
        ResultIsError = true;
        ResultTitle = title;
        ResultMessage = message;
        ResultPath = null;
    }

    private void SuggestOutput()
    {
        OnPropertyChanged(nameof(SuggestedFileName));
        if (!_outputChosenByUser)
        {
            OutputPath = BuildSuggestion() ?? string.Empty;
        }
    }

    /// <summary>
    /// A destination next to the first input that does not exist yet, so the default never
    /// overwrites anything: <c>report-rotated.pdf</c>, then <c>report-rotated (2).pdf</c> ...
    /// </summary>
    private string? BuildSuggestion()
    {
        if (Files.Count == 0)
        {
            return null;
        }

        ToolInputFile first = Files[0];
        if (Kind == ToolKind.ConvertImages)
        {
            // Results go next to the first image; name clashes get (2), (3) ... so nothing is replaced.
            return first.Folder;
        }

        string baseName = Path.GetFileNameWithoutExtension(first.Name);
        string suffix = Kind switch
        {
            ToolKind.Merge => "merged",
            ToolKind.Split => "parts",
            ToolKind.Extract => "pages",
            ToolKind.Rotate => "rotated",
            ToolKind.Protect => "protected",
            ToolKind.Unlock => "unlocked",
            ToolKind.Optimize => "optimized",
            ToolKind.ImagesToPdf => "album",
            ToolKind.Watermark => "watermarked",
            ToolKind.PageNumbers => "numbered",
            ToolKind.AddImage => "stamped",
            ToolKind.Crop => "cropped",
            ToolKind.Flatten => "flattened",
            ToolKind.RemovePages => "trimmed",
            ToolKind.ToText => "text",
            _ => "images",
        };
        string extension = ProducesFolder ? string.Empty : OutputExtension;

        for (int attempt = 1; ; attempt++)
        {
            string numbered = attempt == 1 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({attempt})");
            string candidate = Path.Combine(first.Folder, $"{baseName}-{suffix}{numbered}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }
}
