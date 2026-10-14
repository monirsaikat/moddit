using System.CommandLine;
using System.Globalization;
using Modit.Core.Convert;
using Modit.Core.Pdf;

namespace Modit.Cli.Commands;

/// <summary><c>modit convert</c>: image conversion, the CLI side of the app's Convert images tool.</summary>
internal static class ConvertCommands
{
    public static Command Build()
    {
        var inputs = new Argument<string[]>("inputs")
        {
            Description = "Images to convert: PNG, JPEG, WebP, AVIF, HEIC, TIFF, GIF, BMP, ICO or SVG.",
            Arity = ArgumentArity.OneOrMore,
        };
        var to = new Option<string>("--to")
        {
            Description = "Target format: " + string.Join(", ", ImageFormats.Writable.Select(f => f.Id)) + ".",
            Required = true,
        };
        var output = new Option<string?>("--out", "-o") { Description = "Folder for the results. Default: next to each source." };
        var quality = new Option<int>("--quality", "-q") { Description = "1-100 for JPEG, WebP and AVIF.", DefaultValueFactory = _ => 85 };
        var lossless = new Option<bool>("--lossless") { Description = "Lossless WebP or AVIF." };
        var maxWidth = new Option<int>("--max-width") { Description = "Shrink to at most this many pixels wide." };
        var maxHeight = new Option<int>("--max-height") { Description = "Shrink to at most this many pixels high." };
        var strip = new Option<bool>("--strip-metadata") { Description = "Drop EXIF (including GPS), XMP and ICC data." };
        var background = new Option<string>("--background")
        {
            Description = "Color for transparent areas in JPEG and BMP, as #RRGGBB.",
            DefaultValueFactory = _ => "#FFFFFF",
        };
        var dpi = new Option<double>("--dpi") { Description = "Resolution for SVG input.", DefaultValueFactory = _ => 96 };
        var firstFrame = new Option<bool>("--first-frame") { Description = "Write only the first frame of animated GIF or WebP." };
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace existing files instead of adding (2), (3)..." };
        var skip = new Option<bool>("--skip-existing") { Description = "Leave existing files alone and skip those inputs." };

        var command = new Command("convert", "Convert images between formats, optionally resizing them.")
        {
            inputs, to, output, quality, lossless, maxWidth, maxHeight, strip, background, dpi, firstFrame, overwrite, skip,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            if (parse.GetValue(overwrite) && parse.GetValue(skip))
            {
                throw new ModitException(ErrorCode.InvalidArgument, "Use either --overwrite or --skip-existing, not both.");
            }

            var options = new ImageConvertOptions
            {
                TargetFormat = parse.GetValue(to)!,
                OutputDirectory = parse.GetValue(output),
                Quality = parse.GetValue(quality),
                Lossless = parse.GetValue(lossless),
                MaxWidthPx = parse.GetValue(maxWidth),
                MaxHeightPx = parse.GetValue(maxHeight),
                KeepMetadata = !parse.GetValue(strip),
                Background = parse.GetValue(background)!,
                VectorDpi = parse.GetValue(dpi),
                KeepAnimation = !parse.GetValue(firstFrame),
                Collision = parse.GetValue(overwrite) ? CollisionPolicy.Overwrite
                    : parse.GetValue(skip) ? CollisionPolicy.Skip
                    : CollisionPolicy.Rename,
            };

            ConvertBatchResult result = await new ImageConvertService(engine)
                .ConvertAsync(ExpandInputs(parse.GetValue(inputs)!), options, cancellationToken: ct).ConfigureAwait(false);
            Write(json, result);

            // Exit code 1 = partial: some inputs failed (docs/plan/05, "CLI mapping").
            return result.Failed == 0 ? ExitCodes.Ok : result.Converted + result.Skipped > 0 ? ExitCodes.Partial : ExitCodes.OperationError;
        }, ct));
        return command;
    }

    /// <summary><c>modit pdf from-images</c>: one PDF page per image.</summary>
    public static Command FromImages()
    {
        var inputs = new Argument<string[]>("images") { Description = "Images in page order.", Arity = ArgumentArity.OneOrMore };
        var output = new Option<string>("--out", "-o") { Description = "The PDF to write.", Required = true };
        var pageSize = new Option<PdfPageSize>("--page-size")
        {
            Description = "image (page fits the picture), a4 or letter.",
            DefaultValueFactory = _ => PdfPageSize.Image,
        };
        var margin = new Option<double>("--margin") { Description = "Border around each image in points (72 per inch)." };
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace the PDF if it exists." };
        var command = new Command("from-images", "Combine images into one PDF, one image per page.")
        {
            inputs, output, pageSize, margin, overwrite,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            PdfOperationResult result = await new PdfService(engine).FromImagesAsync(
                ExpandInputs(parse.GetValue(inputs)!),
                parse.GetValue(output)!,
                new ImagesToPdfOptions { PageSize = parse.GetValue(pageSize), MarginPt = parse.GetValue(margin) },
                new OutputOptions { Overwrite = parse.GetValue(overwrite) },
                ct).ConfigureAwait(false);
            if (json)
            {
                CliRunner.WriteJson(
                    new PdfWriteOutput(result.OutputPath, result.PageCount, result.Warnings, result.InputBytes, result.OutputBytes),
                    CliJsonContext.Default.PdfWriteOutput);
            }
            else
            {
                Console.WriteLine($"Wrote {result.OutputPath} ({result.PageCount} pages).");
            }

            return ExitCodes.Ok;
        }, ct));
        return command;
    }

    /// <summary>Expands wildcards such as <c>*.png</c> or <c>photos\*.jpg</c>, which cmd.exe passes through unexpanded.</summary>
    private static List<string> ExpandInputs(IEnumerable<string> patterns)
    {
        var files = new List<string>();
        foreach (string pattern in patterns)
        {
            string name = Path.GetFileName(pattern);
            if (name.Contains('*', StringComparison.Ordinal) || name.Contains('?', StringComparison.Ordinal))
            {
                string directory = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : ".";
                if (Directory.Exists(directory))
                {
                    files.AddRange(Directory.EnumerateFiles(directory, name).Order(StringComparer.OrdinalIgnoreCase));
                }
            }
            else
            {
                files.Add(pattern);
            }
        }

        return files.Count > 0
            ? files
            : throw new ModitException(ErrorCode.FileNotFound, "No input files matched.");
    }

    private static void Write(bool json, ConvertBatchResult result)
    {
        if (json)
        {
            CliRunner.WriteJson(
                new ConvertOutput(
                    result.Converted,
                    result.Skipped,
                    result.Failed,
                    [.. result.Items.Select(i => new ConvertItemOutput(
                        i.Input, i.Output, i.Status.ToString().ToLowerInvariant(),
                        i.Error == ErrorCode.None ? null : i.Error.ToString(), i.Message, i.Width, i.Height,
                        i.InputBytes, i.OutputBytes, i.Warnings is { Count: > 0 } w ? w : null))]),
                CliJsonContext.Default.ConvertOutput);
            return;
        }

        foreach (ConvertItemResult item in result.Items)
        {
            switch (item.Status)
            {
                case ConvertItemStatus.Converted:
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"ok      {item.Output} ({item.Width}x{item.Height}, {item.OutputBytes:N0} bytes)"));
                    foreach (string warning in item.Warnings ?? [])
                    {
                        Console.WriteLine($"        warning: {warning}");
                    }

                    break;
                case ConvertItemStatus.Skipped:
                    Console.WriteLine($"skipped {item.Input}: {item.Message}");
                    break;
                default:
                    Console.Error.WriteLine($"failed  {item.Input}: {item.Error}: {item.Message}");
                    break;
            }
        }

        Console.WriteLine($"{result.Converted} converted, {result.Skipped} skipped, {result.Failed} failed.");
    }
}
