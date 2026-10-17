using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// Contacts (like Outlook's Contacts / Address Book). Built automatically from mails.
/// pickMode: when opened from the "To..." button the selected contacts are added to the recipient field.
/// </summary>
public class ContactsWindow : Window
{
    public static ContactBook? Book { get; set; }
    public static Action<ContactInfo>? NewMailTo { get; set; }

    public List<ContactInfo> Picked { get; } = new();
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0, 1, 0, 0) };
    readonly TextBox _search = new() { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 2, 8, 2) };
    readonly TextBlock _count = new() { Foreground = ThemeManager.Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };

    public ContactsWindow(bool pickMode)
    {
        Title = pickMode ? L.T("Ad Seç: Kişiler") : L.T("Kişiler - MailStudio");
        Width = 720; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current.MainWindow?.Icon != null) Icon = Application.Current.MainWindow.Icon;

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = L.T("Ad"), Width = 240, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
        view.Columns.Add(new GridViewColumn { Header = L.T("E-posta"), Width = 280, DisplayMemberBinding = new System.Windows.Data.Binding("Address") });
        view.Columns.Add(new GridViewColumn { Header = L.T("Yazışma"), Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding("Count") });
        view.Columns.Add(new GridViewColumn { Header = L.T("Son"), Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Last") { StringFormat = "dd.MM.yyyy" } });
        _list.View = view;

        var top = new DockPanel { Margin = new Thickness(12, 12, 12, 8) };
        var label = new TextBlock { Text = L.T("Ara:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(_count, Dock.Right);
        _count.Margin = new Thickness(10, 0, 0, 0);
        top.Children.Add(label);
        top.Children.Add(_count);
        top.Children.Add(_search);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 10, 12, 10) };
        if (pickMode)
        {
            var ok = new Button { Content = L.T("Ekle"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
            ok.Click += (_, _) => Accept();
            bottom.Children.Add(ok);
        }
        else
        {
            var mail = new Button { Content = L.T("Yeni E-posta"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(14, 4, 14, 4) };
            mail.Click += (_, _) => { if (_list.SelectedItem is ContactInfo c) NewMailTo?.Invoke(c); };
            var copy = new Button { Content = L.T("Adresi kopyala"), Style = (Style)FindResource("FlatButton"), Padding = new Thickness(10, 4, 10, 4) };
            copy.Click += (_, _) => { var sel = _list.SelectedItems.OfType<ContactInfo>().Select(c => c.Display).ToList(); if (sel.Count > 0) Clipboard.SetText(string.Join("; ", sel)); };
            bottom.Children.Add(copy);
            bottom.Children.Add(mail);
        }
        var close = new Button { Content = pickMode ? L.T("İptal") : L.T("Kapat"), Style = (Style)FindResource("FlatButton"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true, Margin = new Thickness(6, 0, 0, 4) };
        bottom.Children.Add(close);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        var footer = new Border { Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(_list);
        Content = root;

        _search.TextChanged += (_, _) => Fill();
        _list.MouseDoubleClick += (_, _) => { if (pickMode) Accept(); else if (_list.SelectedItem is ContactInfo c) NewMailTo?.Invoke(c); };
        Loaded += (_, _) => { Fill(); _search.Focus(); };
    }

    void Fill()
    {
        var all = Book?.All ?? (IReadOnlyList<ContactInfo>)Array.Empty<ContactInfo>();
        var q = _search.Text.Trim();
        var items = q.Length == 0 ? all.ToList() : Book!.Suggest(q, 500).ToList();
        _list.ItemsSource = items;
        _count.Text = L.F("{0} kişi", items.Count);
    }

    void Accept()
    {
        Picked.AddRange(_list.SelectedItems.OfType<ContactInfo>());
        DialogResult = Picked.Count > 0;
    }
}
