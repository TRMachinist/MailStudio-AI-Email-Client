using System.IO;

namespace MailStudio.Services;

/// <summary>
/// Security checks for archive extraction (Zip slip prevention, safe entry paths).
/// </summary>
public static class ArchiveSecurity
{
    public static void ValidateEntryKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Invalid archive entry name: empty or whitespace");

        var normalized = key.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            normalized.Contains("../") ||
            normalized.Contains("/..") ||
            normalized == ".." ||
            normalized.Contains("..\\") ||
            Path.IsPathRooted(key) ||
            (key.Length >= 2 && key[1] == ':'))
        {
            throw new InvalidOperationException("Zip slip detected: " + key);
        }
    }

    public static string GetSafeDestination(string targetDir, string entryKey)
    {
        ValidateEntryKey(entryKey);
        var normalized = entryKey.Replace('\\', '/');
        var fullTarget = Path.GetFullPath(targetDir);
        var fullDest = Path.GetFullPath(Path.Combine(fullTarget, normalized));
        if (!fullDest.StartsWith(fullTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && fullDest != fullTarget)
        {
            throw new InvalidOperationException("Destination escapes target directory: " + entryKey);
        }
        return fullDest;
    }
}
