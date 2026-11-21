using System.Globalization;
using System.IO;
using System.Text;
using TBDMovies.UI.Models;
using TBDMovies.Application.External;
using TBDMovies.Application.Trakt;
using TBDMovies.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace TBDMovies.UI.Views;

public sealed partial class SettingsPage : Page, ILocalizableView, IActivatableView, IDeactivatableView
{
    private bool _editingCredential;
    private bool _credentialBusy;
    private CancellationTokenSource? _credentialRequest;
    private CancellationTokenSource? _traktAuthCts;
    private CancellationTokenSource? _traktActionCts;
    private string? _validatedVerificationUrl;
    private int _traktGeneration;
    private bool _traktBusy;
    private bool _editingTraktConfig;
    private (string ClientId, string RedirectUri)? _lastLoadedTraktConfig;
    private string? _lastExportedFilePath;

    public void Deactivate()
    {
        _credentialRequest?.Cancel();
        _editingCredential = false;
        TokenBox.Password = string.Empty;
        _editingTraktConfig = false;
        _lastLoadedTraktConfig = null;
        TraktClientIdBox.Password = string.Empty;
        TraktClientIdBox.PlaceholderText = string.Empty;
        TraktRedirectUriBox.Text = string.Empty;
        TmdbFeedback.IsOpen = false;
        Interlocked.Increment(ref _traktGeneration);
        _traktAuthCts?.Cancel();
        _traktActionCts?.Cancel();
        _traktAuthCts = null;
        _traktActionCts = null;
        _traktBusy = false;
        TraktDeviceAuthPanel.Visibility = Visibility.Collapsed;
        TraktFeedback.IsOpen = false;
        SubDlApiKeyBox.Password = string.Empty;
        SubDlFeedback.IsOpen = false;
        ExportFeedbackInfoBar.IsOpen = false;
    }

    /// <summary>
    /// True until the page has pushed the stored theme and language into the two selectors.
    /// <para>
    /// A ComboBox raises SelectionChanged for its initial selection while the XAML is still being
    /// parsed - before the constructor body runs - and the first item is Dark / English. Without this
    /// starting true, that initial event would look like a user choice and overwrite the preference
    /// the shell had just restored.
    /// </para>
    /// </summary>
    private bool _isSyncing = true;

    /// <summary>
    /// False until the user has actually opened this page. A ComboBox whose items have not been
    /// realized yet can raise SelectionChanged late - after the sync flag has been cleared - and report
    /// its first item rather than the index that was assigned. Treating that as a user choice is what
    /// silently reset the stored language to English on startup.
    /// </summary>
    private bool _hasBeenShown;

    public SettingsPage()
    {
        this.InitializeComponent();
        ApplyLocalization();
    }

    public event EventHandler? OperationsLogRequested;

    /// <summary>Raised after the catalog is cleared so the cached pages can re-read it.</summary>
    public event EventHandler? LibraryDataChanged;

    public void Activate()
    {
        _hasBeenShown = true;
        SyncSelectors();
        RefreshFolders();
        RefreshTmdbStatus();
        RefreshTraktStatus();
        RefreshSubDlStatus();
    }

