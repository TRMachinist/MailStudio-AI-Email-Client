using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// To / Cc field: suggests contacts while typing like Outlook. Addresses are separated by ";".
/// Enter/Tab/click picks the suggestion, ↑↓ navigates, Esc closes.
/// </summary>
public class RecipientBox : Grid
{
    public static Func<string, IEnumerable<ContactInfo>>? Provider { get; set; }

    readonly TextBox _box = new() { MinHeight = 26, Margin = new Thickness(0, 2, 0, 2), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 1, 6, 1) };
    readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    readonly ListBox _list = new() { MaxHeight = 260, MinWidth = 380, BorderBrush = ThemeManager.Brush("BorderStrongBrush"), Background = ThemeManager.Brush("BgPopupBrush"), BorderThickness = new Thickness(1) };

    public RecipientBox()
    {
        Children.Add(_box);
        _popup.PlacementTarget = _box;
        _popup.Child = new Border { Background = ThemeManager.Brush("BgPopupBrush"), Child = _list, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.25 } };
        Children.Add(_popup);

        _list.ItemTemplate = BuildTemplate();
        _box.TextChanged += (_, _) => Refresh();
        _box.PreviewKeyDown += OnKey;
        _box.LostKeyboardFocus += (_, e) => { if (!_list.IsKeyboardFocusWithin) _popup.IsOpen = false; };
        _list.PreviewMouseLeftButtonUp += (_, _) => Accept();
    }

    public string Text
    {
        get => _box.Text;
        set { _box.Text = value; _box.CaretIndex = value.Length; }
    }

    public new bool Focus() => _box.Focus();

    string CurrentToken()
    {
        var t = _box.Text[.._box.CaretIndex];
        var i = Math.Max(t.LastIndexOf(';'), t.LastIndexOf(','));
        return t[(i + 1)..].Trim();
    }

    void Refresh()
    {
        var q = CurrentToken();
        if (q.Length < 1 || Provider == null || q.Contains('@') && q.EndsWith(".com")) { _popup.IsOpen = false; return; }
        var items = Provider(q).ToList();
        _list.ItemsSource = items;
        _popup.IsOpen = items.Count > 0 && _box.IsKeyboardFocused;
        if (items.Count > 0) _list.SelectedIndex = 0;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down: _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); e.Handled = true; break;
            case Key.Up: _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); e.Handled = true; break;
            case Key.Enter or Key.Tab: Accept(); e.Handled = true; break;
            case Key.Escape: _popup.IsOpen = false; e.Handled = true; break;
        }
    }

    void Accept()
    {
        if (_list.SelectedItem is not ContactInfo c) return;
        var caret = _box.CaretIndex;
        var before = _box.Text[..caret];
        var after = _box.Text[caret..];
        var i = Math.Max(before.LastIndexOf(';'), before.LastIndexOf(','));
        var head = i >= 0 ? before[..(i + 1)] + " " : "";
        var restIdx = after.IndexOfAny(new[] { ';', ',' });
        var tail = restIdx >= 0 ? after[(restIdx + 1)..].TrimStart() : "";
        var entry = string.IsNullOrWhiteSpace(c.Name) ? c.Address : $"{c.Name} <{c.Address}>";
        _box.Text = head + entry + "; " + tail;
        _box.CaretIndex = (head + entry + "; ").Length;
        _popup.IsOpen = false;
        _box.Focus();
    }

    static DataTemplate BuildTemplate()
    {
        var f = new FrameworkElementFactory(typeof(StackPanel));
        f.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        f.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));

        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(WidthProperty, 28.0);
        badge.SetValue(HeightProperty, 28.0);
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        badge.SetValue(Border.BackgroundProperty, ThemeManager.Brush("AccentFillBrush"));
        var ini = new FrameworkElementFactory(typeof(TextBlock));
        ini.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Initials"));
        ini.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        ini.SetValue(TextBlock.FontSizeProperty, 11.0);
        ini.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        ini.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.AppendChild(ini);
        f.AppendChild(badge);

        var col = new FrameworkElementFactory(typeof(StackPanel));
        col.SetValue(MarginProperty, new Thickness(8, 0, 0, 0));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var addr = new FrameworkElementFactory(typeof(TextBlock));
        addr.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Address"));
        addr.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextSecondaryBrush"));
        addr.SetValue(TextBlock.FontSizeProperty, 12.0);
        col.AppendChild(name);
        col.AppendChild(addr);
        f.AppendChild(col);
        return new DataTemplate { VisualTree = f };
    }
}
