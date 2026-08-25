namespace AiStatus.Model;

public static class SeverityPolicy
{
    public static Severity FromPercent(double? percent, double warning, double critical)
    {
        if (double.IsNaN(warning) || warning < 0 || warning >= critical)
        {
            throw new ArgumentOutOfRangeException(nameof(warning));
        }

        if (double.IsNaN(critical) || critical > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(critical));
        }

        if (percent is null)
        {
            return Severity.Normal;
        }

        if (percent >= critical)
        {
            return Severity.Critical;
        }

        return percent >= warning ? Severity.Warning : Severity.Normal;
    }
}
