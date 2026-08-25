using System.Text.Json;
using System.IO;

namespace AiStatus.Core;

public sealed class SettingsStore : IDisposable
{
    private static readonly string[] RequiredProviderIds = ["claude", "codex", "ollama"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly FileSystemWatcher _watcher;
    private System.Threading.Timer? _reloadTimer;
    private AppSettings _current = AppSettings.Default;
    private bool _disposed;

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);
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
        (bool isValid, AppSettings settings) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        AppSettings loaded = isValid ? settings : AppSettings.Default;

        lock (_gate)
        {
            ThrowIfDisposed();
            _current = loaded;
        }

        return loaded;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings))
        {
            throw new ArgumentException("Settings must be complete and valid.", nameof(settings));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
        }

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
            _reloadTimer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadFromWatcher()
    {
        _ = ReloadFromWatcherAsync();
    }

    private async Task ReloadFromWatcherAsync()
    {
        (bool isValid, AppSettings settings) = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
        if (!isValid)
        {
            return;
        }

        EventHandler<AppSettings>? changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _current = settings;
            changed = Changed;
        }

        changed?.Invoke(this, settings);
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
            return settings is not null && IsValid(settings)
                ? (true, settings)
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

    private static bool IsValid(AppSettings settings)
    {
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

        return RequiredProviderIds.All(providerId => settings.Providers.TryGetValue(providerId, out ProviderSettings? provider) && provider is not null);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
