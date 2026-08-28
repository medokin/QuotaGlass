using System.Globalization;
using QuotaGlass.Model;
using CommunityToolkit.WinUI.Notifications;
using Windows.UI.Notifications;

namespace QuotaGlass.Platform;

internal interface IToastPublisher
{
    void Show(ToastContent content);
}

internal sealed class ToolkitToastPublisher : IToastPublisher
{
    public void Show(ToastContent content)
    {
        var notification = new ToastNotification(content.GetXml());
        ToastNotificationManagerCompat.CreateToastNotifier().Show(notification);
    }
}

public sealed class ToastNotifier
{
    private readonly IToastPublisher _publisher;

    public ToastNotifier()
        : this(new ToolkitToastPublisher())
    {
    }

    internal ToastNotifier(IToastPublisher publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public void Show(StatusAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var builder = new ToastContentBuilder()
            .AddText(alert.ProviderLabel)
            .AddText(alert.Message);

        if (alert.CycleResetsAt is DateTimeOffset resetTime)
        {
            builder.AddText(resetTime.ToUniversalTime().ToString(
                "'Resets' yyyy-MM-dd HH:mm 'UTC'",
                CultureInfo.InvariantCulture));
        }

        _publisher.Show(builder.GetToastContent());
    }
}
