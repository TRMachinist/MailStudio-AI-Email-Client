using System.IO;
using System.Windows;
using System.Windows.Controls;
using MailStudio.Models;

namespace MailStudio.Services;

/// <summary>
/// "Compare with previous revision" for every viewer: the main window knows which mail an attachment came from
/// and looks for earlier revisions in that correspondent's mails; otherwise the file's folder and a file dialog are used.
/// </summary>
public static class RevisionLookup
{
    /// <summary>Set by the main window: (owner, path of the new revision) → path of the chosen older revision, or null.</summary>
    public static Func<Window, string, Task<string?>>? Resolver { get; set; }
    /// <summary>Set by the main window: compares (old, new) and shows the result with "add to reply" for the source mail.</summary>
    public static Func<string, string, Task>? ShowDiff { get; set; }

    public static async Task<string?> PickOlderAsync(Window owner, string path)
    {
        if (Resolver != null) return await Resolver(owner, path);
        return PickFromDisk(owner, path);
    }

    public static string? PickFromDisk(Window owner, string path)
    {
        var found = RevisionService.FindCandidatesOnDisk(path);
        if (found.Count == 0) return Browse(owner, path);
        var (choice, browse) = Choose(owner, found, Path.GetFileName(path));
        return browse ? Browse(owner, path) : choice?.FilePath;
    }

    public static string? Browse(Window owner, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Önceki revizyon dosyasını seçin"),
            Filter = ext switch
            {
                ".step" or ".stp" => "STEP (*.step;*.stp)|*.step;*.stp",
                ".dxf" or ".dwg" => "CAD (*.dxf;*.dwg)|*.dxf;*.dwg",
                ".xlsx" or ".xlsm" => "Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm",
                ".pdf" => "PDF (*.pdf)|*.pdf",
                _ => "*.*|*.*"
            },
            InitialDirectory = Path.GetDirectoryName(path)
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    /// <summary>Lets the user pick one of the found revisions (the best guess is preselected).</summary>
    public static (RevisionCandidate? Choice, bool Browse) Choose(Window owner, IReadOnlyList<RevisionCandidate> candidates, string newFileName)
    {
        RevisionCandidate? result = null; var browse = false;
        var w = new Window
        {
            Title = L.T("Önceki revizyonu seçin"), Owner = owner, Width = 640, Height = 380, MinWidth = 420, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        w.SetResourceReference(Control.BackgroundProperty, "BgSurfacePrimaryBrush");
        var root = new DockPanel { Margin = new Thickness(16) };
        var head = new TextBlock { Text = L.F("\"{0}\" ile karşılaştırılacak revizyon:", newFileName), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        UiTranslator.SetSkip(head, true);
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var list = new ListBox { DisplayMemberPath = nameof(RevisionCandidate.Display), ItemsSource = candidates, SelectedIndex = 0 };
        UiTranslator.SetSkip(list, true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Button B(string text, string style, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel, Padding = new Thickness(12, 4, 12, 4) };
            b.SetResourceReference(FrameworkElement.StyleProperty, style);
            buttons.Children.Add(b);
            return b;
        }
        B(L.T("Başka dosya seç…"), "FlatButton").Click += (_, _) => { browse = true; w.Close(); };
        B(L.T("Karşılaştır"), "PrimaryButton", isDefault: true).Click += (_, _) => { result = list.SelectedItem as RevisionCandidate; w.Close(); };
        B(L.T("İptal"), "FlatButton", isCancel: true).Click += (_, _) => w.Close();
        list.MouseDoubleClick += (_, _) => { result = list.SelectedItem as RevisionCandidate; w.Close(); };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(list);
        w.Content = root;
        UiTranslator.Apply(w);
        w.ShowDialog();
        return (result, browse);
    }
}
