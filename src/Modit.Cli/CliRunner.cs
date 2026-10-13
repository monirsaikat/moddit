using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modit.Engine.Client;

namespace Modit.Cli;

/// <summary>
/// Runs one command body with an engine client and turns failures into the documented exit codes
/// (docs/plan/05, "CLI mapping"). With <c>--json</c>, errors are a JSON object on stderr.
/// </summary>
internal static class CliRunner
{
    public static async Task<int> RunAsync(
        ParseResult parseResult, Func<EngineClient, bool, Task> body, CancellationToken cancellationToken)
    {
        bool json = parseResult.GetValue(CliOptions.Json);
        try
        {
            var client = new EngineClient();
            await using (client.ConfigureAwait(false))
            {
                await body(client, json).ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WriteError(json, ErrorCode.Cancelled, "Cancelled.");
        }
        catch (ModitException ex)
        {
            return WriteError(json, ex.Code, ex.Message);
        }
    }

    public static void WriteJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, typeInfo));

    private static int WriteError(bool json, ErrorCode code, string message)
    {
        if (json)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(
                new CliError(code.ToString(), (int)code, message), CliJsonContext.Default.CliError));
        }
        else
        {
            Console.Error.WriteLine($"error {code}: {message}");
        }

        return ExitCodes.FromError(code);
    }
}

internal sealed record CliError(string Error, int Code, string Message);

internal sealed record PdfInfoOutput(
    string Path,
    int PageCount,
    string PdfVersion,
    bool Encrypted,
    long FileSizeBytes,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<PdfPageOutput> Pages);

internal sealed record PdfPageOutput(int Number, double WidthPt, double HeightPt, string? Label);

internal sealed record PdfWriteOutput(
    string Output, int PageCount, IReadOnlyList<string> Warnings, long InputBytes = 0, long OutputBytes = 0);

internal sealed record PdfFilesOutput(IReadOnlyList<string> Outputs);

internal sealed record PdfRenderOutput(string Output, int Page, int WidthPx, int HeightPx, double RenderMilliseconds);

internal sealed record PdfTextOutput(int Page, string Text);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliError))]
[JsonSerializable(typeof(PdfInfoOutput))]
[JsonSerializable(typeof(PdfWriteOutput))]
[JsonSerializable(typeof(PdfRenderOutput))]
[JsonSerializable(typeof(PdfFilesOutput))]
[JsonSerializable(typeof(IReadOnlyList<PdfTextOutput>))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
