using QuotaGlass.Model;

namespace QuotaGlass.Tests.Model;

public sealed class SeverityPolicyTests
{
    [Theory]
    [InlineData(null, Severity.Normal)]
    [InlineData(79.99, Severity.Normal)]
    [InlineData(80d, Severity.Warning)]
    [InlineData(94.99, Severity.Warning)]
    [InlineData(95d, Severity.Critical)]
    public void FromPercent_UsesConfiguredThresholds(double? percent, Severity expected)
    {
        Assert.Equal(expected, SeverityPolicy.FromPercent(percent, 80, 95));
    }
}
