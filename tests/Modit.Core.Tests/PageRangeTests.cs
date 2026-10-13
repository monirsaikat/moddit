using Modit.Core.Pdf;

namespace Modit.Core.Tests;

public sealed class PageRangeTests
{
    [Theory]
    [InlineData("1", 10, new[] { 0 })]
    [InlineData("1-3", 10, new[] { 0, 1, 2 })]
    [InlineData("1-3, 7", 10, new[] { 0, 1, 2, 6 })]
    [InlineData("8-end", 10, new[] { 7, 8, 9 })]
    [InlineData("last", 10, new[] { 9 })]
    [InlineData("odd", 5, new[] { 0, 2, 4 })]
    [InlineData("even", 5, new[] { 1, 3 })]
    [InlineData("3,1,1", 5, new[] { 2, 0, 0 })]
    [InlineData("4-2", 5, new[] { 3, 2, 1 })]
    [InlineData("ALL", 3, new[] { 0, 1, 2 })]
    public void Resolves_to_zero_based_indexes_in_written_order(string text, int pageCount, int[] expected)
    {
        Assert.Equal(expected, PageRange.Parse(text).Resolve(pageCount));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("1,,2")]
    [InlineData("a-b")]
    [InlineData("1-")]
    [InlineData("-3")]
    [InlineData("1.5")]
    public void Rejects_malformed_text(string text)
    {
        Assert.False(PageRange.TryParse(text, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Pages_beyond_the_document_are_an_error_not_a_silent_clamp()
    {
        ModitException ex = Assert.Throws<ModitException>(() => PageRange.Parse("2-9").Resolve(5));

        Assert.Equal(ErrorCode.PdfPageOutOfRange, ex.Code);
    }

    [Theory]
    [InlineData("1-3,7", 10, "1-3,7")]
    [InlineData("odd", 6, "1,3,5")]
    [InlineData("5-end", 8, "5-8")]
    [InlineData("3,2,1", 5, "3-1")]
    [InlineData("1,1,2", 5, "1,1-2")]
    public void Qpdf_range_is_fully_resolved_and_compressed(string text, int pageCount, string expected)
    {
        Assert.Equal(expected, PageRange.Parse(text).ToQpdfRange(pageCount));
    }

    [Fact]
    public void Qpdf_range_expands_back_to_the_same_pages_for_random_selections()
    {
        var random = new Random(20261013);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            int pageCount = random.Next(1, 40);
            string text = string.Join(",", Enumerable.Range(0, random.Next(1, 8)).Select(_ =>
            {
                int a = random.Next(1, pageCount + 1);
                int b = random.Next(1, pageCount + 1);
                return random.Next(3) == 0 ? $"{a}" : $"{a}-{b}";
            }));

            PageRange original = PageRange.Parse(text);
            PageRange roundTripped = PageRange.Parse(original.ToQpdfRange(pageCount));

            Assert.Equal(original.Resolve(pageCount), roundTripped.Resolve(pageCount));
        }
    }
}
