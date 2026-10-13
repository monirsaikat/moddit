using System.Globalization;
using System.Text;

namespace Modit.Core.Pdf;

/// <summary>
/// A user-written page selection such as <c>1-3, 7, 10-end, odd</c>. Pages are 1-based in text.
/// Order and repetition are preserved (<c>3,1,1</c> yields page 3 then page 1 twice), which is what
/// reordering and duplicating pages need. Grammar:
/// <code>
/// range   := part ("," part)*
/// part    := page | page "-" page | "odd" | "even" | "all"
/// page    := number | "end" | "last"
/// </code>
/// A reversed span (<c>5-2</c>) counts down.
/// </summary>
public sealed class PageRange
{
    private readonly IReadOnlyList<Part> _parts;

    private PageRange(IReadOnlyList<Part> parts, string text)
    {
        _parts = parts;
        Text = text;
    }

    /// <summary>Every page, in order.</summary>
    public static PageRange All { get; } = new([new Part(PartKind.Span, new Bound(1), Bound.End)], "all");

    public string Text { get; }

    public static PageRange Parse(string text) =>
        TryParse(text, out PageRange? range, out string? error)
            ? range!
            : throw new ModitException(ErrorCode.InvalidArgument, error!);

    public static bool TryParse(string? text, out PageRange? range, out string? error)
    {
        range = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The page range is empty.";
            return false;
        }

        var parts = new List<Part>();
        foreach (string raw in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                error = "The page range contains an empty item.";
                return false;
            }

            string token = raw.ToLowerInvariant();
            switch (token)
            {
                case "odd":
                    parts.Add(new Part(PartKind.Odd, default, default));
                    continue;
                case "even":
                    parts.Add(new Part(PartKind.Even, default, default));
                    continue;
                case "all":
                    parts.Add(new Part(PartKind.Span, new Bound(1), Bound.End));
                    continue;
            }

            int dash = token.IndexOf('-', StringComparison.Ordinal);
            string first = dash < 0 ? token : token[..dash].Trim();
            string second = dash < 0 ? token : token[(dash + 1)..].Trim();
            if (!TryParseBound(first, out Bound from) || !TryParseBound(second, out Bound to))
            {
                error = $"'{raw}' is not a valid page or page span.";
                return false;
            }

            parts.Add(new Part(PartKind.Span, from, to));
        }

        range = new PageRange(parts, text.Trim());
        return true;
    }

    /// <summary>Expands the selection to 0-based page indexes for a document of <paramref name="pageCount"/> pages.</summary>
    public IReadOnlyList<int> Resolve(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        var pages = new List<int>();
        foreach (Part part in _parts)
        {
            switch (part.Kind)
            {
                case PartKind.Odd:
                    for (int p = 1; p <= pageCount; p += 2)
                    {
                        pages.Add(p - 1);
                    }

                    break;
                case PartKind.Even:
                    for (int p = 2; p <= pageCount; p += 2)
                    {
                        pages.Add(p - 1);
                    }

                    break;
                default:
                    int from = part.From.Resolve(pageCount);
                    int to = part.To.Resolve(pageCount);
                    if (from > pageCount || to > pageCount)
                    {
                        throw new ModitException(
                            ErrorCode.PdfPageOutOfRange,
                            $"Page range '{Text}' refers to page {Math.Max(from, to)}, but the document has {pageCount} pages.");
                    }

                    int step = from <= to ? 1 : -1;
                    for (int p = from; p != to + step; p += step)
                    {
                        pages.Add(p - 1);
                    }

                    break;
            }
        }

        return pages;
    }

    /// <summary>The selection in QPDF's range syntax, fully resolved (for example <c>1-3,7,10-12</c>).</summary>
    public string ToQpdfRange(int pageCount)
    {
        IReadOnlyList<int> pages = Resolve(pageCount);
        if (pages.Count == 0)
        {
            throw new ModitException(ErrorCode.InvalidArgument, $"Page range '{Text}' selects no pages.");
        }

        var builder = new StringBuilder();
        int i = 0;
        while (i < pages.Count)
        {
            // Compress ascending or descending runs into "a-b".
            int start = pages[i];
            int end = start;
            int direction = 0;
            while (i + 1 < pages.Count)
            {
                int delta = pages[i + 1] - end;
                if ((delta == 1 || delta == -1) && (direction == 0 || direction == delta))
                {
                    direction = delta;
                    end = pages[++i];
                }
                else
                {
                    break;
                }
            }

            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(CultureInfo.InvariantCulture, $"{start + 1}");
            if (end != start)
            {
                builder.Append(CultureInfo.InvariantCulture, $"-{end + 1}");
            }

            i++;
        }

        return builder.ToString();
    }

    public override string ToString() => Text;

    private static bool TryParseBound(string token, out Bound bound)
    {
        if (token is "end" or "last" or "z")
        {
            bound = Bound.End;
            return true;
        }

        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page >= 1)
        {
            bound = new Bound(page);
            return true;
        }

        bound = default;
        return false;
    }

    private enum PartKind
    {
        Span,
        Odd,
        Even,
    }

    private readonly record struct Bound(int Page)
    {
        /// <summary>Page 0 is never valid in text, so it encodes "the last page".</summary>
        public static Bound End => new(0);

        public int Resolve(int pageCount) => Page == 0 ? pageCount : Page;
    }

    private readonly record struct Part(PartKind Kind, Bound From, Bound To);
}
