using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Modit.Core.Imaging;
using Modit.Core.Pdf;
using Modit.Engine;
using Modit.Engine.Client;

namespace Modit.Cli.Commands;

internal static partial class PdfCommands
{
    private static readonly Option<string?> Password = new("--password")
    {
        Description = "Password for an encrypted PDF.",
    };

    public static Command Build()
    {
        var pdf = new Command("pdf", "Work with PDF documents.");
        pdf.Subcommands.Add(Info());
        pdf.Subcommands.Add(Merge());
        pdf.Subcommands.Add(Render());
        pdf.Subcommands.Add(Text());
        AddTools(pdf);
        return pdf;
    }

    private static Command Info()
    {
        var file = new Argument<string>("file") { Description = "The PDF to inspect." };
        var command = new Command("info", "Show page count, sizes, version, encryption and metadata.")
        {
            file,
            Password,
        };

        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            string path = Path.GetFullPath(parse.GetValue(file)!);
            DocumentInfo info = await new PdfService(engine).GetInfoAsync(path, parse.GetValue(Password), ct).ConfigureAwait(false);

            if (json)
            {
                CliRunner.WriteJson(
                    new PdfInfoOutput(
                        path, info.PageCount, info.PdfVersion, info.IsEncrypted, info.FileSizeBytes, info.Metadata,
                        [.. info.Pages.Select(p => new PdfPageOutput(p.Index + 1, p.WidthPt, p.HeightPt, p.Label))]),
                    CliJsonContext.Default.PdfInfoOutput);
                return;
            }

            Console.WriteLine($"File:       {path}");
            Console.WriteLine($"Pages:      {info.PageCount}");
            Console.WriteLine($"PDF:        {info.PdfVersion}");
            Console.WriteLine($"Encrypted:  {(info.IsEncrypted ? "yes" : "no")}");
            Console.WriteLine($"Size:       {info.FileSizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes");
            if (info.PageCount > 0)
            {
                PageInfo first = info.Pages[0];
                Console.WriteLine($"First page: {first.WidthPt:0.##} x {first.HeightPt:0.##} pt");
            }

            foreach ((string key, string value) in info.Metadata)
            {
                Console.WriteLine($"{key + ":",-12}{value}");
            }
        }, ct));
        return command;
    }

    private static Command Merge()
    {
        var inputs = new Argument<string[]>("inputs")
        {
            Description = "Input PDFs in order. Append a page range to take part of a file, for example b.pdf:1-3,7 or c.pdf:odd.",
            Arity = ArgumentArity.OneOrMore,
        };
        var output = new Option<string>("--out", "-o") { Description = "The merged PDF to write.", Required = true };
        var command = new Command("merge", "Combine PDFs (or parts of them) into one document.")
        {
            inputs,
            output,
            Overwrite,
            Password,
        };

        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            string? password = parse.GetValue(Password);
            List<MergeSource> sources = [.. parse.GetValue(inputs)!.Select(i => ParseSource(i, password))];
            PdfOperationResult result = await new PdfService(engine).MergeAsync(
                sources,
                parse.GetValue(output)!,
                Options(parse),
                ct).ConfigureAwait(false);

            if (json)
            {
                CliRunner.WriteJson(
                    new PdfWriteOutput(result.OutputPath, result.PageCount, result.Warnings),
                    CliJsonContext.Default.PdfWriteOutput);
                return;
            }

            Console.WriteLine($"Merged {sources.Count} input(s) into {result.OutputPath} ({result.PageCount} pages).");
            foreach (string warning in result.Warnings)
            {
                Console.WriteLine($"warning: {warning}");
            }
        }, ct));
        return command;
    }

    private static Command Render()
    {
        var file = new Argument<string>("file") { Description = "The PDF to render." };
        var page = new Option<int>("--page", "-p") { Description = "Page number, starting at 1.", DefaultValueFactory = _ => 1 };
        var dpi = new Option<double>("--dpi") { Description = "Resolution in dots per inch.", DefaultValueFactory = _ => 150 };
        var output = new Option<string>("--out", "-o") { Description = "The PNG file to write.", Required = true };
        var command = new Command("render", "Render one page to a PNG image.")
        {
            file,
            page,
            dpi,
            output,
            Password,
        };

        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            double resolution = parse.GetValue(dpi);
            if (resolution is < 8 or > 1200)
            {
                throw new ModitException(ErrorCode.InvalidArgument, "--dpi must be between 8 and 1200.");
            }

            int pageNumber = parse.GetValue(page);
            string outputPath = Path.GetFullPath(parse.GetValue(output)!);
            DocumentInfo info = await engine.OpenDocumentAsync(parse.GetValue(file)!, parse.GetValue(Password), ct).ConfigureAwait(false);
            using RenderedImage image = await engine.RenderPageAsync(info, pageNumber - 1, resolution / 72.0, ct).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            string temp = outputPath + ".modit-tmp";
            try
            {
                using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    PngEncoder.WriteBgra(stream, image.Pixels, image.WidthPx, image.HeightPx, image.StrideBytes);
                }

                File.Move(temp, outputPath, overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }

            if (json)
            {
                CliRunner.WriteJson(
                    new PdfRenderOutput(outputPath, pageNumber, image.WidthPx, image.HeightPx, image.RenderMilliseconds),
                    CliJsonContext.Default.PdfRenderOutput);
                return;
            }

            Console.WriteLine(
                $"Rendered page {pageNumber} at {resolution:0.##} dpi to {outputPath} " +
                $"({image.WidthPx} x {image.HeightPx} px, {image.RenderMilliseconds:0.0} ms).");
        }, ct));
        return command;
    }

    private static Command Text()
    {
        var file = new Argument<string>("file") { Description = "The PDF to read." };
        var pages = new Option<string?>("--pages") { Description = "Pages to extract, for example 1-3,7 or odd. Default: all." };
        var command = new Command("text", "Extract the text of a PDF.")
        {
            file,
            pages,
            Password,
        };

        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            DocumentInfo info = await engine.OpenDocumentAsync(parse.GetValue(file)!, parse.GetValue(Password), ct).ConfigureAwait(false);
            PageRange range = parse.GetValue(pages) is { } text ? PageRange.Parse(text) : PageRange.All;

            var results = new List<PdfTextOutput>();
            foreach (int index in range.Resolve(Math.Max(1, info.PageCount)))
            {
                string pageText = await engine.GetPageTextAsync(info.DocumentId, index, ct).ConfigureAwait(false);
                results.Add(new PdfTextOutput(index + 1, pageText));
            }

            if (json)
            {
                CliRunner.WriteJson<IReadOnlyList<PdfTextOutput>>(results, CliJsonContext.Default.IReadOnlyListPdfTextOutput);
                return;
            }

            foreach (PdfTextOutput result in results)
            {
                Console.WriteLine(result.Text);
            }
        }, ct));
        return command;
    }

    /// <summary>Splits <c>path[:range]</c>. A drive letter colon (<c>C:\a.pdf</c>) is not a range separator.</summary>
    internal static MergeSource ParseSource(string input, string? password)
    {
        Match match = SourceWithRange().Match(input);
        if (match.Success && !File.Exists(input))
        {
            return new MergeSource(match.Groups["path"].Value, PageRange.Parse(match.Groups["range"].Value), password);
        }

        return new MergeSource(input, null, password);
    }

    [GeneratedRegex(@"^(?<path>.+):(?<range>[0-9a-zA-Z,\-\s]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceWithRange();
}
