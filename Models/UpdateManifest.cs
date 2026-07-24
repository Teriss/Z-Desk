namespace ZDesk.Models;

public sealed class UpdateManifest
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
}
