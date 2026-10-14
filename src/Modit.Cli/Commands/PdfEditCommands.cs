using System.CommandLine;
using System.Globalization;
using Modit.Core.Pdf;
using Modit.Engine;

namespace Modit.Cli.Commands;

/// <summary>CLI side of the page editing tools: organize, remove pages, stamps, crop, flatten, text export.</summary>
internal static partial class PdfCommands
{
    private static void AddEditTools(Command pdf)
    {
        pdf.Subcommands.Add(Organize());
        pdf.Subcommands.Add(RemovePages());
        pdf.Subcommands.Add(Watermark());
        pdf.Subcommands.Add(PageNumbers());
        pdf.Subcommands.Add(AddImage());
        pdf.Subcommands.Add(Crop());
        pdf.Subcommands.Add(Flatten());
        pdf.Subcommands.Add(ToText());
    }

    private static Option<string> PositionOption(string defaultValue)
    {
        var option = new Option<string>("--position")
        {
            Description = "Where on the page: " + string.Join(", ", StampPositions.All) + ".",
            DefaultValueFactory = _ => defaultValue,
        };
        option.AcceptOnlyFromAmong([.. StampPositions.All]);
        return option;
    }

    private static Option<string?> PagesOption() => new("--pages") { Description = "Pages to change, such as 1-3,7 or odd. Default: all." };

    private static PageRange? RangeOf(ParseResult parse, Option<string?> option) =>
        parse.GetValue(option) is { } text ? PageRange.Parse(text) : null;

