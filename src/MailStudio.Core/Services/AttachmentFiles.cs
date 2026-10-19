using System.Security.Cryptography;
using System.Text;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>Only complete downloads are published. v2 deliberately ignores older, possibly partial cache files.</summary>
public static class AttachmentFiles
{
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string DirectoryFor(string root, MailHeader h) => Path.Combine(root, "v2",
        Hash($"{h.AccountId}\n{h.Folder}\n{h.UidValidity}\n{h.Uid}"));

    public static string PathFor(string directory, AttachmentInfo att)
    {
        var name = string.Join("_", att.FileName.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "ek";
        if (name.Length > 120) name = name[..100] + Path.GetExtension(name)[..Math.Min(16, Path.GetExtension(name).Length)];
        // Keep the original filename for opening/forwarding; the MIME identity lives in its parent directory.
        return Path.Combine(directory, Hash(att.Part + "\n" + att.FileName), name);
    }

    public static async Task<string> WriteAsync(string path, Func<Stream, Task> write, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(stream);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            MailStudio.Services.SafeFile.Move(temp, path, true);
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
