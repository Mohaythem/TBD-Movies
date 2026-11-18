namespace TBDMovies.Application.Subtitles;

/// <summary>
/// Protected OS credential storage contract for the user's SubDL API key.
/// </summary>
public interface ISubDlCredentialStore
{
    string? Read();
    void Write(string credential);
    void Delete();
}
