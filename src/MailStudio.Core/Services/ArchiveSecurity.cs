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

    /// <summary>Extracts every file of a ZIP / RAR / 7z archive below <paramref name="targetDir"/>.
    /// Entries that would escape the folder are refused, existing files are never overwritten, nothing is executed.</summary>
    public static int ExtractAll(string archivePath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archivePath, null);
        var n = 0;
        foreach (var e in archive.Entries)
        {
            if (e.IsDirectory || string.IsNullOrEmpty(e.Key)) continue;
            var dest = GetSafeDestination(targetDir, e.Key);
            if (File.Exists(dest)) throw new IOException("File already exists: " + dest);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var src = e.OpenEntryStream())
            using (var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                src.CopyTo(fs);
            n++;
        }
        return n;
    }
}