    public void ApplyLocalization()
    {
        FlowDirection = LocalizationService.FlowDirection;
        PageTitleText.Text = LocalizationService.Text("Settings");
        LoadVersionInfo();

        AppearanceHeadingText.Text = LocalizationService.Text("Appearance");
        ThemeLabelText.Text = LocalizationService.Text("Theme");
        RefreshThemeHelp();
        ThemeDarkItem.Content = LocalizationService.Text("Dark");
        ThemeLightItem.Content = LocalizationService.Text("Light");
        LanguageLabelText.Text = LocalizationService.Text("Language");
        LanguageHelpText.Text = LocalizationService.Text("LanguageHelp");
        LanguageEnglishItem.Content = LocalizationService.Text("EnglishLanguage");
        LanguageArabicItem.Content = LocalizationService.Text("ArabicLanguage");

        MediaPlayerHeadingText.Text = LocalizationService.Text("MediaPlayer");
        MediaPlayerLabelText.Text = LocalizationService.Text("MediaPlayer");
        MediaPlayerHelpText.Text = LocalizationService.Text("MediaPlayerHelp");
        PlayerDefaultItem.Content = LocalizationService.Text("PlayerSystemDefault");
        PlayerVlcItem.Content = LocalizationService.Text("PlayerVlc");
        PlayerMpcItem.Content = LocalizationService.Text("PlayerMpc");
        PlayerMpvItem.Content = LocalizationService.Text("PlayerMpv");
        PlayerCustomItem.Content = LocalizationService.Text("PlayerCustom");
        CustomPlayerArgsHelpText.Text = LocalizationService.Text("PlayerCustomArgsHelp");

        LibraryHeadingText.Text = LocalizationService.Text("LibrarySection");
        MediaFoldersLabelText.Text = LocalizationService.Text("MediaFolders");
        MediaFoldersHelpText.Text = LocalizationService.Text("MediaFoldersHelp");
        AddMediaFolderButton.Content = LocalizationService.Text("AddFolder");
        ExportMoviesLabelText.Text = LocalizationService.Text("ExportMoviesList");
        ExportMoviesHelpText.Text = LocalizationService.Text("ExportMoviesListHelp");
        ExportMoviesButtonText.Text = LocalizationService.Text("ExportMovies");
        OpenExportedFolderButton.Content = LocalizationService.Text("OpenFolder");
        StorageAdvancedHeadingText.Text = LocalizationService.Text("StorageAdvanced");
        DataFolderLabelText.Text = LocalizationService.Text("LibraryDataFolder");
        DataFolderHelpText.Text = LocalizationService.Text("LibraryDataFolderHelp");
        DataFolderPathText.Text = AppServices.DataFolder;
        OpenDataFolderButton.Content = LocalizationService.Text("OpenFolder");

        TmdbHeadingText.Text = LocalizationService.Text("Tmdb");
        TokenLabelText.Text = LocalizationService.Text("ReadAccessToken");
        TokenHelpText.Text = LocalizationService.Text("ReadAccessTokenHelp");
        TokenBox.PlaceholderText = LocalizationService.Text("TmdbTokenPlaceholder");
        UseTokenButton.Content = LocalizationService.Text("TmdbSaveConnect");
        CancelTokenButton.Content = LocalizationService.Text("Cancel");
        ClearTokenButton.Content = LocalizationService.Text("Remove");
        TestConnectionButton.Content = LocalizationService.Text("TestConnection");
        SetupGuideButton.Content = LocalizationService.Text("SetupGuide");

        TraktHeadingText.Text = LocalizationService.Text("Trakt");
        TraktClientIdLabelText.Text = LocalizationService.Text("TraktClientIdLabel");
        TraktClientIdHelpText.Text = LocalizationService.Text("TraktClientIdHelp");
        TraktRedirectUriLabelText.Text = LocalizationService.Text("TraktRedirectUriLabel");
        TraktRedirectUriHelpText.Text = LocalizationService.Text("TraktRedirectUriHelp");
        TraktSaveConfigButton.Content = LocalizationService.Text("TraktSaveConfig");
        TraktCancelConfigButton.Content = LocalizationService.Text("Cancel");
        TraktEditConfigButton.Content = LocalizationService.Text("TraktEditConfig");
        TraktConnectButton.Content = LocalizationService.Text("TraktConnectDevice");
        TraktSyncNowButton.Content = LocalizationService.Text("TraktSyncNow");
        TraktDisconnectButton.Content = LocalizationService.Text("TraktDisconnect");
        TraktCancelAuthButton.Content = LocalizationService.Text("TraktCancelLogin");
        TraktDeviceAuthHeadingText.Text = LocalizationService.Text("TraktDeviceAuthHeading");
        TraktDeviceAuthInstructionsText.Text = LocalizationService.Text("TraktDeviceAuthInstructions");
        TraktOpenVerificationUrlButton.Content = LocalizationService.Text("TraktOpenVerificationUrl");

        HistoryHeadingText.Text = LocalizationService.Text("HistoryRecovery");
        ViewLogLabelText.Text = LocalizationService.Text("OperationsLog");
        HistoryHelpText.Text = LocalizationService.Text("OperationsLogHelp");
        ViewLogButton.Content = LocalizationService.Text("ViewOperationsLog");

        DatabaseBackupsHeadingText.Text = LocalizationService.Text("DatabaseBackups");
        CreateBackupLabelText.Text = LocalizationService.Text("CreateBackup");
        CreateBackupHelpText.Text = LocalizationService.Text("CreateBackupHelp");
        CreateBackupButtonText.Text = LocalizationService.Text("CreateBackupButton");
        RestoreBackupLabelText.Text = LocalizationService.Text("RestoreBackup");
        RestoreBackupHelpText.Text = LocalizationService.Text("RestoreBackupHelp");
        RestoreBackupButtonText.Text = LocalizationService.Text("RestoreBackupButton");

        DangerHeadingText.Text = LocalizationService.Text("DangerZone");
        ClearHistoryLabelText.Text = LocalizationService.Text("ClearHistory");
        ClearHistoryHelpText.Text = LocalizationService.Text("ClearHistoryHelp");
        ClearHistoryButton.Content = LocalizationService.Text("Clear");
        ClearLibraryLabelText.Text = LocalizationService.Text("ClearLibraryData");
        ClearLibraryHelpText.Text = LocalizationService.Text("ClearLibraryDataHelp");
        ClearLibraryButton.Content = LocalizationService.Text("Clear");

        SubtitlesHeadingText.Text = LocalizationService.Text("Subtitles");
        SubDlApiKeyLabelText.Text = LocalizationService.Text("SubDlApiKey");
        SubDlApiKeyHelpText.Text = LocalizationService.Text("SubDlApiKeyHelp");
        SaveSubDlApiKeyButtonText.Text = LocalizationService.Text("Save");
        RemoveSubDlApiKeyButtonText.Text = LocalizationService.Text("Remove");
        GetSubDlKeyButtonText.Text = LocalizationService.Text("GetSubDlApiKey");

        SyncSelectors();
        RefreshFolders();
        RefreshTmdbStatus();
        RefreshTraktStatus();
        RefreshSubDlStatus();
    }

    private void LoadVersionInfo()
    {
        AppVersionText.Text = "TBD Movies " + AppServices.Version;
    }

    private void SyncSelectors()
    {
        _isSyncing = true;
        ThemeSelector.SelectedIndex = ThemeService.CurrentTheme switch
        {
            AppTheme.Light => 1,
            _ => 0,
        };
        LanguageSelector.SelectedIndex = LocalizationService.CurrentLanguage == UiLanguage.Arabic ? 1 : 0;
        if (AppServices.IsAvailable)
        {
            var playerType = AppServices.PlaybackTracking.ResolvePlayerType();
            PlayerSelector.SelectedIndex = playerType switch
            {
                TBDMovies.Domain.Playback.MediaPlayerType.Vlc => 1,
                TBDMovies.Domain.Playback.MediaPlayerType.Mpc => 2,
                TBDMovies.Domain.Playback.MediaPlayerType.Mpv => 3,
                TBDMovies.Domain.Playback.MediaPlayerType.Custom => 4,
                _ => 0
            };
            CustomPlayerPanel.Visibility = playerType == TBDMovies.Domain.Playback.MediaPlayerType.Custom ? Visibility.Visible : Visibility.Collapsed;
            CustomPlayerPathBox.Text = AppServices.SettingsStore.Get("playback_player_custom_path") ?? string.Empty;
            CustomPlayerArgsBox.Text = AppServices.SettingsStore.Get("playback_player_custom_args") ?? string.Empty;
        }
        _isSyncing = false;
    }

    /// <summary>
    /// Show credential state without copying a saved secret into any UI control.
    /// </summary>
    private void RefreshTmdbStatus()
    {
        var state = ReadTmdbState();
        var configured = state is TmdbCredentialState.Configured or TmdbCredentialState.AuthenticationFailed;
        TmdbStatusText.Text = LocalizationService.Text(state switch
        {
            TmdbCredentialState.Configured => "TmdbConnected",
            TmdbCredentialState.AuthenticationFailed => "TmdbAuthFailed",
            TmdbCredentialState.StorageUnavailable => "TmdbStorageUnavailable",
            _ => "NotConfigured",
        });
        TmdbStatusHelpText.Text = LocalizationService.Text(state switch
        {
            TmdbCredentialState.Configured => "TmdbSavedHelp",
            TmdbCredentialState.AuthenticationFailed => "TmdbAuthFailedHelp",
            TmdbCredentialState.StorageUnavailable => "TmdbStorageFailureHelp",
            _ => "TmdbNotConfiguredHelp",
        });
        TmdbConnectedDot.Visibility = state == TmdbCredentialState.Configured ? Visibility.Visible : Visibility.Collapsed;
        TmdbMissingDot.Visibility = state == TmdbCredentialState.Configured ? Visibility.Collapsed : Visibility.Visible;
        ChangeTokenButton.Content = LocalizationService.Text(state == TmdbCredentialState.AuthenticationFailed
            ? "TmdbUpdateCredential" : configured ? "TmdbChangeCredential" : "TmdbAddCredential");
        ChangeTokenButton.Visibility = _editingCredential ? Visibility.Collapsed : Visibility.Visible;
        TokenInputPanel.Visibility = _editingCredential ? Visibility.Visible : Visibility.Collapsed;
        UseTokenButton.Visibility = _editingCredential ? Visibility.Visible : Visibility.Collapsed;
        CancelTokenButton.Visibility = _editingCredential ? Visibility.Visible : Visibility.Collapsed;
        ClearTokenButton.Visibility = configured || state == TmdbCredentialState.StorageUnavailable ? Visibility.Visible : Visibility.Collapsed;
        TestConnectionButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        ChangeTokenButton.IsEnabled = !_credentialBusy;
        UseTokenButton.IsEnabled = !_credentialBusy;
        CancelTokenButton.IsEnabled = !_credentialBusy;
        ClearTokenButton.IsEnabled = !_credentialBusy;
        TestConnectionButton.IsEnabled = configured && !_credentialBusy;
        TokenBox.IsEnabled = !_credentialBusy;
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncing || !_hasBeenShown)
        {
            return;
        }

