using System.IO.Compression;

namespace MailStudio.Services.Skills;

/// <summary>
/// Built-in skills ship next to the app (skills\), the user's own live in %AppData%\MailStudio\skills.
/// A user skill with the same folder name overrides the built-in one. Settings keep which are off / auto-run.
/// </summary>
public sealed class SkillStore
{
    readonly AppSettings _settings;
    public SkillStore(AppSettings? settings = null) => _settings = settings ?? AppSettings.Shared;

    public static string UserFolder
    {
        get { var p = Path.Combine(AppPaths.Root, "skills"); Directory.CreateDirectory(p); return p; }
    }

    public static string? BuiltInFolder
    {
        get
        {
            var b = AppContext.BaseDirectory;
            foreach (var c in new[] { Path.Combine(b, "skills"), Path.Combine(b, "..", "skills"), Path.Combine(b, @"..\..\..\..\..\skills") })
                if (Directory.Exists(c)) return Path.GetFullPath(c);
            return null;
        }
    }

    public List<Skill> All()
    {
        var result = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        void Scan(string? root, bool builtIn)
        {
            if (root == null || !Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d))
                if (File.Exists(Path.Combine(dir, "SKILL.md")))
                {
                    var s = Skill.Load(dir, builtIn);
                    if (s.Error == null) s.Enabled = !_settings.DisabledSkills.Contains(s.Id, StringComparer.OrdinalIgnoreCase);
                    result[s.Id] = s;
                }
        }
        Scan(BuiltInFolder, true);
        Scan(UserFolder, false);
        return result.Values.OrderBy(s => s.BuiltIn ? 1 : 0).ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public List<Skill> Enabled() => All().Where(s => s.Enabled && s.Error == null).ToList();

    public bool IsAuto(Skill s) => s.RunsOnNewMail && _settings.AutoRunSkills.Contains(s.Id, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(Skill s, bool on)
    {
        _settings.DisabledSkills.RemoveAll(x => x.Equals(s.Id, StringComparison.OrdinalIgnoreCase));
        if (!on) _settings.DisabledSkills.Add(s.Id);
        _settings.Save();
    }

    public void SetAuto(Skill s, bool on)
    {
        _settings.AutoRunSkills.RemoveAll(x => x.Equals(s.Id, StringComparison.OrdinalIgnoreCase));
        if (on) _settings.AutoRunSkills.Add(s.Id);
        _settings.Save();
    }

    /// <summary>Writes a new user skill. Validates the file first; never overwrites an existing folder silently.</summary>
    public Skill Save(string skillMd, string? id = null, bool overwrite = false)
    {
        var parsed = Skill.Parse(skillMd, id ?? "new");
        id ??= Skill.Slug(parsed.Name);
        var dir = Path.Combine(UserFolder, id);
        if (Directory.Exists(dir) && !overwrite)
        {
            int n = 2; while (Directory.Exists(dir + "-" + n)) n++;
            dir += "-" + n; id = Path.GetFileName(dir);
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd.Replace("\r\n", "\n"));
        return Skill.Load(dir, false);
    }

    /// <summary>Copies a built-in skill to the user folder so it can be edited.</summary>
    public Skill CopyToUser(Skill s)
    {
        var dir = Path.Combine(UserFolder, s.Id);
        if (!Directory.Exists(dir)) CopyDir(s.Folder, dir);
        return Skill.Load(dir, false);
    }

    /// <summary>Imports a skill folder, a .zip with one or more skills, or a single SKILL.md file.</summary>
    public List<Skill> Import(string path)
    {
        var added = new List<Skill>();
        if (Directory.Exists(path))
        {
            var dirs = File.Exists(Path.Combine(path, "SKILL.md")) ? new[] { path } : Directory.GetDirectories(path).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))).ToArray();
            foreach (var d in dirs) { Skill.Parse(File.ReadAllText(Path.Combine(d, "SKILL.md")), Path.GetFileName(d)); added.Add(Skill.Load(CopyUnique(d), false)); }
        }
        else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var tmp = Path.Combine(Path.GetTempPath(), "MailStudio-skill-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                    foreach (var e in zip.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(tmp, e.FullName));
                        if (!target.StartsWith(Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip guard
                        if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        e.ExtractToFile(target, true);
                    }
                foreach (var md in Directory.GetFiles(tmp, "SKILL.md", SearchOption.AllDirectories))
                {
                    var d = Path.GetDirectoryName(md)!;
                    Skill.Parse(File.ReadAllText(md), Path.GetFileName(d));
                    added.Add(Skill.Load(CopyUnique(d == tmp ? d : d), false));
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
        else if (File.Exists(path)) added.Add(Save(File.ReadAllText(path)));
        if (added.Count == 0) throw new InvalidDataException(L.T("İçinde SKILL.md bulunan bir skill bulunamadı."));
        return added;
    }

    public void Delete(Skill s)
    {
        if (s.BuiltIn) throw new InvalidOperationException(L.T("Hazır skill'ler silinemez; kapatabilirsiniz."));
        if (!Path.GetFullPath(s.Folder).StartsWith(Path.GetFullPath(UserFolder), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(s.Folder, true);
    }

    public static void ExportZip(Skill s, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in Directory.GetFiles(s.Folder, "*", SearchOption.AllDirectories))
            zip.CreateEntryFromFile(f, Path.Combine(s.Id, Path.GetRelativePath(s.Folder, f)).Replace('\\', '/'));
    }

    string CopyUnique(string source)
    {
        var name = Path.GetFileName(source.TrimEnd('\\', '/'));
        if (name.Length == 0 || name.StartsWith("MailStudio-skill-")) name = "imported-skill";
        var dir = Path.Combine(UserFolder, name); int n = 2;
        while (Directory.Exists(dir)) dir = Path.Combine(UserFolder, name + "-" + n++);
        CopyDir(source, dir);
        return dir;
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, true);
        }
    }
}
