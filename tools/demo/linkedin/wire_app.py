from pathlib import Path

p = Path(r"D:\MailStudio_GitHub\src\MailStudio\App.xaml.cs")
c = p.read_text(encoding="utf-8")
c_norm = c.replace("\r\n", "\n")

old = """        var showcaseLinkedInAt = Array.IndexOf(e.Args, "--showcase-linkedin");
        if (demo && showcaseLinkedInAt >= 0 && showcaseLinkedInAt + 1 < e.Args.Length)
        {
            var target = System.IO.Path.GetFullPath(e.Args[showcaseLinkedInAt + 1]);
            window.ContentRendered += async (_, _) => await Services.ShowcaseLinkedIn.RunAsync(window, target);
        }"""

new = """        var showcaseLinkedInAt = Array.IndexOf(e.Args, "--showcase-linkedin");
        if (demo && showcaseLinkedInAt >= 0 && showcaseLinkedInAt + 1 < e.Args.Length)
        {
            var target = System.IO.Path.GetFullPath(e.Args[showcaseLinkedInAt + 1]);
            window.ContentRendered += async (_, _) => await Services.ShowcaseLinkedIn.RunAsync(window, target);
        }
        var showcaseLinkedIn2At = Array.IndexOf(e.Args, "--showcase-linkedin2");
        if (demo && showcaseLinkedIn2At >= 0 && showcaseLinkedIn2At + 1 < e.Args.Length)
        {
            var target = System.IO.Path.GetFullPath(e.Args[showcaseLinkedIn2At + 1]);
            window.ContentRendered += async (_, _) => await Services.ShowcaseLinkedIn2.RunAsync(window, target);
        }"""

assert old in c_norm, "old block not found in App.xaml.cs"
c_norm = c_norm.replace(old, new, 1)
p.write_text(c_norm.replace("\n", "\r\n"), encoding="utf-8")
print("Successfully wired --showcase-linkedin2 in App.xaml.cs")
