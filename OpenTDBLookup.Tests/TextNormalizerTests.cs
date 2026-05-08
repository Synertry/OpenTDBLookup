using FluentAssertions;
using OpenTDBLookup.Helpers;

namespace OpenTDBLookup.Tests;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  Hello   World  ", "hello world")]
    [InlineData("MixedCASE Sentence", "mixedcase sentence")]
    [InlineData("Tabs\tand\nlines\r\nmix", "tabs and lines mix")]
    public void Normalize_collapses_whitespace_trims_and_lowercases(string? input, string expected)
    {
        TextNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("What&#039;s up?", "what's up?")]
    [InlineData("&quot;Caf&eacute;&quot;", "\"café\"")]
    [InlineData("A &amp; B", "a & b")]
    public void Normalize_decodes_html_entities(string input, string expected)
    {
        TextNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_unicode_NFD_to_NFC_so_accented_chars_compare_equal()
    {
        // "Cafe" + combining acute (NFD) versus precomposed "é" (NFC).
        const string nfd = "Café";
        const string nfc = "Café";

        var normalizedNfd = TextNormalizer.Normalize(nfd);
        var normalizedNfc = TextNormalizer.Normalize(nfc);

        normalizedNfd.Should().Be(normalizedNfc);
        normalizedNfd.Should().Be("café");
    }

    [Fact]
    public void Normalize_returns_empty_for_whitespace_only_input()
    {
        TextNormalizer.Normalize("\t  \n").Should().BeEmpty();
    }
}