    private static Command Organize()
    {
        var files = new Argument<string[]>("files") { Description = "Source PDFs, numbered 1, 2, ... in this order.", Arity = ArgumentArity.OneOrMore };
        var order = new Option<string>("--order")
        {
            Description = "Pages of the result, comma separated: [file:]pages[@angle] or blank. Example: \"3,1@90,blank,2:1-4\".",
            Required = true,
        };
        var output = Out("The PDF to write.");
        var command = new Command("organize", "Build a PDF from pages of one or more PDFs in any order, turned, with blank pages.")
        {
            files, order, output, Overwrite, Password,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            var service = new PdfService(engine);
            string[] paths = parse.GetValue(files)!;
            string? password = parse.GetValue(Password);
            var infos = new List<DocumentInfo>();
            foreach (string path in paths)
            {
                infos.Add(await service.GetInfoAsync(path, password, ct).ConfigureAwait(false));
            }

            List<OrganizePage> pages = ParseOrder(parse.GetValue(order)!, infos);
            PdfOperationResult result = await service.OrganizeAsync(
                [.. paths.Select(p => new OrganizeSource(p, password))], pages, parse.GetValue(output)!, Options(parse), ct)
                .ConfigureAwait(false);
            WriteOne(json, result, "Wrote");
        }, ct));
        return command;
    }

    /// <summary>Parses "3,1@90,blank,2:1-4". A blank page takes the size of the page before it (A4 if first).</summary>
    internal static List<OrganizePage> ParseOrder(string order, IReadOnlyList<DocumentInfo> documents)
    {
        var pages = new List<OrganizePage>();
        foreach (string raw in order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(raw, "blank", StringComparison.OrdinalIgnoreCase))
            {
                (double width, double height) = pages.Count == 0 || pages[^1].IsBlank
                    ? pages.Count == 0 ? (595.28, 841.89) : (pages[^1].BlankWidthPt, pages[^1].BlankHeightPt)
                    : SizeOf(documents[pages[^1].SourceIndex].Pages[pages[^1].PageIndex]);
                pages.Add(OrganizePage.Blank(width, height));
                continue;
            }

            string token = raw;
            int rotation = 0;
            int at = token.IndexOf('@', StringComparison.Ordinal);
            if (at >= 0)
            {
                rotation = int.TryParse(token[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int angle) && angle % 90 == 0
                    ? angle
                    : throw new ModitException(ErrorCode.InvalidArgument, $"'{raw}': the angle after @ must be 90, 180 or 270.");
                token = token[..at];
            }

            int file = 1;
            int colon = token.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                file = int.TryParse(token[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= documents.Count
                    ? n
                    : throw new ModitException(ErrorCode.InvalidArgument, $"'{raw}': file number must be 1-{documents.Count}.");
                token = token[(colon + 1)..];
            }

            foreach (int index in PageRange.Parse(token).Resolve(documents[file - 1].PageCount))
            {
                pages.Add(new OrganizePage(file - 1, index, rotation));
            }
        }

        return pages;
    }

    private static (double Width, double Height) SizeOf(PageInfo page) => (page.WidthPt, page.HeightPt);

    private static Command RemovePages()
    {
        var file = new Argument<string>("file") { Description = "The PDF to remove pages from." };
        var pages = new Option<string>("--pages") { Description = "Pages to remove, such as 2,5-7 or even.", Required = true };
        var output = Out("The PDF to write.");
        var command = new Command("remove-pages", "Write a copy without the selected pages.") { file, pages, output, Overwrite, Password };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            PdfOperationResult result = await new PdfService(engine).RemovePagesAsync(
                parse.GetValue(file)!, PageRange.Parse(parse.GetValue(pages)!), parse.GetValue(output)!,
                parse.GetValue(Password), Options(parse), ct).ConfigureAwait(false);
            WriteOne(json, result, "Wrote");
        }, ct));
        return command;
    }

    private static Command Watermark()
    {
        var file = new Argument<string>("file") { Description = "The PDF to watermark." };
        var text = new Option<string>("--text") { Description = "Watermark text. May use {page}, {pages}, {name}, {date}.", Required = true };
        var size = new Option<double>("--size") { Description = "Font size in points.", DefaultValueFactory = _ => 60 };
        var opacity = new Option<double>("--opacity") { Description = "0 (invisible) to 1 (solid).", DefaultValueFactory = _ => 0.25 };
        var angle = new Option<double>("--angle") { Description = "Counter-clockwise angle in degrees.", DefaultValueFactory = _ => 45 };
        var color = new Option<string>("--color") { Description = "Color as #RRGGBB.", DefaultValueFactory = _ => "#808080" };
        var position = PositionOption("center");
        var pages = PagesOption();
        var output = Out("The PDF to write.");
        var command = new Command("watermark", "Write text across pages, for example DRAFT or CONFIDENTIAL.")
        {
            file, text, size, opacity, angle, color, position, pages, output, Overwrite, Password,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            var stamp = new PdfTextStamp(
                parse.GetValue(text)!, parse.GetValue(size), parse.GetValue(color)!, parse.GetValue(opacity),
                parse.GetValue(angle), parse.GetValue(position)!, 36, 1, string.Empty, Bold: true);
            await EditAsync(engine, json, parse, file, output, new PdfEditOptions { Text = stamp, Pages = RangeOf(parse, pages) }, ct)
                .ConfigureAwait(false);
        }, ct));
        return command;
    }

    private static Command PageNumbers()
    {
        var file = new Argument<string>("file") { Description = "The PDF to number." };
        var format = new Option<string>("--format")
        {
            Description = "Text per page. {page} = number, {pages} = last number, {name} = file name, {date} = today.",
            DefaultValueFactory = _ => "{page}",
        };
        var start = new Option<int>("--start") { Description = "Number of the first page.", DefaultValueFactory = _ => 1 };
        var size = new Option<double>("--size") { Description = "Font size in points.", DefaultValueFactory = _ => 11 };
        var margin = new Option<double>("--margin") { Description = "Distance from the page edge in points.", DefaultValueFactory = _ => 28 };
        var position = PositionOption("bottom-center");
        var pages = PagesOption();
        var output = Out("The PDF to write.");
        var command = new Command("page-numbers", "Add page numbers, or any header or footer text, to pages.")
        {
            file, format, start, size, margin, position, pages, output, Overwrite, Password,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            var stamp = new PdfTextStamp(
                parse.GetValue(format)!, parse.GetValue(size), "#000000", 1, 0, parse.GetValue(position)!,
                parse.GetValue(margin), parse.GetValue(start), string.Empty, Bold: false);
            await EditAsync(engine, json, parse, file, output, new PdfEditOptions { Text = stamp, Pages = RangeOf(parse, pages) }, ct)
                .ConfigureAwait(false);
        }, ct));
        return command;
    }

    private static Command AddImage()
    {
        var file = new Argument<string>("file") { Description = "The PDF to add the picture to." };
        var image = new Option<string>("--image") { Description = "Picture file: PNG, JPEG, SVG, WebP ...", Required = true };
        var width = new Option<double>("--width") { Description = "Width on the page in points (72 per inch).", DefaultValueFactory = _ => 144 };
        var margin = new Option<double>("--margin") { Description = "Distance from the page edge in points.", DefaultValueFactory = _ => 36 };
        var position = PositionOption("bottom-right");
        var pages = PagesOption();
        var output = Out("The PDF to write.");
        var command = new Command("add-image", "Place a picture, such as a logo or signature, on pages.")
        {
            file, image, width, margin, position, pages, output, Overwrite, Password,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            var stamp = new PdfImageStamp(parse.GetValue(image)!, parse.GetValue(width), parse.GetValue(position)!, parse.GetValue(margin));
            await EditAsync(engine, json, parse, file, output, new PdfEditOptions { Image = stamp, Pages = RangeOf(parse, pages) }, ct)
                .ConfigureAwait(false);
        }, ct));
        return command;
    }

    private static Command Crop()
    {
        var file = new Argument<string>("file") { Description = "The PDF to crop." };
        var left = new Option<double>("--left") { Description = "Points to trim from the left edge." };
        var top = new Option<double>("--top") { Description = "Points to trim from the top edge." };
        var right = new Option<double>("--right") { Description = "Points to trim from the right edge." };
        var bottom = new Option<double>("--bottom") { Description = "Points to trim from the bottom edge." };
        var pages = PagesOption();
        var output = Out("The PDF to write.");
        var command = new Command("crop", "Trim the edges of pages (the hidden part stays in the file).")
        {
            file, left, top, right, bottom, pages, output, Overwrite, Password,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            var crop = new PdfCrop(parse.GetValue(left), parse.GetValue(top), parse.GetValue(right), parse.GetValue(bottom));
            await EditAsync(engine, json, parse, file, output, new PdfEditOptions { Crop = crop, Pages = RangeOf(parse, pages) }, ct)
                .ConfigureAwait(false);
        }, ct));
        return command;
    }

    private static Command Flatten()
    {
        var file = new Argument<string>("file") { Description = "The PDF to flatten." };
        var output = Out("The PDF to write.");
        var command = new Command("flatten", "Burn form fields and comments into the pages so they can no longer be changed.")
        {
            file, output, Overwrite, Password,
        };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
            await EditAsync(engine, json, parse, file, output, new PdfEditOptions { Flatten = true }, ct).ConfigureAwait(false), ct));
        return command;
    }

    private static Command ToText()
    {
        var file = new Argument<string>("file") { Description = "The PDF to read." };
        var pages = PagesOption();
        var output = Out("The text file to write (UTF-8; pages separated by form feeds).");
        var command = new Command("to-text", "Save the text of a PDF as a .txt file.") { file, pages, output, Overwrite, Password };
        command.SetAction((parse, ct) => CliRunner.RunAsync(parse, async (engine, json) =>
        {
            PdfOperationResult result = await new PdfService(engine).ExportTextAsync(
                parse.GetValue(file)!, parse.GetValue(output)!, RangeOf(parse, pages), parse.GetValue(Password), Options(parse), ct)
                .ConfigureAwait(false);
            WriteOne(json, result, "Wrote");
        }, ct));
        return command;
    }

    private static async Task EditAsync(
        Engine.Client.EngineClient engine, bool json, ParseResult parse, Argument<string> file, Option<string> output,
        PdfEditOptions edit, CancellationToken ct)
    {
        PdfOperationResult result = await new PdfService(engine).EditAsync(
            parse.GetValue(file)!, edit, parse.GetValue(output)!, parse.GetValue(Password), Options(parse), ct).ConfigureAwait(false);
        WriteOne(json, result, "Wrote");
        if (!json)
        {
            foreach (string warning in result.Warnings)
            {
                Console.WriteLine($"  warning: {warning}");
            }
        }
    }
}
