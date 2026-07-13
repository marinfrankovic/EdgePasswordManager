using EdgePasswordBulkManager.Helpers;

namespace EdgePasswordBulkManager.Tests;

public sealed class DomainListParserTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("0.0.0.0 www.example.com", "example.com")]
    [InlineData("EXAMPLE.COM. # comment", "example.com")]
    public void ParseLine_AcceptsSupportedFormats(string input, string expected)
    {
        Assert.Equal(expected, DomainListParser.ParseLine(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment")]
    [InlineData("..com")]
    [InlineData("a..com")]
    [InlineData("-bad.example")]
    [InlineData("bad-.example")]
    [InlineData("1.1.1.1")]
    [InlineData("localhost")]
    [InlineData("https://example.com/path")]
    public void ParseLine_RejectsMalformedOrNonDomainValues(string input)
    {
        Assert.Null(DomainListParser.ParseLine(input));
    }

    [Fact]
    public void Match_MatchesParentDomainButNotLookalike()
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "example.com" };

        Assert.Equal("example.com", DomainListParser.Match("login.example.com", domains));
        Assert.Null(DomainListParser.Match("notexample.com", domains));
    }
}