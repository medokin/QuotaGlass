using ReservePane.Core;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Core;

public sealed class ApplicationStartupNotificationTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public void Show_DeliveryFailureIsLoggedAndDoesNotEscape()
    {
        string logPath = Path.Combine(_directory.Path, "log.txt");
        var notification = new ApplicationStartupNotification(
            () => throw new InvalidOperationException("Sensitive delivery detail."),
            new RollingFileLog(logPath));

        notification.Show();

        string log = File.ReadAllText(logPath);
        Assert.Contains(" ui failed exception=InvalidOperationException", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive delivery detail.", log, StringComparison.Ordinal);
    }

    public void Dispose() => _directory.Dispose();
}
