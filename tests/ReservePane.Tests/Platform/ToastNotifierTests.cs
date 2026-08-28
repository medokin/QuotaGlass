using System.Xml.Linq;
using ReservePane.Model;
using ReservePane.Platform;
using CommunityToolkit.WinUI.Notifications;

namespace ReservePane.Tests.Platform;

public sealed class ToastNotifierTests
{
    [Fact]
    public void Show_BuildsProviderMessageAndResetTimeWithoutDisplayingToast()
    {
        var publisher = new FakeToastPublisher();
        var notifier = new ToastNotifier(publisher);
        var alert = new StatusAlert(
            "claude",
            "Claude",
            "weekly",
            AlertKind.Critical,
            96,
            DateTimeOffset.Parse("2026-08-26T14:30:00Z"),
            "weekly usage reached 96%.");

        notifier.Show(alert);

        string[] text = ReadText(publisher.Content!);
        Assert.Equal(
            ["Claude", "weekly usage reached 96%.", "Resets 2026-08-26 14:30 UTC"],
            text);
    }

    [Fact]
    public void Show_OmitsResetLineWhenAlertHasNoResetTime()
    {
        var publisher = new FakeToastPublisher();
        var notifier = new ToastNotifier(publisher);
        var alert = new StatusAlert(
            "codex",
            "Codex",
            null,
            AlertKind.AuthExpired,
            null,
            null,
            "re-auth: run codex login");

        notifier.Show(alert);

        Assert.Equal(["Codex", "re-auth: run codex login"], ReadText(publisher.Content!));
    }

    private static string[] ReadText(ToastContent content) =>
        XDocument.Parse(content.GetContent())
            .Descendants("text")
            .Select(element => element.Value)
            .ToArray();

    private sealed class FakeToastPublisher : IToastPublisher
    {
        public ToastContent? Content { get; private set; }
        public void Show(ToastContent content) => Content = content;
    }
}
