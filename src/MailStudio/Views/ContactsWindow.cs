using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;
using Microsoft.Win32;

namespace MailStudio.Views;

/// <summary>
/// Address book (Outlook "Contacts" + "Select Names"). Saved contacts and groups, plus every address collected from mail.
/// pickMode: opened from the To/Cc/Bcc buttons of a new mail — the chosen names go to the To, Cc and Bcc fields.
/// </summary>
public class ContactsWindow : Window
{
    public static ContactBook? Book { get; set; }
    public static Action<ContactInfo>? NewMailTo { get; set; }
    public static Action<string>? FindMailsOf { get; set; }

    public enum Target { To, Cc, Bcc }

    /// <summary>Kept for callers that only need the To list.</summary>
    public List<ContactInfo> Picked { get; } = new();
    public string PickedTo => _to.Text.Trim().TrimEnd(';');
    public string PickedCc => _cc.Text.Trim().TrimEnd(';');
    public string PickedBcc => _bcc.Text.Trim().TrimEnd(';');

    readonly bool _pick;
    readonly Target _default;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0, 1, 0, 0) };
    readonly TextBox _search = new() { MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 2, 8, 2) };
    readonly ComboBox _filter = new() { Width = 190, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _count = new() { Foreground = ThemeManager.Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _to = new(), _cc = new(), _bcc = new();

    public ContactsWindow(bool pickMode, Target defaultTarget = Target.To)
    {
        _pick = pickMode; _default = defaultTarget;
        Title = pickMode ? L.T("Ad Seç: Kişiler") : L.T("Kişiler - MailStudio");
        Width = 900; Height = pickMode ? 720 : 640; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        if (Application.Current.MainWindow?.Icon != null) Icon = Application.Current.MainWindow.Icon;

        var view = new GridView();
        view.Columns.Add(Col(L.T("Ad"), 220, "Name"));
        view.Columns.Add(Col(L.T("E-posta"), 250, "Address"));
        view.Columns.Add(Col(L.T("Şirket"), 150, "Company"));
        view.Columns.Add(Col(L.T("Telefon"), 120, "Phone"));
        view.Columns.Add(Col(L.T("Yazışma"), 70, "Count"));
        _list.View = view;
        _list.ItemContainerStyle = RowStyle();

        foreach (var f in new[] { L.T("Tümü"), L.T("Kayıtlı kişiler"), L.T("Gruplar"), L.T("Maillerden toplananlar") }) _filter.Items.Add(f);
        _filter.SelectedIndex = 0;

        var top = new DockPanel { Margin = new Thickness(12, 12, 12, 8) };
        var label = new TextBlock { Text = L.T("Ara:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(_count, Dock.Right); _count.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(_filter, Dock.Right);
        top.Children.Add(label); top.Children.Add(_count); top.Children.Add(_filter); top.Children.Add(_search);

        var tools = new WrapPanel { Margin = new Thickness(12, 0, 12, 8) };
        tools.Children.Add(Btn(L.T("Yeni kişi"), (_, _) => EditContact(null)));
        tools.Children.Add(Btn(L.T("Yeni grup"), (_, _) => EditGroup(null)));
        tools.Children.Add(Btn(L.T("Düzenle"), (_, _) => EditSelected()));
        tools.Children.Add(Btn(L.T("Sil"), (_, _) => DeleteSelected()));
        tools.Children.Add(Btn(L.T("İçe aktar (CSV / vCard)..."), (_, _) => Import()));
        tools.Children.Add(Btn(L.T("Dışa aktar..."), (_, _) => Export()));

        var bottom = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (pickMode)
        {
            foreach (var (t, box) in new[] { (Target.To, _to), (Target.Cc, _cc), (Target.Bcc, _bcc) })
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var b = new Button { Content = t switch { Target.To => L.T("Kime →"), Target.Cc => L.T("Bilgi →"), _ => L.T("Gizli →") }, Width = 90, Style = (Style)FindResource("FlatButton"), Margin = new Thickness(0, 0, 8, 0) };
                var target = t; b.Click += (_, _) => AddSelected(target);
                DockPanel.SetDock(b, Dock.Left);
                box.MinHeight = 26; box.VerticalContentAlignment = VerticalAlignment.Center; box.Padding = new Thickness(6, 1, 6, 1);
                row.Children.Add(b); row.Children.Add(box);
                bottom.Children.Add(row);
            }
            var ok = new Button { Content = L.T("Tamam"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
            ok.Click += (_, _) => Accept();
            buttons.Children.Add(ok);
        }
        else
        {
            var mail = new Button { Content = L.T("Yeni E-posta"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(14, 4, 14, 4) };
            mail.Click += (_, _) => { if (_list.SelectedItem is ContactInfo c) NewMailTo?.Invoke(c); };
            buttons.Children.Add(Btn(L.T("Adresi kopyala"), (_, _) => Copy(false)));
            buttons.Children.Add(mail);
        }
        var close = new Button { Content = pickMode ? L.T("İptal") : L.T("Kapat"), Style = (Style)FindResource("FlatButton"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        buttons.Children.Add(close);
        bottom.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(tools, Dock.Top);
        var footer = new Border { Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(tools); root.Children.Add(footer); root.Children.Add(_list);
        Content = root;

        _list.ContextMenu = BuildMenu();
        _search.TextChanged += (_, _) => Fill();
        _filter.SelectionChanged += (_, _) => Fill();
        _list.MouseDoubleClick += (_, _) => { if (_pick) AddSelected(_default); else EditSelected(); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) DeleteSelected(); if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) Copy(true); };
        if (Book?.Store != null) Book.Store.Changed += OnStoreChanged;
        Closed += (_, _) => { if (Book?.Store != null) Book.Store.Changed -= OnStoreChanged; };
        Loaded += (_, _) => { Fill(); _search.Focus(); };
    }

    void OnStoreChanged() => Dispatcher.InvokeAsync(Fill);

    static GridViewColumn Col(string header, double width, string path) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };

    static Style RowStyle()
    {
        var s = new Style(typeof(ListViewItem));
        var saved = new DataTrigger { Binding = new System.Windows.Data.Binding("Saved"), Value = true };
        saved.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        var group = new DataTrigger { Binding = new System.Windows.Data.Binding("IsGroup"), Value = true };
        group.Setters.Add(new Setter(ForegroundProperty, ThemeManager.Brush("AccentPrimaryBrush")));
        group.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        s.Triggers.Add(saved); s.Triggers.Add(group);
        return s;
    }

    Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("FlatButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += click; return b;
    }

    ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string h, Action a) { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); m.Items.Add(i); return i; }
        Item(L.T("Yeni E-posta"), () => { if (_list.SelectedItem is ContactInfo c) NewMailTo?.Invoke(c); });
        Item(L.T("Kopyala (ad ve adres)"), () => Copy(true));
        Item(L.T("Yalnızca e-posta adresini kopyala"), () => Copy(false));
        m.Items.Add(new Separator());
        Item(L.T("Kişilere kaydet / düzenle"), EditSelected);
        var toGroup = Item(L.T("Gruba ekle"), () => { });
        Item(L.T("Bu kişinin maillerini bul"), () => { if (_list.SelectedItem is ContactInfo { IsGroup: false } c) { FindMailsOf?.Invoke(c.Address); Close(); } });
        m.Items.Add(new Separator());
        Item(L.T("Sil"), DeleteSelected);
        m.Opened += (_, _) =>
        {
            toGroup.Items.Clear();
            foreach (var g in Book?.Store?.Groups ?? (IReadOnlyList<ContactGroup>)Array.Empty<ContactGroup>())
            {
                var gi = new MenuItem { Header = g.Name }; var grp = g;
                gi.Click += (_, _) => AddToGroup(grp);
                toGroup.Items.Add(gi);
            }
            var ng = new MenuItem { Header = L.T("Yeni grup...") };
            ng.Click += (_, _) => EditGroup(null, Selected().Where(c => !c.IsGroup).Select(c => c.Display));
            toGroup.Items.Add(ng);
        };
        return m;
    }

    List<ContactInfo> Selected() => _list.SelectedItems.OfType<ContactInfo>().ToList();

    void Fill()
    {
        var all = Book?.All ?? (IReadOnlyList<ContactInfo>)Array.Empty<ContactInfo>();
        var q = _search.Text.Trim();
        IEnumerable<ContactInfo> items = q.Length == 0 ? all : Book!.Suggest(q, 5000);
        items = _filter.SelectedIndex switch
        {
            1 => items.Where(c => c.Saved),
            2 => items.Where(c => c.IsGroup),
            3 => items.Where(c => !c.Saved && !c.IsGroup),
            _ => items,
        };
        var list = items.ToList();
        _list.ItemsSource = list;
        _count.Text = L.F("{0} kişi", list.Count);
    }

    void Copy(bool withName)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        Clipboard.SetText(string.Join("; ", sel.Select(c => withName || c.IsGroup ? c.Display : c.Address)));
    }

    void AddSelected(Target t)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var box = t switch { Target.Cc => _cc, Target.Bcc => _bcc, _ => _to };
        var cur = box.Text.Trim().TrimEnd(';');
        box.Text = (cur.Length > 0 ? cur + "; " : "") + string.Join("; ", sel.Select(c => c.Display)) + "; ";
        if (t == Target.To) Picked.AddRange(sel);
    }

    void Accept()
    {
        if (PickedTo.Length + PickedCc.Length + PickedBcc.Length == 0) AddSelected(_default);
        DialogResult = PickedTo.Length + PickedCc.Length + PickedBcc.Length > 0;
    }

    void EditSelected()
    {
        if (_list.SelectedItem is not ContactInfo c) { EditContact(null); return; }
        if (c.IsGroup) EditGroup(c.Group);
        else if (c.Contact != null) EditContact(c.Contact);
        else EditContact(null, c.Name, c.Address);
    }

    void EditContact(Contact? existing, string? name = null, string? address = null)
    {
        if (Book?.Store == null) return;
        var c = existing ?? new Contact { Email = address ?? "" };
        if (existing == null && name != null) c.SetName(name);
        if (new ContactEditWindow(c) { Owner = this }.ShowDialog() == true) Book.Store.Save(c);
    }

    void EditGroup(ContactGroup? existing, IEnumerable<string>? members = null)
    {
        if (Book?.Store == null) return;
        var g = existing ?? new ContactGroup();
        if (members != null) g.Members.AddRange(members.Where(m => !g.Members.Contains(m)));
        if (new GroupEditWindow(g) { Owner = this }.ShowDialog() == true) Book.Store.Save(g);
    }

    void AddToGroup(ContactGroup g)
    {
        foreach (var c in Selected().Where(c => !c.IsGroup))
            if (!g.Members.Any(m => m.Contains(c.Address, StringComparison.OrdinalIgnoreCase))) g.Members.Add(c.Display);
        Book?.Store?.Save(g);
    }

    void DeleteSelected()
    {
        var store = Book?.Store; if (store == null) return;
        var sel = Selected().Where(c => c.Saved || c.IsGroup).ToList();
        if (sel.Count == 0) { if (Selected().Count > 0) MessageBox.Show(this, L.T("Maillerden toplanan adresler silinemez; yalnızca kayıtlı kişiler ve gruplar silinir."), Title); return; }
        if (MessageBox.Show(this, L.F("{0} kayıt silinsin mi?", sel.Count), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var c in sel) { if (c.Group != null) store.Delete(c.Group); else if (c.Contact != null) store.Delete(c.Contact); }
    }

    void Import()
    {
        var store = Book?.Store; if (store == null) return;
        var dlg = new OpenFileDialog { Filter = L.T("Kişi dosyaları") + " (*.csv;*.vcf)|*.csv;*.vcf|CSV (*.csv)|*.csv|vCard (*.vcf)|*.vcf", Title = L.T("Kişileri içe aktar") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var (added, updated) = store.Import(dlg.FileName);
            MessageBox.Show(this, L.F("{0} kişi eklendi, {1} kişi güncellendi.", added, updated), L.T("Kişileri içe aktar"));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Kişileri içe aktar"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void Export()
    {
        var store = Book?.Store; if (store == null) return;
        var dlg = new SaveFileDialog { Filter = "CSV (Outlook / Excel) (*.csv)|*.csv|vCard (*.vcf)|*.vcf", FileName = L.T("Kişiler"), Title = L.T("Kişileri dışa aktar") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase)) store.ExportVCard(dlg.FileName); else store.ExportCsv(dlg.FileName);
            MessageBox.Show(this, L.F("{0} kişi dışa aktarıldı.", store.Contacts.Count), L.T("Kişileri dışa aktar"));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Kişileri dışa aktar"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

/// <summary>Contact card editor.</summary>
public class ContactEditWindow : Window
{
    readonly Contact _c;
    readonly Dictionary<string, TextBox> _boxes = new();

    public ContactEditWindow(Contact c)
    {
        _c = c;
        Title = c.DisplayName.Length > 0 ? c.DisplayName : L.T("Yeni kişi");
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new());
        var fields = new (string key, string label, string value)[]
        {
            ("first", L.T("Adı"), c.FirstName), ("last", L.T("Soyad"), c.LastName), ("company", L.T("Şirket"), c.Company), ("title", L.T("Unvan"), c.JobTitle),
            ("email", L.T("E-posta"), c.Email), ("email2", L.T("E-posta 2"), c.Email2), ("email3", L.T("E-posta 3"), c.Email3),
            ("phone", L.T("İş telefonu"), c.Phone), ("mobile", L.T("Cep telefonu"), c.Mobile), ("notes", L.T("Notlar"), c.Notes),
        };
        foreach (var (key, label, value) in fields)
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var r = grid.RowDefinitions.Count - 1;
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
            var b = new TextBox { Text = value, MinHeight = 26, Margin = new Thickness(0, 4, 0, 4), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 1, 6, 1) };
            if (key == "notes") { b.AcceptsReturn = true; b.TextWrapping = TextWrapping.Wrap; b.MinHeight = 70; b.VerticalContentAlignment = VerticalAlignment.Top; }
            Grid.SetRow(l, r); Grid.SetRow(b, r); Grid.SetColumn(b, 1);
            grid.Children.Add(l); grid.Children.Add(b);
            _boxes[key] = b;
        }
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = L.T("Kaydet"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
        var cancel = new Button { Content = L.T("İptal"), Style = (Style)FindResource("FlatButton"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        ok.Click += (_, _) => Save();
        bar.Children.Add(ok); bar.Children.Add(cancel);
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(bar, grid.RowDefinitions.Count - 1); Grid.SetColumnSpan(bar, 2);
        grid.Children.Add(bar);
        Content = grid;
        Loaded += (_, _) => _boxes["first"].Focus();
    }

    void Save()
    {
        string V(string k) => _boxes[k].Text.Trim();
        if (!V("email").Contains('@') && !V("email2").Contains('@') && V("first").Length + V("last").Length == 0)
        { MessageBox.Show(this, L.T("En az bir ad ya da e-posta adresi girin."), Title); return; }
        _c.FirstName = V("first"); _c.LastName = V("last"); _c.Company = V("company"); _c.JobTitle = V("title");
        _c.Email = V("email"); _c.Email2 = V("email2"); _c.Email3 = V("email3"); _c.Phone = V("phone"); _c.Mobile = V("mobile"); _c.Notes = _boxes["notes"].Text.Trim();
        DialogResult = true;
    }
}

/// <summary>Contact group (distribution list) editor.</summary>
public class GroupEditWindow : Window
{
    public GroupEditWindow(ContactGroup g)
    {
        Title = g.Name.Length > 0 ? g.Name : L.T("Yeni grup");
        Width = 560; Height = 460; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        var name = new TextBox { Text = g.Name, MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 4, 0, 10) };
        var members = new TextBox { Text = string.Join(";\n", g.Members), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(6) };
        var root = new DockPanel { Margin = new Thickness(16) };
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top);
        head.Children.Add(new TextBlock { Text = L.T("Grup adı") });
        head.Children.Add(name);
        head.Children.Add(new TextBlock { Text = L.T("Üyeler (her satıra bir adres; noktalı virgülle de ayırabilirsiniz)"), Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(head);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        var add = new Button { Content = L.T("Kişilerden ekle..."), Style = (Style)FindResource("FlatButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        add.Click += (_, _) =>
        {
            var w = new ContactsWindow(true) { Owner = this };
            if (w.ShowDialog() == true)
            {
                var all = string.Join("; ", new[] { w.PickedTo, w.PickedCc, w.PickedBcc }.Where(s => s.Length > 0));
                members.Text = (members.Text.Trim().TrimEnd(';') + (members.Text.Trim().Length > 0 ? ";\n" : "") + all.Replace("; ", ";\n")).Trim();
            }
        };
        var ok = new Button { Content = L.T("Kaydet"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
        var cancel = new Button { Content = L.T("İptal"), Style = (Style)FindResource("FlatButton"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            var list = members.Text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(m => m.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (name.Text.Trim().Length == 0 || list.Count == 0) { MessageBox.Show(this, L.T("Grup adı ve en az bir e-posta adresi gerekli."), Title); return; }
            g.Name = name.Text.Trim(); g.Members = list; DialogResult = true;
        };
        bar.Children.Add(add); bar.Children.Add(ok); bar.Children.Add(cancel);
        root.Children.Add(bar);
        root.Children.Add(members);
        Content = root;
        Loaded += (_, _) => name.Focus();
    }
}
