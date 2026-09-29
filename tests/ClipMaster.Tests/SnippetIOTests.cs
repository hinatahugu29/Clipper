using ClipMaster.Data;
using ClipMaster.Services;
using Xunit;

namespace ClipMaster.Tests;

public class SnippetIOTests
{
    private static readonly Snippet[] Sample =
    {
        new() { Category = "業務", Title = "挨拶", Body = "お世話になっております。\r\n\"引用\",カンマ含む" },
        new() { Category = "", Title = "単行", Body = "abc" },
    };

    [Fact]
    public void Csv_RoundTrip_KeepsNewlinesQuotesAndCommas()
    {
        var back = SnippetIO.ParseCsv(SnippetIO.ToCsv(Sample));
        Assert.Equal(2, back.Count);
        Assert.Equal(Sample[0].Body, back[0].Body);
        Assert.Equal("業務", back[0].Category);
        Assert.Equal("単行", back[1].Title);
    }

    [Fact]
    public void Csv_WithoutHeader_IsAccepted()
    {
        var back = SnippetIO.ParseCsv("a,b,c\r\n");
        Assert.Single(back);
        Assert.Equal("b", back[0].Title);
    }

    [Fact]
    public void Txt_RoundTrip()
    {
        var back = SnippetIO.ParseTxt(SnippetIO.ToTxt(Sample), "x");
        Assert.Equal(2, back.Count);
        Assert.Equal(Sample[0].Body, back[0].Body);
        Assert.Equal("業務", back[0].Category);
        Assert.Equal("abc", back[1].Body);
    }

    [Fact]
    public void Txt_WithoutSeparator_BecomesOneSnippetTitledByFileName()
    {
        var back = SnippetIO.ParseTxt("hello\r\nworld\r\n", "memo");
        Assert.Single(back);
        Assert.Equal("memo", back[0].Title);
        Assert.Equal("hello\r\nworld", back[0].Body);
    }
}
