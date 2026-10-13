using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modit.Core.Pdf;

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
}

/// <summary>One input file of a tool, with the optional page selection used by Merge.</summary>
public sealed partial class ToolInputFile(string path) : ObservableObject
{
    public string FilePath { get; } = path;

    public string Name { get; } = Path.GetFileName(path);

    public string Folder { get; } = Path.GetDirectoryName(path) ?? string.Empty;

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
    private readonly IStringProvider _strings;
    private bool _outputChosenByUser;
    private CancellationTokenSource? _cancellation;

    public ToolViewModel(ToolKind kind, PdfService service, IStringProvider strings)
    {
        Kind = kind;
        _service = service;
        _strings = strings;
        Title = strings.Get($"Tool{kind}Title");
        Description = strings.Get($"Tool{kind}Description");
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

    public bool AllowsMultipleFiles => Kind == ToolKind.Merge;

    /// <summary>True when the tool writes several files into a folder instead of one file.</summary>
    public bool ProducesFolder => Kind is ToolKind.Split or ToolKind.Images;

    public bool ShowPerFilePages => Kind == ToolKind.Merge;

    public bool ShowPages => Kind is ToolKind.Extract or ToolKind.Rotate or ToolKind.Images;

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
            if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
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

            default:
            {
                PdfMultiResult result = await _service.ExportImagesAsync(
                    source, pages, Dpi, OutputPath, password, options, progress, ct).ConfigureAwait(true);
                return (OutputPath, _strings.Format("ToolFilesDone", result.OutputPaths.Count));
            }
        }
    }

    private string Describe(ModitException ex) => ex.Code switch
    {
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
            _ => "images",
        };
        string extension = ProducesFolder ? string.Empty : ".pdf";

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
