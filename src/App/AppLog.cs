namespace GigaGlade;

/// <summary>Appends status messages and errors to %LOCALAPPDATA%\GigaGlade\gigaglade.log (kept small).</summary>
public static class AppLog
{
    static readonly Lock Gate = new();
    public static readonly string FilePath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GigaGlade", "gigaglade.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
