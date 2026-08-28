namespace ReservePane.Platform;

internal sealed class InstallerShutdownSignal : IDisposable
{
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _registration;

    public InstallerShutdownSignal(string executablePath, Func<Task> shutdownAsync)
    {
        ArgumentNullException.ThrowIfNull(shutdownAsync);
        _signal = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            InstallerShutdownSignalName.FromExecutablePath(executablePath));
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            (_, _) => _ = shutdownAsync(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _signal.Dispose();
    }
}
