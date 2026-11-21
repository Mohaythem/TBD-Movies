using System.Text.RegularExpressions;
using TBDMovies.Application.External;
using TBDMovies.UI.Services;
using Xunit;

namespace TBDMovies.Tests.UI;

public sealed class PostHandoffPresentationTests
{
    private static string UiRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "src", "TBDMovies.UI");
                if (Directory.Exists(path)) return path;
            }
            throw new DirectoryNotFoundException("UI source not found.");
        }
    }

    [Fact]
    public void Every_metadata_category_has_distinct_English_and_Arabic_copy()
    {
        var source = File.ReadAllText(Path.Combine(UiRoot, "Services", "LocalizationService.cs"));
        var keys = Enum.GetValues<MetadataFailureKind>().Select(MetadataErrorKey.For).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        foreach (var key in keys.Concat(["MetadataCancelled", "MetadataUnexpectedError", "RelinkFailedHelp", "CrashTempTitle", "CrashTempHelp"]))
        {
            var entries = Regex.Matches(source, "\\[\"" + key + "\"\\] = \"([^\"]+)\"");
            Assert.Equal(2, entries.Count);
            Assert.NotEqual(entries[0].Groups[1].Value, entries[1].Groups[1].Value);
            Assert.Matches("[\\u0600-\\u06ff]", entries[1].Groups[1].Value);
        }
    }

    [Fact]
    public void Error_mapping_never_returns_sensitive_exception_or_connection_text()
    {
        const string sensitive = "secret-token https://example.invalid/?api_key=secret-token";
        Assert.Equal("MetadataUnexpectedError", MetadataErrorKey.For(new Exception(sensitive)));
        Assert.Equal("MetadataCancelled", MetadataErrorKey.For(new OperationCanceledException(sensitive)));
        Assert.Equal("TmdbConnectionFailed", MetadataErrorKey.For(new ConnectionTestResult(false, sensitive)));
        Assert.Equal("MetadataAuthenticationError", MetadataErrorKey.For(
            new ConnectionTestResult(false, sensitive, 401, MetadataFailureKind.Authentication)));
    }

    [Fact]
    public void Settings_and_relink_use_localized_safe_errors_and_temp_report_is_read_only()
    {
        var settings = File.ReadAllText(Path.Combine(UiRoot, "Views", "SettingsPage.xaml.cs"));
        var connection = settings[settings.IndexOf("private async void TestConnectionButton_Click", StringComparison.Ordinal)..];
        connection = connection[..connection.IndexOf("private void SetupGuideButton_Click", StringComparison.Ordinal)];
        Assert.Contains("MetadataErrorText.For(result)", connection);
        Assert.DoesNotContain("ex.Message", connection);
        Assert.DoesNotContain("AppServices.TmdbClient", connection);
        var check = File.ReadAllText(Path.Combine(UiRoot, "Views", "CheckLibraryPage.xaml.cs"));
        var relink = check[check.IndexOf("private async void RelinkAction_Click", StringComparison.Ordinal)..];
        relink = relink[..relink.IndexOf("private async void IssueAction_Click", StringComparison.Ordinal)];
        Assert.Contains("LocalizationService.Text(\"RelinkFailedHelp\")", relink);
        Assert.DoesNotContain("error.Message", relink);
        Assert.Contains("AppServices.DetectStaleTempFiles(", check);
        Assert.Contains("_staleTemps.Select(file => file.FilePath)", check);
        Assert.DoesNotContain("File.Delete", check);
        Assert.DoesNotContain("Directory.Delete", check);
    }

    [Fact]
    public void Batch1_3_WinUiSecretHygiene_SubDlDialog_AppSettingsStore_And_AppServices_ContractsHold()
    {
        // 1. SubtitleDialog.cs uses AppSettingsStore.IsSubDlConfigured() and never calls removed ReadSubDlApiKey()
        var subtitleDialog = File.ReadAllText(Path.Combine(UiRoot, "Views", "SubtitleDialog.cs"));
        Assert.Contains("if (!AppSettingsStore.IsSubDlConfigured())", subtitleDialog, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadSubDlApiKey", subtitleDialog, StringComparison.Ordinal);

        // 2. AppSettingsStore.cs never exposes raw SubDL key, uses LoadFoldersOrThrow on mutations, retires legacy keys strictly with empty sentinel, and propagates IOException
        var appSettingsStore = File.ReadAllText(Path.Combine(UiRoot, "Services", "AppSettingsStore.cs"));
        Assert.DoesNotContain("ReadSubDlApiKey", appSettingsStore, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSubDlApiKey", appSettingsStore, StringComparison.Ordinal);
        Assert.Contains("LibraryViewModePreference.Parse(Read(LibraryViewModeKey))", appSettingsStore, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(appSettingsStore, @"\bLoadFoldersOrThrow\(\)").Count); // 3 mutations + 1 definition
        Assert.Contains("var serialized = folders.Count == 0 ? string.Empty : string.Join(\"\\n\", folders);", appSettingsStore, StringComparison.Ordinal);
        Assert.Contains("AppServices.SettingsStore.Set(MediaFoldersKey, serialized,", appSettingsStore, StringComparison.Ordinal);
        Assert.Contains("AppServices.SettingsStore.Delete(MovieFolderKey);", appSettingsStore, StringComparison.Ordinal);
        Assert.Contains("AppServices.SettingsStore.Delete(ShowFolderKey);", appSettingsStore, StringComparison.Ordinal);
        Assert.DoesNotContain("Write(MovieFolderKey", appSettingsStore, StringComparison.Ordinal);
        Assert.DoesNotContain("Write(ShowFolderKey", appSettingsStore, StringComparison.Ordinal);
        Assert.Contains("throw new IOException(", appSettingsStore, StringComparison.Ordinal);

        // 3. SettingsPage.xaml.cs clears secret controls/placeholders on Deactivate, gates SubDL InfoBar, masks <12-char ClientIds, and sets TraktConfigInputPanel visible synchronously before Focus
        var settingsPage = File.ReadAllText(Path.Combine(UiRoot, "Views", "SettingsPage.xaml.cs"));
        var deactivateSection = settingsPage[settingsPage.IndexOf("public void Deactivate()", StringComparison.Ordinal)..];
        deactivateSection = deactivateSection[..deactivateSection.IndexOf("private bool _isSyncing", StringComparison.Ordinal)];
        Assert.Contains("TokenBox.Password = string.Empty;", deactivateSection, StringComparison.Ordinal);
        Assert.Contains("TraktClientIdBox.Password = string.Empty;", deactivateSection, StringComparison.Ordinal);
        Assert.Contains("TraktClientIdBox.PlaceholderText = string.Empty;", deactivateSection, StringComparison.Ordinal);
        Assert.Contains("SubDlApiKeyBox.Password = string.Empty;", deactivateSection, StringComparison.Ordinal);

        Assert.Contains("private void ShowSubDlFeedback(string message, InfoBarSeverity severity)", settingsPage, StringComparison.Ordinal);
        Assert.Contains("SubDlFeedback.IsOpen = _hasBeenShown && Visibility == Visibility.Visible;", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("SubDlFeedback.IsOpen = true;", settingsPage, StringComparison.Ordinal);
        Assert.Contains("if (value.Length < 12)", settingsPage, StringComparison.Ordinal);

        var traktSaveSection = settingsPage[settingsPage.IndexOf("private async void TraktSaveConfigButton_Click", StringComparison.Ordinal)..];
        traktSaveSection = traktSaveSection[..traktSaveSection.IndexOf("private async void TraktEditConfigButton_Click", StringComparison.Ordinal)];
        Assert.DoesNotContain("var existingConfig = AppServices.TraktConfig;", traktSaveSection, StringComparison.Ordinal);
        Assert.Contains("AppServices.TraktConfig.ClientId", traktSaveSection, StringComparison.Ordinal);

        var traktEditSection = settingsPage[settingsPage.IndexOf("private async void TraktEditConfigButton_Click", StringComparison.Ordinal)..];
        traktEditSection = traktEditSection[..traktEditSection.IndexOf("private void TraktCancelConfigButton_Click", StringComparison.Ordinal)];
        Assert.Contains("await Task.Run(() => AppServices.TraktConfig)", traktEditSection, StringComparison.Ordinal);
        var visibleIdx = traktEditSection.IndexOf("TraktConfigInputPanel.Visibility = Visibility.Visible;", StringComparison.Ordinal);
        var focusIdx = traktEditSection.IndexOf("TraktClientIdBox.Focus(FocusState.Programmatic);", StringComparison.Ordinal);
        var refreshIdx = traktEditSection.IndexOf("RefreshTraktStatus();", StringComparison.Ordinal);
        Assert.True(visibleIdx >= 0 && focusIdx > visibleIdx && refreshIdx > focusIdx);

        // 4. AppServices.cs uses SqliteConnectionStringBuilder, unified SubDL plaintext migration, SettingsService.CompleteStartupSecretReclaim, and zero forbidden identifiers
        var appServices = File.ReadAllText(Path.Combine(UiRoot, "Services", "AppServices.cs"));
        Assert.Contains("SqliteConnectionStringBuilder", appServices, StringComparison.Ordinal);
        Assert.Contains("deferStartupSecretReclaim: true", appServices, StringComparison.Ordinal);
        Assert.Contains("WindowsSubDlCredentialStore.MigrateLegacyPlaintext(", appServices, StringComparison.Ordinal);
        Assert.Contains("SettingsService.CompleteStartupSecretReclaim(", appServices, StringComparison.Ordinal);
        Assert.Contains("security_l6_baseline_vacuumed", appServices, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderWatcher", appServices, StringComparison.Ordinal);
        Assert.DoesNotContain("SmartRelink", appServices, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch2_3_DetailsPages_PlaybackEndedLifecycle_ContractsHold()
    {
        // 1. TVShowDetailsPage implements IActivatableView and IDeactivatableView with symmetric lifecycle
        var showPage = File.ReadAllText(Path.Combine(UiRoot, "Views", "TVShowDetailsPage.xaml.cs"));
        Assert.Contains("public sealed partial class TVShowDetailsPage : Page, ILocalizableView, IActivatableView, IDeactivatableView", showPage, StringComparison.Ordinal);
        Assert.Contains("private bool _playbackTrackingSubscribed;", showPage, StringComparison.Ordinal);
        Assert.Contains("private void EnsurePlaybackTrackingSubscribed()", showPage, StringComparison.Ordinal);
        Assert.Contains("private void UnsubscribePlaybackTracking()", showPage, StringComparison.Ordinal);
        Assert.Contains("public void Activate()", showPage, StringComparison.Ordinal);
        Assert.Contains("EnsurePlaybackTrackingSubscribed();", showPage, StringComparison.Ordinal);
        Assert.Contains("RefreshEpisodeProgress();", showPage, StringComparison.Ordinal);
        Assert.Contains("UpdateShowPersonalVisuals();", showPage, StringComparison.Ordinal);

        var showDeactivate = showPage[showPage.IndexOf("public void Deactivate()", StringComparison.Ordinal)..];
        showDeactivate = showDeactivate[..showDeactivate.IndexOf("private void EnsurePlaybackTrackingSubscribed()", StringComparison.Ordinal)];
        Assert.Contains("UnsubscribePlaybackTracking();", showDeactivate, StringComparison.Ordinal);

        // 2. MovieDetailsPage implements IActivatableView and IDeactivatableView with symmetric lifecycle
        var moviePage = File.ReadAllText(Path.Combine(UiRoot, "Views", "MovieDetailsPage.xaml.cs"));
        Assert.Contains("public sealed partial class MovieDetailsPage : Page, ILocalizableView, IActivatableView, IDeactivatableView", moviePage, StringComparison.Ordinal);
        Assert.Contains("private bool _playbackTrackingSubscribed;", moviePage, StringComparison.Ordinal);
        Assert.Contains("private void EnsurePlaybackTrackingSubscribed()", moviePage, StringComparison.Ordinal);
        Assert.Contains("private void UnsubscribePlaybackTracking()", moviePage, StringComparison.Ordinal);
        Assert.Contains("public void Activate()", moviePage, StringComparison.Ordinal);
        Assert.Contains("ReloadPersonalState();", moviePage, StringComparison.Ordinal);

        var movieDeactivate = moviePage[moviePage.IndexOf("public void Deactivate()", StringComparison.Ordinal)..];
        movieDeactivate = movieDeactivate[..movieDeactivate.IndexOf("private void EnsurePlaybackTrackingSubscribed()", StringComparison.Ordinal)];
        Assert.Contains("UnsubscribePlaybackTracking();", movieDeactivate, StringComparison.Ordinal);

        // 3. Raw unguarded PlaybackEnded += is eliminated from Loaded in both pages
        var showLoadedIdx = showPage.IndexOf("Loaded +=", StringComparison.Ordinal);
        var showLoadedEndIdx = showPage.IndexOf("};", showLoadedIdx, StringComparison.Ordinal);
        var showLoadedBlock = showPage[showLoadedIdx..showLoadedEndIdx];
        Assert.Contains("EnsurePlaybackTrackingSubscribed();", showLoadedBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaybackTracking.PlaybackEnded +=", showLoadedBlock, StringComparison.Ordinal);

        var movieLoadedIdx = moviePage.IndexOf("Loaded +=", StringComparison.Ordinal);
        var movieLoadedEndIdx = moviePage.IndexOf("};", movieLoadedIdx, StringComparison.Ordinal);
        var movieLoadedBlock = moviePage[movieLoadedIdx..movieLoadedEndIdx];
        Assert.Contains("EnsurePlaybackTrackingSubscribed();", movieLoadedBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaybackTracking.PlaybackEnded +=", movieLoadedBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationsLogPage_Enforces_Undo_Double_Click_Guard_And_Preview_Discard_On_Non_Primary()
    {
        var code = File.ReadAllText(Path.Combine(UiRoot, "Views", "OperationsLogPage.xaml.cs"));
        Assert.Contains("button.IsEnabled = false;", code);
        Assert.Contains("AppServices.History.DiscardUndoPreview(preview.PreviewId);", code);
        Assert.Contains("LibraryDataChanged?.Invoke(this, EventArgs.Empty);", code);
        Assert.Contains("button.IsEnabled = true;", code);

        var xaml = File.ReadAllText(Path.Combine(UiRoot, "Views", "OperationsLogPage.xaml"));
        Assert.Contains("x:Name=\"UndoButton\"", xaml);
        Assert.Contains("Click=\"UndoButton_Click\"", xaml);
    }

    [Fact]
    public void SettingsPage_Wires_CreateBackup_And_RestoreFromBackup_With_Full_UI_Refresh()
    {
        var code = File.ReadAllText(Path.Combine(UiRoot, "Views", "SettingsPage.xaml.cs"));
        Assert.Contains("AppServices.CreateDatabaseBackup()", code);
        Assert.Contains("AppServices.RestoreDatabaseBackup(backupPath)", code);
        Assert.Contains("AppServices.ListDatabaseBackups()", code);
        Assert.Contains("LibraryDataChanged?.Invoke(this, EventArgs.Empty);", code);

        var xaml = File.ReadAllText(Path.Combine(UiRoot, "Views", "SettingsPage.xaml"));
        Assert.Contains("x:Name=\"CreateBackupButton\"", xaml);
        Assert.Contains("x:Name=\"RestoreBackupButton\"", xaml);
        Assert.Contains("x:Name=\"AvailableBackupsFlyout\"", xaml);
    }
}
