using System.Text.Json;
using System.IO;

namespace QuotaGlass.Core;

public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly string _path;
    private readonly TimeSpan _reloadDelay;
    private readonly RollingFileLog? _log;
    private readonly FileSystemWatcher _watcher;
    private System.Threading.Timer? _reloadTimer;
    private AppSettings _current = AppSettings.Default;
    private bool _disposed;

    public SettingsStore(string path)
        : this(path, TimeSpan.FromMilliseconds(250), null)
    {
    }

    public SettingsStore(string path, RollingFileLog log)
        : this(path, TimeSpan.FromMilliseconds(250), log)
    {
    }

    internal SettingsStore(string path, TimeSpan reloadDelay)
        : this(path, reloadDelay, null)
    {
    }

    internal SettingsStore(string path, TimeSpan reloadDelay, RollingFileLog? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (reloadDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reloadDelay));
        }

        _path = Path.GetFullPath(path);
        _reloadDelay = reloadDelay;
        _log = log;
        string directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("The settings path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnSettingsFileChanged;
        _watcher.Created += OnSettingsFileChanged;
        _watcher.Renamed += OnSettingsFileRenamed;
    }

    public event EventHandler<AppSettings>? Changed;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposedLocked();
            (bool isValid, AppSettings settings) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            AppSettings loaded = isValid ? settings : AppSettings.Default;

            lock (_gate)
            {
                ThrowIfDisposed();
                _current = loaded;
            }

            return loaded;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryNormalize(settings, out AppSettings normalized))
        {
            throw new ArgumentException("Settings must be complete and valid.", nameof(settings));
        }

        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposedLocked();
            await WriteAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings lastKnownGood;
            lock (_gate)
            {
                ThrowIfDisposed();
                lastKnownGood = _current;
            }

            (bool isValid, AppSettings fileSettings) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            AppSettings current = isValid ? fileSettings : lastKnownGood;

            AppSettings candidate = update(current)
                ?? throw new ArgumentException("The settings updater returned null.", nameof(update));
            if (!TryNormalize(candidate, out AppSettings updated))
            {
                throw new ArgumentException("The settings updater produced invalid settings.", nameof(update));
            }

            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _persistenceGate.Release();
        }
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        string temporaryPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);

            lock (_gate)
            {
                ThrowIfDisposed();
                _current = settings;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reloadTimer?.Dispose();
            _watcher.Dispose();
        }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs args)
    {
        ScheduleReload();
    }

    private void OnSettingsFileRenamed(object sender, RenamedEventArgs args)
    {
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _reloadTimer ??= new System.Threading.Timer(static state => ((SettingsStore)state!).ReloadFromWatcher(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _reloadTimer.Change(_reloadDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadFromWatcher()
    {
        _ = ReloadFromWatcherAsync();
    }

    private async Task ReloadFromWatcherAsync()
    {
        await _persistenceGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        EventHandler<AppSettings>? changed = null;
        AppSettings? changedSettings = null;
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            (bool isValid, AppSettings settings) = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (!isValid)
            {
                _log?.Write(LogArea.Settings, LogOutcome.Invalid);
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _current = settings;
                changed = Changed;
                changedSettings = settings;
            }
        }
        finally
        {
            _persistenceGate.Release();
        }

        changed?.Invoke(this, changedSettings!);
    }

    private async Task<(bool IsValid, AppSettings Settings)> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return (true, AppSettings.Default);
        }

        try
        {
            await using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            AppSettings? settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return settings is not null && TryNormalize(settings, out AppSettings normalized)
                ? (true, normalized)
                : (false, AppSettings.Default);
        }
        catch (JsonException)
        {
            return (false, AppSettings.Default);
        }
        catch (IOException)
        {
            return (false, AppSettings.Default);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, AppSettings.Default);
        }
    }

    private static bool TryNormalize(AppSettings settings, out AppSettings normalized)
    {
        normalized = AppSettings.Default;
        if (settings.PollInterval <= TimeSpan.Zero || settings.IdleInterval <= TimeSpan.Zero ||
            !double.IsFinite(settings.WarningPercent) || !double.IsFinite(settings.CriticalPercent) ||
            settings.WarningPercent < 0 || settings.WarningPercent >= settings.CriticalPercent || settings.CriticalPercent > 100 ||
            settings.Providers is null || string.IsNullOrWhiteSpace(settings.Hotkey) ||
            !Enum.IsDefined(settings.OverlayCorner))
        {
            return false;
        }

        if (settings.OverlayPosition is { X: var x, Y: var y } && (!double.IsFinite(x) || !double.IsFinite(y)))
        {
            return false;
        }

        if (settings.Providers.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            return false;
        }

        var providers = settings.Providers.ToBuilder();
        foreach ((string providerId, ProviderSettings provider) in AppSettings.Default.Providers)
        {
            providers.TryAdd(providerId, provider);
        }

        normalized = settings with { Providers = providers.ToImmutable() };
        return true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfDisposedLocked()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
        }
    }
}
