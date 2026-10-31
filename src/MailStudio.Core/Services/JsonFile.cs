using System.Text;
using System.Text.Json;

namespace MailStudio.Services;

/// <summary>Small JSON list files (rules, quotes, snoozes, templates...): atomic writes and a kept copy of a damaged file.</summary>
public static class JsonFile
{
    /// <summary>The file's text, or "[]" when it is missing. A file that is not valid JSON is copied to *.bozuk-yyyyMMdd-HHmmss first,
    /// so the next save never overwrites the user's data without a backup.</summary>
    public static string ReadOrBackup(string path)
    {
        if (!File.Exists(path)) return "[]";
        var text = File.ReadAllText(path);
        try { using var _ = JsonDocument.Parse(text); return text; }
        catch (JsonException)
        {
            try { File.Copy(path, path + ".bozuk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch (IOException) { }
            return "[]";
        }
    }

    /// <summary>Writes through a temporary file and an atomic replace (no half-written file after a crash).</summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        SafeFile.Move(tmp, path, true);
    }

    /// <summary>Identity of a file's content (length + FNV-1a hash). Used instead of the write time, which can stay the
    /// same for two quick writes by the app and the MCP agent (clock resolution) – a missed change would lose an edit.</summary>
    public readonly record struct Stamp(long Length, ulong Hash)
    {
        public static Stamp OfBytes(byte[] b)
        {
            ulong h = 14695981039346656037;
            foreach (var x in b) { h ^= x; h *= 1099511628211; }
            return new Stamp(b.LongLength, h);
        }
        public static Stamp OfText(string text) => OfBytes(new UTF8Encoding(false).GetBytes(text));
        /// <summary>Stamp of the file now (default when missing or unreadable).</summary>
        public static Stamp Of(string path)
        {
            try { return File.Exists(path) ? OfBytes(File.ReadAllBytes(path)) : default; }
            catch (IOException) { return default; }
        }
    }
}
