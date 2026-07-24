using System.IO;

namespace ZDesk.Services;

public sealed class RecoveryService
{
    private string _directory = AppDataPathService.DataDirectory;

    public string SessionMarkerFile => Path.Combine(_directory, "session.active");
    public bool PreviousSessionEndedUnexpectedly { get; private set; }
    public void SetDataDirectory(string directory) => _directory = AppDataPathService.Normalize(directory);

    public void MarkSessionStarted()
    {
        Directory.CreateDirectory(_directory);
        PreviousSessionEndedUnexpectedly = File.Exists(SessionMarkerFile);
        File.WriteAllText(SessionMarkerFile, $"PID={Environment.ProcessId};STARTED={DateTimeOffset.Now:O}");
    }

    public void MarkSessionCompleted()
    {
        if (File.Exists(SessionMarkerFile))
        {
            File.Delete(SessionMarkerFile);
        }
    }
}
