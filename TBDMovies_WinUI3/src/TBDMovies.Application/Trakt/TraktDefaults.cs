namespace TBDMovies.Application.Trakt;

/// <summary>
/// Pre-configured official Trakt API application defaults for zero-config onboarding.
/// </summary>
public static class TraktDefaults
{
    public const string OfficialClientId = "97424164cb6b5c90ec13324f3316e138a0f00f8650dfc3e981ea3c489c7d42cf";
    public const string DefaultRedirectUri = "https://localhost";

    public static bool HasOfficialClientId => !string.IsNullOrWhiteSpace(OfficialClientId);

    public static TraktAuthConfig CreateDefaultConfig() => new(OfficialClientId, DefaultRedirectUri);
}
