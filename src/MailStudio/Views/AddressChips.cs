using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// Sender / recipients shown like Outlook: every person is clickable. Click or right-click: copy, copy address only,
/// new mail, add to contacts (or edit), find this person's mails, copy all addresses.
/// </summary>
public static class AddressChips
{
    public static ContactStore? Store { get; set; }
    public static Action<string>? NewMailTo { get; set; }
    public static Action<string>? FindMailsOf { get; set; }

    /// <summary>Fills the panel: optional label ("To: ") then one link per address.</summary>
    public static void Fill(Panel panel, string? label, string? field, double fontSize = 12, bool bold = false, Brush? foreground = null)
    {
        panel.Children.Clear();
        var people = ContactStore.SplitAddresses(field);
        if (people.Count == 0) { panel.Visibility = Visibility.Collapsed; return; }
        panel.Visibility = Visibility.Visible;
        var text = new TextBlock { FontSize = fontSize, TextWrapping = TextWrapping.Wrap, Foreground = foreground ?? ThemeManager.Brush("TextSecondaryBrush") };
        if (!string.IsNullOrEmpty(label)) text.Inlines.Add(new Run(label));
        for (int i = 0; i < people.Count; i++)
        {
            var (name, addr) = people[i];
            var saved = Store?.FindByEmail(addr);
            var shown = saved?.DisplayName is { Length: > 0 } sn ? sn : name.Length > 0 ? name : addr;
            var link = new Hyperlink(new Run(shown))
            {
                Foreground = foreground ?? ThemeManager.Brush("TextPrimaryBrush"), TextDecorations = null, Cursor = Cursors.Hand,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = (name.Length > 0 ? $"{name} <{addr}>" : addr) + (saved?.Company is { Length: > 0 } co ? "\n" + co : ""),
            };
            var full = name.Length > 0 ? $"{name} <{addr}>" : addr;
            link.ContextMenu = Menu(name, addr, full, field!);
            link.Click += (_, _) => { link.ContextMenu.PlacementTarget = text; link.ContextMenu.IsOpen = true; };
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
            text.Inlines.Add(link);
            if (name.Length > 0 && bold) text.Inlines.Add(new Run($"  <{addr}>") { Foreground = ThemeManager.Brush("TextSecondaryBrush"), FontWeight = FontWeights.Normal, FontSize = fontSize - 1 });
            if (i < people.Count - 1) text.Inlines.Add(new Run("; "));
        }
        text.ContextMenu = AllMenu(field!);
        panel.Children.Add(text);
    }

    static ContextMenu Menu(string name, string addr, string full, string field)
    {
        var m = new ContextMenu();
        void Item(string h, Action a, bool enabled = true) { var i = new MenuItem { Header = h, IsEnabled = enabled }; i.Click += (_, _) => a(); m.Items.Add(i); }
        m.Items.Add(new MenuItem { Header = full, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        m.Items.Add(new Separator());
        Item(L.T("Kopyala (ad ve adres)"), () => Clipboard.SetText(full));
        Item(L.T("Yalnızca e-posta adresini kopyala"), () => Clipboard.SetText(addr));
        Item(L.T("Tüm adresleri kopyala"), () => Clipboard.SetText(AllAddresses(field)));
        m.Items.Add(new Separator());
        Item(L.T("Yeni E-posta"), () => NewMailTo?.Invoke(full + "; "), NewMailTo != null);
        var saved = Store?.FindByEmail(addr);
        Item(saved == null ? L.T("Kişilere ekle") : L.T("Kişiyi düzenle"), () =>
        {
            if (Store == null) return;
            var c = saved ?? new Contact { Email = addr };
            if (saved == null) c.SetName(name);
            if (new ContactEditWindow(c) { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) }.ShowDialog() == true) Store.Save(c);
        }, Store != null);
        Item(L.T("Bu kişinin maillerini bul"), () => FindMailsOf?.Invoke(addr), FindMailsOf != null);
        return m;
    }

    static ContextMenu AllMenu(string field)
    {
        var m = new ContextMenu();
        var i = new MenuItem { Header = L.T("Tüm adresleri kopyala") };
        i.Click += (_, _) => Clipboard.SetText(AllAddresses(field));
        m.Items.Add(i);
        return m;
    }

    static string AllAddresses(string field) => string.Join("; ", ContactStore.SplitAddresses(field).Select(p => p.Name.Length > 0 ? $"{p.Name} <{p.Address}>" : p.Address));
}
