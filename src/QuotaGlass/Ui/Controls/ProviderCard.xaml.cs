using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QuotaGlass.Model;

namespace QuotaGlass.Ui.Controls;

public partial class ProviderCard : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot),
        typeof(ProviderSnapshot),
        typeof(ProviderCard),
        new PropertyMetadata(null, OnDisplayInputChanged));

    public static readonly DependencyProperty PollIntervalProperty = DependencyProperty.Register(
        nameof(PollInterval),
        typeof(TimeSpan),
        typeof(ProviderCard),
        new PropertyMetadata(TimeSpan.FromSeconds(60), OnDisplayInputChanged));

    private TimeProvider _timeProvider = TimeProvider.System;

    public ProviderCard()
    {
        InitializeComponent();
    }

    public ProviderSnapshot? Snapshot
    {
        get => (ProviderSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public TimeSpan PollInterval
    {
        get => (TimeSpan)GetValue(PollIntervalProperty);
        set => SetValue(PollIntervalProperty, value);
    }

    public TimeProvider TimeProvider
    {
        get => _timeProvider;
        set
        {
            _timeProvider = value ?? throw new ArgumentNullException(nameof(value));
            NotifyDisplayPropertiesChanged();
        }
    }

    public string HealthText => Snapshot is null ? string.Empty : ProviderDisplayText.GetHealthText(Snapshot);

    public string UpdatedText => Snapshot is null
        ? string.Empty
        : ProviderDisplayText.GetUpdatedText(Snapshot, PollInterval, TimeProvider);

    public Visibility WindowsVisibility => Snapshot is not null && !Snapshot.Windows.IsDefaultOrEmpty
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Severity SignalSeverity
    {
        get
        {
            if (Snapshot is null)
            {
                return Severity.Normal;
            }

            if (Snapshot.Health is HealthState.AuthExpired or HealthState.Unreachable)
            {
                return Severity.Critical;
            }

            if (Snapshot.Health == HealthState.Degraded)
            {
                return Severity.Warning;
            }

            return Snapshot.Windows.IsDefaultOrEmpty
                ? Severity.Normal
                : Snapshot.Windows.Max(window => window.Severity);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static void OnDisplayInputChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((ProviderCard)dependencyObject).NotifyDisplayPropertiesChanged();

    private void NotifyDisplayPropertiesChanged()
    {
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(SignalSeverity));
        OnPropertyChanged(nameof(WindowsVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
