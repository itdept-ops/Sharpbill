namespace Sharpbill.Migrator.Tests;

public sealed class CliOptionsTests
{
    [Theory]
    [InlineData("validate", "Validate")]
    [InlineData("migrate", "Migrate")]
    [InlineData("seed-demo", "SeedDemo")]
    public void TryParseRecognizesSupportedCommands(
        string argument,
        string expectedCommand)
    {
        bool parsed = CliOptions.TryParse(
            [argument],
            out CliOptions? options,
            out string? error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal(expectedCommand, options.Command.ToString());
    }

    [Fact]
    public void TryParseRejectsDryRunForReadOnlyValidate()
    {
        bool parsed = CliOptions.TryParse(
            ["validate", "--dry-run"],
            out CliOptions? options,
            out string? error);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.Contains("always read-only", error, StringComparison.Ordinal);
    }
}
