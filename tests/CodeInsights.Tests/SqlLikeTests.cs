using CodeInsights.Api.Data;

public sealed class SqlLikeTests
{
    [Theory]
    [InlineData("no-eval", "no-eval")]
    [InlineData("100%", "100\\%")]
    [InlineData("no_eval", "no\\_eval")]
    [InlineData("C:\\src", "C:\\\\src")]
    public void ExactEscapesWildcardsAndEscapeCharacter(string value, string expected) =>
        Assert.Equal(expected, SqlLike.Exact(value));

    [Fact]
    public void ContainingWrapsTheEscapedValue() =>
        Assert.Equal("%a\\_b%", SqlLike.Containing("a_b"));
}
