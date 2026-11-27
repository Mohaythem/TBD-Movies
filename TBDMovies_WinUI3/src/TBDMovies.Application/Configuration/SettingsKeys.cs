namespace TBDMovies.Application.Configuration;

/// <summary>
/// Single source of truth for the <c>settings</c>-table key strings. Each key is written
/// by one component and read by another, often across layers (Application writes what the
/// UI startup fast-path reads directly, or what Infrastructure credential stores migrate),
/// so the literals are defined here instead of being re-typed per file.
/// </summary>
public static class SettingsKeys
{
    public const string UiTheme = "ui_theme";
    public const string UiLanguage = "ui_language";

    public const string PlaybackPlayerType = "playback_player_type";
    public const string PlaybackPlayerCustomPath = "playback_player_custom_path";
    public const string PlaybackPlayerCustomArgs = "playback_player_custom_args";

    public const string TraktApiConfig = "trakt_api_config";

    public const string SecurityVacuumPending = "security_vacuum_pending";
    public const string SecurityL6BaselineVacuumed = "security_l6_baseline_vacuumed";

    // Legacy secret rows, kept only for the one-way migration to the OS credential vault.
    public const string LegacyTmdbReadAccessToken = "tmdb_read_access_token";
    public const string LegacySubDlApiKey = "subdl_api_key";
    public const string LegacyTmdbDpapiCredential = "tmdb_credential_dpapi";
    public const string LegacyTraktDpapiCredential = "trakt_credential_dpapi";
}
