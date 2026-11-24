using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TBDMovies.Application.Trakt;
using TBDMovies.UI.Services;

namespace TBDMovies.UI.Controls;

public sealed partial class TraktSyncCapsule : UserControl
{
    private ITraktUiActions? _subscribedTrakt;
    private TraktSyncProgress? _lastProgress;
    private int _manualSyncGeneration;
    private bool _isManualSyncing;

    public TraktSyncCapsule()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppServices.TraktSyncCompleted -= OnTraktSyncCompleted;
        AppServices.TraktSyncCompleted += OnTraktSyncCompleted;
        EnsureTraktSubscription();
        ApplyLocalization();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppServices.TraktSyncCompleted -= OnTraktSyncCompleted;
        if (_subscribedTrakt is not null)
        {
            _subscribedTrakt.SyncProgressChanged -= OnSyncProgressChanged;
            _subscribedTrakt = null;
        }
        _isManualSyncing = false;
        _lastProgress = null;
    }

    private void EnsureTraktSubscription()
    {
        if (!IsLoaded) return;
        var current = AppServices.Trakt;
        if (ReferenceEquals(_subscribedTrakt, current)) return;

        if (_subscribedTrakt is not null)
        {
            _subscribedTrakt.SyncProgressChanged -= OnSyncProgressChanged;
            _subscribedTrakt = null;
        }

        if (current is not null)
        {
            _subscribedTrakt = current;
            _subscribedTrakt.SyncProgressChanged += OnSyncProgressChanged;
        }
    }

    public void ApplyLocalization()
    {
        var syncLabel = LocalizationService.Text("TraktSyncNow");
        ToolTipService.SetToolTip(SyncNowButton, syncLabel);
        AutomationProperties.SetName(SyncNowButton, syncLabel);

        if (_lastProgress is not null)
        {
            var total = Math.Max(1, _lastProgress.TotalSteps);
            var cur = Math.Clamp(_lastProgress.CurrentStep, 0, total);
            SyncProgressCounter.Text = $"{LocalizationService.Number(cur)}/{LocalizationService.Number(total)}";
        }
        else
        {
            UpdateSyncStatus();
        }
    }

    public void UpdateSyncStatus()
    {
        EnsureTraktSubscription();
        var trakt = AppServices.Trakt;
        if (trakt is null)
        {
            _lastProgress = null;
            Visibility = Visibility.Collapsed;
            return;
        }

        TraktSyncState state;
        try
        {
            state = trakt.GetSyncState();
        }
        catch
        {
            _lastProgress = null;
            Visibility = Visibility.Collapsed;
            return;
        }

        if (!state.AuthState.IsConnected)
        {
            _lastProgress = null;
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        var isSyncing = state.IsSyncing || _isManualSyncing;
        SyncNowButton.IsEnabled = !isSyncing;

        if (isSyncing)
        {
            SyncStatusDot.Visibility = Visibility.Collapsed;

            if (_lastProgress is not null)
            {
                // State 3: Determinate Syncing (Strict mutual exclusivity: StatusText Collapsed)
                var total = Math.Max(1, _lastProgress.TotalSteps);
                var cur = Math.Clamp(_lastProgress.CurrentStep, 0, total);
                SyncProgressBar.Maximum = total;
                SyncProgressBar.Value = cur;
                SyncProgressCounter.Text = $"{LocalizationService.Number(cur)}/{LocalizationService.Number(total)}";
                SyncProgressContainer.Visibility = Visibility.Visible;
                SyncStatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // State 2: Indeterminate Syncing (Strict mutual exclusivity: ProgressContainer Collapsed)
                SyncProgressContainer.Visibility = Visibility.Collapsed;
                SyncStatusText.Visibility = Visibility.Visible;
                SyncStatusText.Text = LocalizationService.Text("TraktStatusSyncing");
            }
        }
        else
        {
            // State 1: Idle (Strict mutual exclusivity: ProgressContainer Collapsed)
            _lastProgress = null;
            SyncProgressBar.Value = 0;
            SyncProgressCounter.Text = string.Empty;
            SyncProgressContainer.Visibility = Visibility.Collapsed;

            SyncStatusDot.Visibility = Visibility.Visible;

            if (state.LastSyncSuccessAt.HasValue)
            {
                var timeStr = LocalizationService.Digits(
                    state.LastSyncSuccessAt.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
                SyncStatusText.Text = $"{LocalizationService.Text("TraktLastSynced")}: {timeStr}";
                SyncStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                SyncStatusText.Text = string.Empty;
                SyncStatusText.Visibility = Visibility.Collapsed; // No phantom spacing
            }
        }
    }

    private void OnSyncProgressChanged(object? sender, TraktSyncProgress progress)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsLoaded || !ReferenceEquals(sender, _subscribedTrakt)) return;
            _lastProgress = progress;
            var totalSteps = Math.Max(1, progress.TotalSteps);
            var currentStep = Math.Clamp(progress.CurrentStep, 0, totalSteps);

            // State 3: Determinate Syncing (Strict mutual exclusivity: StatusText Collapsed)
            SyncStatusDot.Visibility = Visibility.Collapsed;
            SyncStatusText.Visibility = Visibility.Collapsed;
            SyncProgressContainer.Visibility = Visibility.Visible;

            SyncProgressBar.Maximum = totalSteps;
            SyncProgressBar.Value = currentStep;
            SyncProgressCounter.Text = $"{LocalizationService.Number(currentStep)}/{LocalizationService.Number(totalSteps)}";
            SyncNowButton.IsEnabled = false;
        });
    }

    private void OnTraktSyncCompleted(object? sender, EventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            UpdateSyncStatus();
        });
    }

    private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isManualSyncing) return;
        var trakt = AppServices.Trakt;
        if (trakt is null) return;

        var generation = unchecked(++_manualSyncGeneration);
        _isManualSyncing = true;
        _lastProgress = null;

        // State 2: Immediate Indeterminate Syncing
        SyncNowButton.IsEnabled = false;
        SyncStatusDot.Visibility = Visibility.Collapsed;
        SyncProgressContainer.Visibility = Visibility.Collapsed;
        SyncProgressBar.Value = 0;
        SyncProgressCounter.Text = string.Empty;
        SyncStatusText.Text = LocalizationService.Text("TraktStatusSyncing");
        SyncStatusText.Visibility = Visibility.Visible;

        try
        {
            await Task.Run(async () => await trakt.SyncAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TraktSyncCapsule] Manual sync failed: {ex.Message}");
        }
        finally
        {
            if (generation == _manualSyncGeneration)
            {
                _isManualSyncing = false;
            }

            if (DispatcherQueue?.HasThreadAccess == true)
            {
                if (IsLoaded)
                {
                    UpdateSyncStatus();
                }
            }
            else
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (!IsLoaded) return;
                    UpdateSyncStatus();
                });
            }
        }
    }
}
