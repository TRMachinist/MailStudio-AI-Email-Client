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
}