        ThemeService.SetTheme(ThemeSelector.SelectedIndex switch
        {
            1 => AppTheme.Light,
            _ => AppTheme.Dark,
        });
        RefreshThemeHelp();
    }

    private void RefreshThemeHelp()
    {
        ThemeHelpText.Text = LocalizationService.Text(ThemeService.CurrentTheme switch
        {
            AppTheme.Light => "ThemeHelpLight",
            _ => "ThemeHelpDark",
        });
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncing || !_hasBeenShown)
        {
            return;
        }

        LocalizationService.SetLanguage(
            LanguageSelector.SelectedIndex == 1 ? UiLanguage.Arabic : UiLanguage.English);
    }

    private void PlayerSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncing || !_hasBeenShown || !AppServices.IsAvailable) return;
        var type = PlayerSelector.SelectedIndex switch
        {
            1 => TBDMovies.Domain.Playback.MediaPlayerType.Vlc,
            2 => TBDMovies.Domain.Playback.MediaPlayerType.Mpc,
            3 => TBDMovies.Domain.Playback.MediaPlayerType.Mpv,
            4 => TBDMovies.Domain.Playback.MediaPlayerType.Custom,
            _ => TBDMovies.Domain.Playback.MediaPlayerType.Default
        };
        AppServices.PlaybackTracking.SetPlayerType(type);
        CustomPlayerPanel.Visibility = type == TBDMovies.Domain.Playback.MediaPlayerType.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CustomPlayerPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing || !AppServices.IsAvailable) return;
        AppServices.PlaybackTracking.SetCustomPlayerConfig(CustomPlayerPathBox.Text, CustomPlayerArgsBox.Text);
    }

    private void CustomPlayerArgsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing || !AppServices.IsAvailable) return;
        AppServices.PlaybackTracking.SetCustomPlayerConfig(CustomPlayerPathBox.Text, CustomPlayerArgsBox.Text);
    }

    private async void BrowseCustomPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            CustomPlayerPathBox.Text = file.Path;
        }
    }

    /// <summary>
    /// Picks a media root without asking the user to classify it as movie or TV content.
    /// </summary>
    /// <summary>Add another media root. Adding never replaces or removes an existing root.</summary>
    private async void AddMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await PickFolderAsync() is not { } path)
            {
                return;
            }

            if (!AppSettingsStore.AddMediaFolder(path))
            {
                await ShowMessageAsync(LocalizationService.Text("MediaFolders"), LocalizationService.Text("FolderAlreadyAdded"));
            }

            RefreshFolders();
        }
        catch (Exception error)
        {
            await ShowMessageAsync(LocalizationService.Text("Failed"), MetadataErrorText.For(error));
        }
    }

    /// <summary>Replace the selected root with a newly picked one, in place. Never treated as an add.</summary>
    private async void ReplaceMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string oldPath })
        {
            return;
        }

        try
        {
            if (await PickFolderAsync() is not { } newPath)
            {
                return;
            }

            if (!AppSettingsStore.ReplaceMediaFolder(oldPath, newPath))
            {
                await ShowMessageAsync(LocalizationService.Text("MediaFolders"), LocalizationService.Text("FolderAlreadyAdded"));
            }

            RefreshFolders();
        }
        catch (Exception error)
        {
            await ShowMessageAsync(LocalizationService.Text("Failed"), MetadataErrorText.For(error));
        }
    }

    /// <summary>
    /// Remove the selected root from the configuration. This only edits settings; the media files on that
    /// folder are never moved or deleted, and library/watch/list state is untouched.
    /// </summary>
    private async void RemoveMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }

        var confirmed = await ConfirmAsync(
            LocalizationService.Text("RemoveFolder"),
            LocalizationService.Format("RemoveFolderConfirm", path));
        if (!confirmed)
        {
            return;
        }

        try
        {
            AppSettingsStore.RemoveMediaFolder(path);
            RefreshFolders();
        }
        catch (Exception error)
        {
            await ShowMessageAsync(LocalizationService.Text("Failed"), MetadataErrorText.For(error));
        }
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>Shows the configured roots (each with Replace / Remove), or the empty help when none is set.</summary>
    private void RefreshFolders()
    {
        var folders = AppSettingsStore.ConfiguredMediaFolders;
        MediaFoldersRepeater.ItemsSource = folders.Select(path => new MediaFolderRow(path)).ToList();
        MediaFoldersEmptyText.Text = LocalizationService.Text("MediaFoldersNone");
        MediaFoldersEmptyText.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ExportMoviesButton_Click(object sender, RoutedEventArgs e)
    {
        var movies = AppServices.Library.ListMovies();
        var shows = AppServices.Tv.ListShows();
        if (movies.Count == 0 && shows.Count == 0)
        {
            ExportFeedbackInfoBar.Severity = InfoBarSeverity.Informational;
            ExportFeedbackInfoBar.Message = LocalizationService.Text("ExportMoviesEmpty");
            OpenExportedFolderButton.Visibility = Visibility.Collapsed;
            ExportFeedbackInfoBar.IsOpen = true;
            return;
        }

        var savePicker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, MainWindow.WindowHandle);
        savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        savePicker.SuggestedFileName = "library_export.txt";
        savePicker.FileTypeChoices.Add("Text Document", new List<string> { ".txt" });

        var file = await savePicker.PickSaveFileAsync();
        if (file is null) return;

        var orderedMovies = movies
            .OrderBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var orderedShows = shows
            .OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("TBD Movies - Library Export");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Total Movies: {orderedMovies.Count} | Total TV Shows: {orderedShows.Count}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        sb.AppendLine("--- SECTION 1: MOVIES ---");
        sb.AppendLine();
        if (orderedMovies.Count == 0)
        {
            sb.AppendLine("(No movies in library)");
        }
        else
        {
            for (int i = 0; i < orderedMovies.Count; i++)
            {
                var movie = orderedMovies[i];
                var yearStr = movie.Year is > 0 ? movie.Year.ToString() : "N/A";
                var tmdbId = string.IsNullOrWhiteSpace(movie.ExternalId) ? "N/A" : movie.ExternalId;
                var watched = movie.Watched ? "Yes" : "No";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. {movie.Title} ({yearStr}) | TMDB ID: {tmdbId} | Watched: {watched}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("--- SECTION 2: TV SHOWS ---");
        sb.AppendLine();
        if (orderedShows.Count == 0)
        {
            sb.AppendLine("(No TV shows in library)");
        }
        else
        {
            for (int i = 0; i < orderedShows.Count; i++)
            {
                var show = orderedShows[i];
                var yearStr = show.Year is > 0 ? show.Year.ToString() : "N/A";
                string tmdbId = "N/A";
                try
                {
                    var details = AppServices.Tv.GetShowDetails(show.Id);
                    if (!string.IsNullOrWhiteSpace(details?.ExternalId))
                    {
                        tmdbId = details.ExternalId;
                    }
                }
                catch
                {
                    // Fallback if details lookup fails
                }
                var seasonText = $"{show.SeasonCount} {(show.SeasonCount == 1 ? "Season" : "Seasons")}";
                var episodeText = $"{show.EpisodeCount} {(show.EpisodeCount == 1 ? "Episode" : "Episodes")}";
                var watchedProgress = $"{show.WatchedEpisodeCount}/{show.EpisodeCount} watched";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. {show.Title} ({yearStr}) | TMDB ID: {tmdbId} | {seasonText}, {episodeText} | Watched: {watchedProgress}");
            }
        }

        await File.WriteAllTextAsync(file.Path, sb.ToString(), Encoding.UTF8);

        _lastExportedFilePath = file.Path;
        var totalCount = orderedMovies.Count + orderedShows.Count;
        ExportFeedbackInfoBar.Severity = InfoBarSeverity.Success;
        ExportFeedbackInfoBar.Message = string.Format(
            LocalizationService.Culture,
            LocalizationService.Text("ExportMoviesSuccessFormat"),
            LocalizationService.Digits(totalCount.ToString(LocalizationService.Culture)),
            file.Name);
        OpenExportedFolderButton.Visibility = Visibility.Visible;
        ExportFeedbackInfoBar.IsOpen = true;
    }

    private async void OpenExportedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastExportedFilePath))
        {
            var dir = Path.GetDirectoryName(_lastExportedFilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                await Launcher.LaunchFolderPathAsync(dir);
            }
        }
    }

    /// <summary>Opening the data folder is fully implemented through the shell launcher.</summary>
    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(AppServices.DataFolder))
        {
            _ = Launcher.LaunchFolderPathAsync(AppServices.DataFolder);
        }
    }

    /// <summary>
    /// Validate a new value before securely replacing the saved credential.
    /// </summary>
    private async void UseTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_credentialBusy) return;
        _credentialBusy = true;
        _credentialRequest = new CancellationTokenSource();
        RefreshTmdbStatus();
        try
        {
            var credential = TokenBox.Password;
            TokenBox.Password = string.Empty;
            var result = await AppServices.Settings.SaveTmdbCredentialAsync(
                credential, AppServices.TmdbClient, _credentialRequest.Token);
            if (result.Success)
            {
                _editingCredential = false;
                TmdbFeedback.IsOpen = false;
            }
            else ShowCredentialFeedback(CredentialFailureKey(result.Failure));
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            ShowCredentialFeedback("TmdbCredentialStorageError");
        }
        finally
        {
            TokenBox.Password = string.Empty;
            _credentialRequest.Dispose();
            _credentialRequest = null;
            _credentialBusy = false;
            RefreshTmdbStatus();
        }
    }

    private void ChangeTokenButton_Click(object sender, RoutedEventArgs e)
    {
        TokenBox.Password = string.Empty;
        _editingCredential = true;
        TmdbFeedback.IsOpen = false;
        RefreshTmdbStatus();
        TokenBox.Focus(FocusState.Programmatic);
    }

    private void CancelTokenButton_Click(object sender, RoutedEventArgs e)
    {
        TokenBox.Password = string.Empty;
        _editingCredential = false;
        TmdbFeedback.IsOpen = false;
        RefreshTmdbStatus();
    }

    private static TmdbCredentialState ReadTmdbState()
    {
        try
        {
            return AppServices.IsAvailable ? AppServices.Settings.GetTmdbCredentialState() : TmdbCredentialState.StorageUnavailable;
        }
        catch (Exception)
        {
            return TmdbCredentialState.StorageUnavailable;
        }
    }

    private async void ClearTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_credentialBusy || !await ConfirmAsync("TmdbRemoveCredential", "TmdbRemoveConfirm")) return;
        if (_credentialBusy) return;
        _credentialBusy = true;
        RefreshTmdbStatus();
        try
        {
            if (await AppServices.Settings.RemoveTmdbCredentialAsync())
            {
                TokenBox.Password = string.Empty;
                _editingCredential = false;
                TmdbFeedback.IsOpen = false;
            }
            else ShowCredentialFeedback("TmdbCredentialStorageError");
        }
        catch (Exception)
        {
            ShowCredentialFeedback("TmdbCredentialStorageError");
        }
        finally { _credentialBusy = false; RefreshTmdbStatus(); }
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_credentialBusy) return;
        _credentialBusy = true;
        _credentialRequest = new CancellationTokenSource();
        RefreshTmdbStatus();
        try
        {
            var result = await AppServices.Settings.TestTmdbConnectionAsync(AppServices.Metadata, _credentialRequest.Token);
            var message = MetadataErrorText.For(result);
            await ShowMessageAsync(LocalizationService.Text("TestConnection"), message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await ShowMessageAsync(LocalizationService.Text("TestConnection"), MetadataErrorText.For(ex));
        }
        finally
        {
            _credentialRequest.Dispose();
            _credentialRequest = null;
            _credentialBusy = false;
            RefreshTmdbStatus();
        }
    }

    private void ShowCredentialFeedback(string key)
    {
        TmdbFeedback.Message = LocalizationService.Text(key);
        TmdbFeedback.IsOpen = _hasBeenShown && Visibility == Visibility.Visible;
    }

    private static string CredentialFailureKey(TmdbCredentialFailure failure) => failure switch
    {
        TmdbCredentialFailure.Empty => "TmdbCredentialEmpty",
        TmdbCredentialFailure.Authentication => "TmdbNewCredentialRejected",
        TmdbCredentialFailure.Network => "TmdbCredentialNetworkError",
        TmdbCredentialFailure.Timeout => "TmdbCredentialNetworkError",
        TmdbCredentialFailure.Storage => "TmdbCredentialStorageError",
        _ => "TmdbCredentialServiceError",
    };

    private void SetupGuideButton_Click(object sender, RoutedEventArgs e) =>
        _ = Launcher.LaunchUriAsync(new Uri("https://developer.themoviedb.org/docs/authentication-application"));

    private void ViewLogButton_Click(object sender, RoutedEventArgs e) =>
        OperationsLogRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Clears the entire watch history - movie, episode and events orphaned by deleted media - in one
    /// pass. Preferences and the watchlist are untouched, which is what the row promises.
    /// </summary>
    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("ClearHistory", "ClearHistoryHelp"))
        {
            return;
        }

        int removed;

        try
        {
            removed = await Task.Run(() => AppServices.WatchHistory.ClearWatchHistory());
        }
        catch (Exception error)
        {
            await ShowMessageAsync(LocalizationService.Text("Failed"), MetadataErrorText.For(error));
            return;
        }

        LibraryDataChanged?.Invoke(this, EventArgs.Empty);
        await ShowMessageAsync(
            LocalizationService.Text("ClearHistory"),
            LocalizationService.Format("WatchEventsRemovedFormat", removed));
    }

    /// <summary>
    /// Clears the local index only. The catalog rows go away; not one media file on disk is touched -
    /// that is the product contract and the service enforces it.
    /// </summary>
    private async void ClearLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("ClearLibraryData", "ClearLibraryDataHelp"))
        {
            return;
        }

        try
        {
            var result = AppServices.Settings.ClearLibraryData();
            LibraryDataChanged?.Invoke(this, EventArgs.Empty);
            var message = LocalizationService.Format("LibraryClearedFormat", result.MoviesRemoved, result.MediaFilesRemoved);
            if (string.Equals(result.Warning, "POSTER_CACHE_CLEANUP_FAILED", StringComparison.Ordinal))
            {
                message += Environment.NewLine + Environment.NewLine + LocalizationService.Text("PosterCacheCleanupFailed");
            }
            await ShowMessageAsync(LocalizationService.Text("ClearLibraryData"), message);
        }
        catch (Exception error)
        {
            await ShowMessageAsync(LocalizationService.Text("Failed"), MetadataErrorText.For(error));
        }
    }

    private async void CreateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        CreateBackupButton.IsEnabled = false;
        try
        {
            var backupPath = await Task.Run(() => AppServices.CreateDatabaseBackup());
            var fileName = Path.GetFileName(backupPath);
            await ShowMessageAsync(
                LocalizationService.Text("CreateBackup"),
                LocalizationService.Format("BackupCreatedFormat", fileName));
        }
        catch (Exception error)
        {
            await ShowMessageAsync(LocalizationService.Text("Failed"), MetadataErrorText.For(error));
        }
        finally
        {
            CreateBackupButton.IsEnabled = true;
        }
    }

    private void AvailableBackupsFlyout_Opening(object sender, object e)
    {
        AvailableBackupsFlyout.Items.Clear();

        IReadOnlyList<TBDMovies.Infrastructure.Persistence.DatabaseBackupSummary> backups;
        try
        {
            backups = AppServices.ListDatabaseBackups();
        }
        catch
        {
            backups = Array.Empty<TBDMovies.Infrastructure.Persistence.DatabaseBackupSummary>();
        }

        if (backups.Count > 0)
        {
            foreach (var backup in backups)
            {
                var localTime = backup.CreatedAt.ToLocalTime();
                var sizeMb = (backup.SizeBytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);
                var label = $"{backup.FileName} ({LocalizationService.Digits(localTime.ToString("yyyy-MM-dd HH:mm", LocalizationService.Culture))}, {sizeMb} MB)";
                var item = new MenuFlyoutItem
                {
                    Text = label,
                    Icon = new FontIcon { Glyph = "\uE74E" }
                };
                var path = backup.FilePath;
                item.Click += async (_, _) => await ExecuteRestoreAsync(path);
                AvailableBackupsFlyout.Items.Add(item);
            }

            AvailableBackupsFlyout.Items.Add(new MenuFlyoutSeparator());
        }
        else
        {
            var emptyItem = new MenuFlyoutItem
            {
                Text = LocalizationService.Text("NoBackupsFound"),
                IsEnabled = false
            };
            AvailableBackupsFlyout.Items.Add(emptyItem);
            AvailableBackupsFlyout.Items.Add(new MenuFlyoutSeparator());
        }

        var browseItem = new MenuFlyoutItem
        {
            Text = LocalizationService.Text("BrowseBackupFile"),
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        browseItem.Click += async (_, _) => await BrowseAndRestoreAsync();
        AvailableBackupsFlyout.Items.Add(browseItem);
    }

    private async Task BrowseAndRestoreAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".db");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ExecuteRestoreAsync(file.Path);
        }
    }

    private async Task ExecuteRestoreAsync(string backupPath)
    {
        if (!TBDMovies.Infrastructure.Persistence.DatabaseSafetyBackup.IsRestorableDatabase(backupPath, out _))
        {
            await ShowMessageAsync(
                LocalizationService.Text("Failed"),
                LocalizationService.Text("InvalidBackupFile"));
            return;
        }

        if (!await ConfirmAsync("RestoreBackupConfirmTitle", "RestoreBackupConfirmHelp"))
        {
            return;
        }

        RestoreBackupButton.IsEnabled = false;
        try
        {
            await Task.Run(() => AppServices.RestoreDatabaseBackup(backupPath));
            LibraryDataChanged?.Invoke(this, EventArgs.Empty);
            RefreshFolders();
            RefreshTmdbStatus();
            RefreshTraktStatus();
            RefreshSubDlStatus();
            await ShowMessageAsync(
                LocalizationService.Text("RestoreBackup"),
                LocalizationService.Text("BackupRestoreSuccess"));
        }
        catch (Exception error)
        {
            await ShowMessageAsync(LocalizationService.Text("Failed"), MetadataErrorText.For(error));
        }
        finally
        {
            RestoreBackupButton.IsEnabled = true;
        }
    }

    /// <summary>The destructive confirmations are real, native and correctly themed.</summary>
    private async Task<bool> ConfirmAsync(string titleKey, string messageKey)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            FlowDirection = LocalizationService.FlowDirection,
            RequestedTheme = ThemeService.ElementTheme,
            Title = LocalizationService.Text(titleKey),
            Content = LocalizationService.Text(messageKey),
            PrimaryButtonText = LocalizationService.Text("Confirm"),
            CloseButtonText = LocalizationService.Text("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            FlowDirection = LocalizationService.FlowDirection,
            RequestedTheme = ThemeService.ElementTheme,
            Title = title,
            Content = message,
            CloseButtonText = LocalizationService.Text("Close"),
        };

        await ContentDialogCoordinator.ShowAsync(dialog);
    }

    #region Trakt Integration Controls

    private async void RefreshTraktStatus()
    {
        var generation = _traktGeneration;

        try
        {
            var (config, state, isConfigured) = await Task.Run(() =>
            {
                var cfg = AppServices.TraktConfig;
                var trakt = AppServices.Trakt;
                var st = trakt?.GetSyncState();
                return (cfg, st, trakt is not null);
            });

            if (generation != _traktGeneration) return;
            _lastLoadedTraktConfig = config;

            var isOfficial = string.Equals(config.ClientId, TraktDefaults.OfficialClientId, StringComparison.Ordinal);
            var hasSavedConfig = !string.IsNullOrWhiteSpace(config.ClientId) && !string.IsNullOrWhiteSpace(config.RedirectUri);
            TraktConfigSummaryPanel.Visibility = hasSavedConfig && !_editingTraktConfig ? Visibility.Visible : Visibility.Collapsed;
            TraktConfigInputPanel.Visibility = hasSavedConfig && !_editingTraktConfig ? Visibility.Collapsed : Visibility.Visible;
            TraktEditConfigButton.Visibility = hasSavedConfig && !_editingTraktConfig ? Visibility.Visible : Visibility.Collapsed;
            TraktCancelConfigButton.Visibility = hasSavedConfig && _editingTraktConfig ? Visibility.Visible : Visibility.Collapsed;
            TraktConfigSummaryText.Text = isOfficial
                ? "Official Trakt App (Active · Zero-Config)"
                : (hasSavedConfig ? LocalizationService.Text("TraktConfigConfigured") : string.Empty);
            TraktClientIdMaskedText.Text = hasSavedConfig
                ? (isOfficial ? "Official Client ID Active" : string.Format(LocalizationService.Text("TraktClientIdMaskedFormat"), MaskTraktClientId(config.ClientId)))
                : string.Empty;
            if (hasSavedConfig && !_editingTraktConfig)
            {
                // Do not retain saved values in input controls after returning to the ordinary view.
                TraktClientIdBox.Password = string.Empty;
                TraktClientIdBox.PlaceholderText = string.Empty;
                TraktRedirectUriBox.Text = string.Empty;
            }

            if (!isConfigured || state is null)
            {
                TraktConnectedDot.Visibility = Visibility.Collapsed;
                TraktSyncingDot.Visibility = Visibility.Collapsed;
                TraktDisconnectedDot.Visibility = Visibility.Visible;
                TraktStatusText.Text = LocalizationService.Text("TraktStatusNotConfigured");
                TraktAccountInfoText.Text = LocalizationService.Text("TraktNotConfiguredPrompt");
                TraktSyncInfoText.Text = string.Empty;
                TraktErrorText.Visibility = Visibility.Collapsed;

                TraktSaveConfigButton.IsEnabled = !_traktBusy;
                TraktConnectButton.IsEnabled = !_traktBusy && !string.IsNullOrWhiteSpace(config.ClientId) && !string.IsNullOrWhiteSpace(config.RedirectUri);
                TraktConnectButton.Visibility = Visibility.Visible;
                TraktSyncNowButton.Visibility = Visibility.Collapsed;
                TraktDisconnectButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (state.IsSyncing)
            {
                TraktConnectedDot.Visibility = Visibility.Collapsed;
                TraktSyncingDot.Visibility = Visibility.Visible;
                TraktDisconnectedDot.Visibility = Visibility.Collapsed;
                TraktStatusText.Text = LocalizationService.Text("TraktStatusSyncing");
                TraktConnectButton.Visibility = Visibility.Collapsed;
                TraktSyncNowButton.IsEnabled = false;
                TraktDisconnectButton.IsEnabled = false;
            }
            else if (state.AuthState.IsConnected)
            {
                TraktConnectedDot.Visibility = Visibility.Visible;
                TraktSyncingDot.Visibility = Visibility.Collapsed;
                TraktDisconnectedDot.Visibility = Visibility.Collapsed;
                TraktStatusText.Text = LocalizationService.Text("TraktStatusConnected");
                TraktAccountInfoText.Text = string.Format(LocalizationService.Text("TraktConnectedAccountFormat"), state.CurrentAccountId ?? "Trakt User");
                TraktConnectButton.Visibility = Visibility.Collapsed;
                TraktSyncNowButton.Visibility = Visibility.Visible;
                TraktSyncNowButton.IsEnabled = !_traktBusy;
                TraktDisconnectButton.Visibility = Visibility.Visible;
                TraktDisconnectButton.IsEnabled = !_traktBusy;
            }
            else if (!string.IsNullOrWhiteSpace(state.LastAccountId))
            {
                TraktConnectedDot.Visibility = Visibility.Collapsed;
                TraktSyncingDot.Visibility = Visibility.Collapsed;
                TraktDisconnectedDot.Visibility = Visibility.Visible;
                TraktStatusText.Text = LocalizationService.Text("TraktStatusOfflineCache");
                TraktAccountInfoText.Text = string.Format(LocalizationService.Text("TraktOfflineAccountFormat"), state.LastAccountId);
                TraktConnectButton.Visibility = Visibility.Visible;
                TraktConnectButton.IsEnabled = !_traktBusy;
                TraktSyncNowButton.Visibility = Visibility.Collapsed;
                TraktDisconnectButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                TraktConnectedDot.Visibility = Visibility.Collapsed;
                TraktSyncingDot.Visibility = Visibility.Collapsed;
                TraktDisconnectedDot.Visibility = Visibility.Visible;
                TraktStatusText.Text = LocalizationService.Text("TraktStatusDisconnected");
                TraktAccountInfoText.Text = string.Empty;
                TraktConnectButton.Visibility = Visibility.Visible;
                TraktConnectButton.IsEnabled = !_traktBusy;
                TraktSyncNowButton.Visibility = Visibility.Collapsed;
                TraktDisconnectButton.Visibility = Visibility.Collapsed;
            }

            if (state.LastSyncSuccessAt.HasValue)
            {
                TraktSyncInfoText.Text = string.Format(LocalizationService.Text("TraktLastSyncFormat"), state.LastSyncSuccessAt.Value.ToLocalTime().ToString("g"));
            }
            else
            {
                TraktSyncInfoText.Text = LocalizationService.Text("TraktNeverSynced");
            }

            if (!string.IsNullOrWhiteSpace(state.LastSyncError))
            {
                TraktErrorText.Text = string.Format(LocalizationService.Text("TraktLastSyncErrorFormat"), state.LastSyncError);
                TraktErrorText.Visibility = Visibility.Visible;
            }
            else
            {
                TraktErrorText.Visibility = Visibility.Collapsed;
            }

            TraktSaveConfigButton.IsEnabled = !_traktBusy;
        }
        catch (Exception)
        {
            if (generation != _traktGeneration) return;
            TraktConnectedDot.Visibility = Visibility.Collapsed;
            TraktSyncingDot.Visibility = Visibility.Collapsed;
            TraktDisconnectedDot.Visibility = Visibility.Visible;
            TraktSaveConfigButton.IsEnabled = !_traktBusy;
            TraktStatusText.Text = LocalizationService.Text("TraktStatusReadError");
        }
    }

    private async void TraktSaveConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (_traktBusy) return;

        var typedClientId = TraktClientIdBox.Password?.Trim() ?? string.Empty;
        TraktClientIdBox.Password = string.Empty;
        var isEditingExisting = _editingTraktConfig;
        var redirectUri = TraktRedirectUriBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(typedClientId) && !isEditingExisting)
        {
            var text = LocalizationService.Text("TraktClientIdRequired");
            ShowTraktFeedback(text == "TraktClientIdRequired" ? "Client ID cannot be empty." : text, InfoBarSeverity.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            var text = LocalizationService.Text("TraktRedirectUriInvalid");
            ShowTraktFeedback(text == "TraktRedirectUriInvalid" ? "A valid HTTPS redirect URI is required (e.g. https://localhost)." : text, InfoBarSeverity.Error);
            return;
        }

        _traktBusy = true;
        TraktSaveConfigButton.IsEnabled = false;
        var generation = Interlocked.Increment(ref _traktGeneration);
        var cts = new CancellationTokenSource();
        _traktActionCts = cts;

        try
        {
            var missingClientId = await Task.Run(async () =>
            {
                var effectiveClientId = string.IsNullOrWhiteSpace(typedClientId) && isEditingExisting
                    ? AppServices.TraktConfig.ClientId
                    : typedClientId;
                if (string.IsNullOrWhiteSpace(effectiveClientId))
                {
                    return true;
                }

                await AppServices.ConfigureTraktAsync(effectiveClientId, redirectUri, cts.Token).ConfigureAwait(false);
                return false;
            }, cts.Token);

            if (generation == _traktGeneration)
            {
                if (missingClientId)
                {
                    var text = LocalizationService.Text("TraktClientIdRequired");
                    ShowTraktFeedback(text == "TraktClientIdRequired" ? "Client ID cannot be empty." : text, InfoBarSeverity.Error);
                }
                else
                {
                    _editingTraktConfig = false;
                    TraktClientIdBox.PlaceholderText = string.Empty;
                    ShowTraktFeedback(LocalizationService.Text("TraktConfigSaved"), InfoBarSeverity.Success);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _traktGeneration)
            {
                ShowTraktFeedback(MetadataErrorText.For(ex), InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_traktActionCts, cts))
            {
                _traktActionCts = null;
            }
            cts.Dispose();
            if (generation == _traktGeneration)
            {
                _traktBusy = false;
                TraktSaveConfigButton.IsEnabled = true;
                RefreshTraktStatus();
            }
        }
    }

    private async void TraktEditConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (_traktBusy) return;

        var generation = _traktGeneration;
        var config = _lastLoadedTraktConfig ?? await Task.Run(() => AppServices.TraktConfig);
        if (generation != _traktGeneration)
        {
            return;
        }

        _lastLoadedTraktConfig = config;
        if (string.IsNullOrWhiteSpace(config.ClientId) || string.IsNullOrWhiteSpace(config.RedirectUri))
        {
            return;
        }

        _editingTraktConfig = true;
        TraktClientIdBox.Password = string.Empty;
        TraktClientIdBox.PlaceholderText = MaskTraktClientId(config.ClientId);
        TraktRedirectUriBox.Text = config.RedirectUri;
        TraktConfigSummaryPanel.Visibility = Visibility.Collapsed;
        TraktEditConfigButton.Visibility = Visibility.Collapsed;
        TraktConfigInputPanel.Visibility = Visibility.Visible;
        TraktCancelConfigButton.Visibility = Visibility.Visible;
        TraktClientIdBox.Focus(FocusState.Programmatic);
        RefreshTraktStatus();
    }

    private void TraktCancelConfigButton_Click(object sender, RoutedEventArgs e)
    {
        _editingTraktConfig = false;
        TraktClientIdBox.Password = string.Empty;
        TraktClientIdBox.PlaceholderText = string.Empty;
        TraktRedirectUriBox.Text = string.Empty;
        RefreshTraktStatus();
    }

    private static string MaskTraktClientId(string clientId)
    {
        var value = clientId.Trim();
        if (value.Length < 12)
        {
            return new string('•', Math.Max(8, value.Length));
        }

        return new string('•', value.Length - 4) + value[^4..];
    }

    private async void TraktConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_traktBusy) return;

        var trakt = AppServices.Trakt;
        if (trakt is null)
        {
            ShowTraktFeedback(LocalizationService.Text("TraktNotConfiguredPrompt"), InfoBarSeverity.Warning);
            return;
        }

        _traktBusy = true;
        var generation = Interlocked.Increment(ref _traktGeneration);

        _traktAuthCts?.Cancel();
        var cts = new CancellationTokenSource();
        _traktAuthCts = cts;

        TraktConnectButton.IsEnabled = false;
        TraktDeviceAuthPanel.Visibility = Visibility.Visible;
        TraktFeedback.IsOpen = false;

        try
        {
            var deviceCode = await Task.Run(async () =>
            {
                return await trakt.BeginAuthAsync(cts.Token).ConfigureAwait(false);
            }, cts.Token);

            if (generation != _traktGeneration) return;

            TraktUserCodeText.Text = deviceCode.UserCode;

            // Validate HTTPS Trakt host before user clicks link; no automatic browser launch during generation.
            _validatedVerificationUrl = ValidateTraktVerificationUrl(deviceCode.VerificationUrl);
            TraktOpenVerificationUrlButton.IsEnabled = _validatedVerificationUrl is not null;

            var authState = await Task.Run(async () =>
            {
                return await trakt.CompleteAuthAsync(deviceCode, cts.Token).ConfigureAwait(false);
            }, cts.Token);

            if (generation != _traktGeneration) return;

            if (authState.IsConnected)
            {
                TraktDeviceAuthPanel.Visibility = Visibility.Collapsed;
                ShowTraktFeedback(LocalizationService.Text("TraktAuthSuccess"), InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _traktGeneration)
            {
                TraktDeviceAuthPanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            if (generation == _traktGeneration)
            {
                TraktDeviceAuthPanel.Visibility = Visibility.Collapsed;
                ShowTraktFeedback(ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_traktAuthCts, cts))
            {
                _traktAuthCts = null;
            }
            cts.Dispose();
            if (generation == _traktGeneration)
            {
                _traktBusy = false;
                TraktConnectButton.IsEnabled = true;
                RefreshTraktStatus();
            }
        }
    }

    private async void TraktOpenVerificationUrlButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_validatedVerificationUrl is not null &&
                Uri.TryCreate(_validatedVerificationUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                IsTraktHost(uri.Host))
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
        }
        catch (Exception ex)
        {
            var msg = LocalizationService.Text("TraktLaunchUrlError");
            ShowTraktFeedback(msg == "TraktLaunchUrlError" ? ex.Message : msg, InfoBarSeverity.Error);
        }
    }

    private void TraktCancelAuthButton_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _traktGeneration);
        _traktAuthCts?.Cancel();
        _traktAuthCts = null;
        _traktBusy = false;
        TraktDeviceAuthPanel.Visibility = Visibility.Collapsed;
        TraktConnectButton.IsEnabled = true;
        TraktSaveConfigButton.IsEnabled = true;
        RefreshTraktStatus();
    }

    private async void TraktSyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_traktBusy) return;

        var trakt = AppServices.Trakt;
        if (trakt is null) return;

        _traktBusy = true;
        var generation = Interlocked.Increment(ref _traktGeneration);
        var cts = new CancellationTokenSource();
        _traktActionCts = cts;

        TraktSyncNowButton.IsEnabled = false;
        RefreshTraktStatus();

        try
        {
            var result = await Task.Run(async () =>
            {
                return await trakt.SyncAsync(cts.Token).ConfigureAwait(false);
            }, cts.Token);

            if (generation == _traktGeneration)
            {
                if (result.Success)
                {
                    ShowTraktFeedback(LocalizationService.Text("TraktSyncSuccess") ?? "Trakt synchronization completed successfully.", InfoBarSeverity.Success);
                }
                else
                {
                    ShowTraktFeedback(result.AbortReason ?? "Synchronization did not complete.", InfoBarSeverity.Warning);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _traktGeneration)
            {
                ShowTraktFeedback(ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_traktActionCts, cts))
            {
                _traktActionCts = null;
            }
            cts.Dispose();
            if (generation == _traktGeneration)
            {
                _traktBusy = false;
                TraktSyncNowButton.IsEnabled = true;
                RefreshTraktStatus();
            }
        }
    }

    private async void TraktDisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_traktBusy) return;

        var trakt = AppServices.Trakt;
        if (trakt is null) return;

        _traktBusy = true;
        var generation = Interlocked.Increment(ref _traktGeneration);
        var cts = new CancellationTokenSource();
        _traktActionCts = cts;

        TraktDisconnectButton.IsEnabled = false;

        try
        {
            await Task.Run(async () =>
            {
                await trakt.DisconnectAsync(cts.Token).ConfigureAwait(false);
            }, cts.Token);

            if (generation == _traktGeneration)
            {
                ShowTraktFeedback(LocalizationService.Text("TraktDisconnectedFeedback") ?? "Disconnected from Trakt. Offline cache is retained.", InfoBarSeverity.Informational);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _traktGeneration)
            {
                ShowTraktFeedback(ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(_traktActionCts, cts))
            {
                _traktActionCts = null;
            }
            cts.Dispose();
            if (generation == _traktGeneration)
            {
                _traktBusy = false;
                TraktDisconnectButton.IsEnabled = true;
                RefreshTraktStatus();
            }
        }
    }

    private void ShowTraktFeedback(string message, InfoBarSeverity severity)
    {
        TraktFeedback.Message = message;
        TraktFeedback.Severity = severity;
        TraktFeedback.IsOpen = _hasBeenShown && Visibility == Visibility.Visible;
    }

    private static bool IsTraktHost(string host) =>
        host.Equals("trakt.tv", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".trakt.tv", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateTraktVerificationUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return null;
        if (Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            IsTraktHost(uri.Host))
        {
            return uri.AbsoluteUri;
        }
        return null;
    }

    private void RefreshSubDlStatus()
    {
        var configured = AppSettingsStore.IsSubDlConfigured();
        SubDlConnectedDot.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        SubDlMissingDot.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        SubDlStatusText.Text = LocalizationService.Text(configured ? "TmdbConnected" : "NotConfigured");
        SubDlApiKeyBox.PlaceholderText = configured ? "••••••••••••" : string.Empty;
        RemoveSubDlApiKeyButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSubDlFeedback(string message, InfoBarSeverity severity)
    {
        SubDlFeedback.Message = message;
        SubDlFeedback.Severity = severity;
        SubDlFeedback.IsOpen = _hasBeenShown && Visibility == Visibility.Visible;
    }

    private void SaveSubDlApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var candidate = SubDlApiKeyBox.Password;
        SubDlApiKeyBox.Password = string.Empty;

        var sanitized = TBDMovies.Application.Services.SettingsService.SanitizeAndValidateSubDlApiKey(candidate);
        if (sanitized is null)
        {
            ShowSubDlFeedback(LocalizationService.Text("SubDlInvalidKey"), InfoBarSeverity.Warning);
            RefreshSubDlStatus();
            return;
        }

        if (AppSettingsStore.SaveSubDlApiKey(sanitized))
        {
            ShowSubDlFeedback(LocalizationService.Text("SubDlKeySaved"), InfoBarSeverity.Success);
        }
        else
        {
            ShowSubDlFeedback(LocalizationService.Text("TmdbCredentialStorageError"), InfoBarSeverity.Error);
        }

        RefreshSubDlStatus();
    }

    private void RemoveSubDlApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        SubDlApiKeyBox.Password = string.Empty;
        if (AppSettingsStore.RemoveSubDlApiKey())
        {
            SubDlFeedback.IsOpen = false;
        }
        else
        {
            ShowSubDlFeedback(LocalizationService.Text("TmdbCredentialStorageError"), InfoBarSeverity.Error);
        }

        RefreshSubDlStatus();
    }

    private void GetSubDlKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://subdl.com"));
    }

    #endregion
}
