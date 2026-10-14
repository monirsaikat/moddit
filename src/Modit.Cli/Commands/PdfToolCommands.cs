using System.CommandLine;
using Modit.Core.Pdf;

namespace Modit.Cli.Commands;

/// <summary>CLI counterparts of the tools on the app's Home screen. One subcommand per operation.</summary>
internal static partial class PdfCommands
{
    private static readonly Option<bool> Overwrite = new("--overwrite")
    {
        Description = "Replace output files that already exist.",
    };

    private static Option<string> Out(string description) =>
        new("--out", "-o") { Description = description, Required = true };

    private static void AddTools(Command pdf)
    {
        pdf.Subcommands.Add(Split());
        pdf.Subcommands.Add(Extract());
        pdf.Subcommands.Add(Rotate());
        pdf.Subcommands.Add(Protect());
        pdf.Subcommands.Add(Unlock());
        pdf.Subcommands.Add(Optimize());
        pdf.Subcommands.Add(Images());
        pdf.Subcommands.Add(ConvertCommands.FromImages());
    }

    private static Command Split()
    {
        var file = new Argument<string>("file") { Description = "The PDF to split." };
        var every = new Option<int>("--every") { Description = "Pages per output file.", DefaultValueFactory = _ => 1 };
        var output = Out("Folder for the parts (name_001.pdf, name_002.pdf, ...).");
        var command = new Command("split", "Split a PDF into files of N pages each.") { file, every, output, Overwrite, Password };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            PdfMultiResult result = await new PdfService(engine).SplitAsync(
                parse.GetValue(file)!, parse.GetValue(every), parse.GetValue(output)!, parse.GetValue(Password),
                Options(parse), cancellationToken: ct).ConfigureAwait(false);
            WriteMany(json, result, "Wrote");
        }, ct));
        return command;
    }

    private static Command Extract()
    {
        var file = new Argument<string>("file") { Description = "The PDF to take pages from." };
        var pages = new Option<string>("--pages") { Description = "Pages to keep, in order: 1-3,7 or odd or 5,2.", Required = true };
        var output = Out("The PDF to write.");
        var command = new Command("extract", "Write selected pages to a new PDF.") { file, pages, output, Overwrite, Password };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            PdfOperationResult result = await new PdfService(engine).ExtractAsync(
                parse.GetValue(file)!, PageRange.Parse(parse.GetValue(pages)!), parse.GetValue(output)!,
                parse.GetValue(Password), Options(parse), ct).ConfigureAwait(false);
            WriteOne(json, result, "Extracted to");
        }, ct));
        return command;
    }

    private static Command Rotate()
    {
        var file = new Argument<string>("file") { Description = "The PDF to rotate." };
        var angle = new Option<int>("--angle") { Description = "Clockwise rotation: 90, 180 or 270.", DefaultValueFactory = _ => 90 };
        var pages = new Option<string?>("--pages") { Description = "Pages to rotate. Default: all." };
        var output = Out("The PDF to write.");
        var command = new Command("rotate", "Rotate pages by a quarter, half or three-quarter turn.") { file, angle, pages, output, Overwrite, Password };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            PageRange? range = parse.GetValue(pages) is { } text ? PageRange.Parse(text) : null;
            PdfOperationResult result = await new PdfService(engine).RotateAsync(
                parse.GetValue(file)!, parse.GetValue(angle), range, parse.GetValue(output)!,
                parse.GetValue(Password), Options(parse), ct).ConfigureAwait(false);
            WriteOne(json, result, "Rotated to");
        }, ct));
        return command;
    }

    private static Command Protect()
    {
        var file = new Argument<string>("file") { Description = "The PDF to protect." };
        var user = new Option<string>("--user-password") { Description = "Password needed to open the document.", Required = true };
        var owner = new Option<string?>("--owner-password") { Description = "Password that lifts restrictions. Default: the user password." };
        var noPrint = new Option<bool>("--no-print") { Description = "Forbid printing." };
        var noCopy = new Option<bool>("--no-copy") { Description = "Forbid copying text and images." };
        var noEdit = new Option<bool>("--no-edit") { Description = "Forbid changing the document." };
        var output = Out("The protected PDF to write.");
        var command = new Command("protect", "Encrypt a PDF with AES-256 and optional restrictions.")
        {
            file, user, owner, noPrint, noCopy, noEdit, output, Overwrite, Password,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            var protection = new ProtectOptions
            {
                UserPassword = parse.GetValue(user)!,
                OwnerPassword = parse.GetValue(owner),
                AllowPrinting = !parse.GetValue(noPrint),
                AllowCopying = !parse.GetValue(noCopy),
                AllowEditing = !parse.GetValue(noEdit),
            };
            PdfOperationResult result = await new PdfService(engine).ProtectAsync(
                parse.GetValue(file)!, protection, parse.GetValue(output)!, parse.GetValue(Password), Options(parse), ct)
                .ConfigureAwait(false);
            WriteOne(json, result, "Protected");
        }, ct));
        return command;
    }

    private static Command Unlock()
    {
        var file = new Argument<string>("file") { Description = "The encrypted PDF." };
        var output = Out("The PDF to write without encryption.");
        var command = new Command("unlock", "Remove encryption from a PDF whose password you know (--password).") { file, output, Overwrite, Password };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            string password = parse.GetValue(Password)
                ?? throw new ModitException(ErrorCode.InvalidArgument, "--password is required to unlock a document.");
            PdfOperationResult result = await new PdfService(engine).UnlockAsync(
                parse.GetValue(file)!, password, parse.GetValue(output)!, Options(parse), ct).ConfigureAwait(false);
            WriteOne(json, result, "Unlocked to");
        }, ct));
        return command;
    }

    private static Command Optimize()
    {
        var file = new Argument<string>("file") { Description = "The PDF to shrink." };
        var web = new Option<bool>("--fast-web-view") { Description = "Linearize so the first page shows before the download ends." };
        var output = Out("The optimized PDF to write.");
        var command = new Command("optimize", "Reduce file size without changing quality (lossless).") { file, web, output, Overwrite, Password };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            PdfOperationResult result = await new PdfService(engine).OptimizeAsync(
                parse.GetValue(file)!, parse.GetValue(output)!, parse.GetValue(web), parse.GetValue(Password),
                Options(parse), ct).ConfigureAwait(false);
            if (json)
            {
                WriteOne(json, result, string.Empty);
                return;
            }

            double saved = result.InputBytes > 0 ? 100.0 * (result.InputBytes - result.OutputBytes) / result.InputBytes : 0;
            Console.WriteLine(
                $"Optimized to {result.OutputPath}: {result.InputBytes:N0} -> {result.OutputBytes:N0} bytes ({saved:0.#}% smaller).");
        }, ct));
        return command;
    }

    private static Command Images()
    {
        var file = new Argument<string>("file") { Description = "The PDF to export." };
        var pages = new Option<string?>("--pages") { Description = "Pages to export. Default: all." };
        var dpi = new Option<double>("--dpi") { Description = "Resolution in dots per inch.", DefaultValueFactory = _ => 150 };
        var output = Out("Folder for the images (name_p001.png, ...).");
        var command = new Command("images", "Save pages as PNG images.") { file, pages, dpi, output, Overwrite, Password };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            PageRange? range = parse.GetValue(pages) is { } text ? PageRange.Parse(text) : null;
            PdfMultiResult result = await new PdfService(engine).ExportImagesAsync(
                parse.GetValue(file)!, range, parse.GetValue(dpi), parse.GetValue(output)!, parse.GetValue(Password),
                Options(parse), cancellationToken: ct).ConfigureAwait(false);
            WriteMany(json, result, "Wrote");
        }, ct));
        return command;
    }

    private static OutputOptions Options(ParseResult parse) => new() { Overwrite = parse.GetValue(Overwrite) };

    private static void WriteOne(bool json, PdfOperationResult result, string verb)
    {
        if (json)
        {
            CliRunner.WriteJson(
                new PdfWriteOutput(result.OutputPath, result.PageCount, result.Warnings, result.InputBytes, result.OutputBytes),
                CliJsonContext.Default.PdfWriteOutput);
            return;
        }

        Console.WriteLine($"{verb} {result.OutputPath} ({result.PageCount} pages).");
    }

    private static void WriteMany(bool json, PdfMultiResult result, string verb)
    {
        if (json)
        {
            CliRunner.WriteJson(new PdfFilesOutput(result.OutputPaths), CliJsonContext.Default.PdfFilesOutput);
            return;
        }

        Console.WriteLine($"{verb} {result.OutputPaths.Count} file(s):");
        foreach (string path in result.OutputPaths)
        {
            Console.WriteLine($"  {path}");
        }
    }
}
