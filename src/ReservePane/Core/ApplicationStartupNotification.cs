namespace ReservePane.Core;

internal sealed class ApplicationStartupNotification(
    Action showNotification,
    RollingFileLog log)
{
    private readonly Action _showNotification = showNotification
        ?? throw new ArgumentNullException(nameof(showNotification));
    private readonly RollingFileLog _log = log
        ?? throw new ArgumentNullException(nameof(log));

    public void Show()
    {
        try
        {
            _showNotification();
        }
        catch (Exception exception)
        {
            TryLogFailure(exception);
        }
    }

    private void TryLogFailure(Exception exception)
    {
        try
        {
            _log.Write(LogArea.Ui, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }
}
