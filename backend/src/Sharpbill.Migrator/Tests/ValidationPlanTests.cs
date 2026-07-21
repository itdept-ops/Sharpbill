namespace Sharpbill.Migrator.Tests;

public sealed class ValidationPlanTests
{
    [Fact]
    public void CompareExactReturnsNoIssuesForOrderIndependentExactPlan()
    {
        string[] expected = ["table|users", "table|roles"];
        string[] actual = ["table|roles", "table|users"];

        IReadOnlyList<ValidationIssue> issues = SchemaComparison.CompareExact(
            "tables",
            expected,
            actual);

        Assert.Empty(issues);
    }

    [Fact]
    public void CompareExactReportsMissingAndUnexpectedFactsWithoutRepairingThem()
    {
        string[] expected = ["index|ix_users_email", "index|ix_users_role_id"];
        string[] actual = ["index|ix_users_email", "index|ix_unreviewed"];

        IReadOnlyList<ValidationIssue> issues = SchemaComparison.CompareExact(
            "indexes",
            expected,
            actual);

        Assert.Collection(
            issues,
            issue =>
            {
                Assert.Equal("missing", issue.Kind);
                Assert.Equal("index|ix_users_role_id", issue.Fact);
            },
            issue =>
            {
                Assert.Equal("unexpected", issue.Kind);
                Assert.Equal("index|ix_unreviewed", issue.Fact);
            });
    }
}
