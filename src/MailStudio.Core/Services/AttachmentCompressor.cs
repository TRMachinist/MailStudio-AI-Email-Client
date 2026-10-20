using System.IO.Compression;

namespace MailStudio.Services;

/// <summary>
/// Monitors attachment sizes and provides ZIP compression utilities for large attachments (>20 MB).
/// </summary>
public static class AttachmentCompressor
{
    public const long LargeAttachmentThreshold = 20 * 1024 * 1024; // 20 MB

    public static long GetTotalSize(IEnumerable<string> filePaths)
    {
        long total = 0;
        foreach (var p in filePaths)
        {
            if (File.Exists(p))
            {
                try { total += new FileInfo(p).Length; } catch { }
            }
        }
        return total;
    }

    public static bool IsLarge(IEnumerable<string> filePaths, out long totalSize)
    {
        totalSize = GetTotalSize(filePaths);
        return totalSize > LargeAttachmentThreshold;
    }

    public static string CreateZipArchive(IEnumerable<string> filePaths, string? targetZipPath = null)
    {
        var files = filePaths.Where(File.Exists).ToList();
        if (files.Count == 0) throw new InvalidOperationException("No valid files to compress");

        if (string.IsNullOrEmpty(targetZipPath))
        {
            var baseDir = Path.GetDirectoryName(files[0]) ?? Path.GetTempPath();
            var zipName = $"Ekler_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            targetZipPath = Path.Combine(baseDir, zipName);
        }

        var dir = Path.GetDirectoryName(targetZipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(targetZipPath)) File.Delete(targetZipPath);

        using (var zip = ZipFile.Open(targetZipPath, ZipArchiveMode.Create))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var entryName = Path.GetFileName(file);
                int count = 1;
                while (usedNames.Contains(entryName))
                {
                    var nameOnly = Path.GetFileNameWithoutExtension(file);
                    var ext = Path.GetExtension(file);
                    entryName = $"{nameOnly}_{count++}{ext}";
                }
                usedNames.Add(entryName);
                zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            }
        }
        return targetZipPath;
    }
}
