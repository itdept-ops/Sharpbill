namespace Sharpbill.Migrator.Tests;

public sealed class SqlScriptSplitterTests
{
    [Fact]
    public void SplitIgnoresCommentsAndQuotedSemicolons()
    {
        const string sql =
            """
            -- first statement ; is in this comment
            CREATE TABLE `odd;name` (`value` varchar(20));
            # another ignored ;
            INSERT INTO `odd;name` VALUES ('a;''b'), ("c;d");
            /* final ; block */ SELECT 1
            """;

        IReadOnlyList<string> statements = SqlScriptSplitter.Split(sql);

        Assert.Equal(3, statements.Count);
        Assert.Contains("`odd;name`", statements[0], StringComparison.Ordinal);
        Assert.Contains("'a;''b'", statements[1], StringComparison.Ordinal);
        Assert.Equal("SELECT 1", statements[2]);
    }

    [Fact]
    public void SplitRejectsUnterminatedContent()
    {
        Assert.Throws<FormatException>(() => SqlScriptSplitter.Split("SELECT 'unfinished;"));
        Assert.Throws<FormatException>(() => SqlScriptSplitter.Split("SELECT 1 /* unfinished"));
    }
}
