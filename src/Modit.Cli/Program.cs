using System.CommandLine;
using Modit.Cli.Commands;

namespace Modit.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("Modit: view, merge, edit and convert PDFs and other files from the command line.");
        root.Options.Add(CliOptions.Json);
        root.Subcommands.Add(PdfCommands.Build());

        ParseResult parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
            }

            return ExitCodes.InvalidArguments;
        }

        return await parseResult.InvokeAsync().ConfigureAwait(false);
    }
}

internal static class CliOptions
{
    /// <summary>Available on every subcommand: machine-readable output on stdout, errors as JSON on stderr.</summary>
    public static Option<bool> Json { get; } = new("--json")
    {
        Description = "Write machine-readable JSON instead of text.",
        Recursive = true,
    };
}
