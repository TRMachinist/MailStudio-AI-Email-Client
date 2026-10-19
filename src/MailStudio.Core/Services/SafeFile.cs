namespace MailStudio.Services;

/// <summary>
/// Replace-by-rename that survives the short moment in which Windows Defender, the search indexer or a cloud
/// client still holds a freshly written file ("Access to the path is denied" / sharing violation).
/// </summary>
public static class SafeFile
{
    public static void Move(string source, string destination, bool overwrite = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(source, destination, overwrite); return; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 8 && File.Exists(source))
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
}
