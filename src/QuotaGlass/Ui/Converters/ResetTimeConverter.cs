using System.Globalization;
using System.Windows.Data;

namespace QuotaGlass.Ui.Converters;

public sealed class ResetTimeConverter : IValueConverter
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _localTimeZone;

    public ResetTimeConverter()
        : this(TimeProvider.System, TimeZoneInfo.Local)
    {
    }

    public ResetTimeConverter(TimeProvider timeProvider, TimeZoneInfo localTimeZone)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _localTimeZone = localTimeZone ?? throw new ArgumentNullException(nameof(localTimeZone));
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset resetTime)
        {
            return string.Empty;
        }

        TimeSpan remaining = resetTime - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        if (remaining < TimeSpan.FromHours(24))
        {
            int totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return hours > 0
                ? FormattableString.Invariant($"in {hours}h{minutes:00}")
                : FormattableString.Invariant($"in {minutes}m");
        }

        DateTimeOffset localReset = TimeZoneInfo.ConvertTime(resetTime, _localTimeZone);
        return localReset.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
