namespace AlienRgb.Core;

/// <summary>Tiny shared append-only log for diagnosing sleep/resume behavior. Never throws.</summary>
public static class DiagLog
{
    // See ProfileStore.ConfigDir: %APPDATA% is unreliable across launch contexts on this
    // machine, so diagnostics live next to the exe instead.
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "data", "resume.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // diagnostics must never crash the app
        }
    }
}
