using System.Windows;
using System.Windows.Controls;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views;

public class OutboxWindow : Window
{
    readonly OutboxStore _store;
    readonly Action<ComposeRequest, string?> _openCompose;
    readonly Func<AccountConfig, ComposeRequest, Task> _sendNow;
    readonly Func<string, AccountConfig?> _getAccount;
    readonly ListBox _list = new();

    public OutboxWindow(OutboxStore store, Action<ComposeRequest, string?> openCompose,
        Func<AccountConfig, ComposeRequest, Task> sendNow, Func<string, AccountConfig?> getAccount, Window? owner = null)
    {
        _store = store;
        _openCompose = openCompose;
        _sendNow = sendNow;
        _getAccount = getAccount;
        Owner = owner;

        Title = L.T("Giden Kutusu - Zamanlanmış İletiler");
        Width = 640;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = L.T("Zamanlanmış İletiler"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = ThemeManager.Brush("TextPrimaryBrush")
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        _list.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_list, 1);
        grid.Children.Add(_list);

        var bottomBar = new DockPanel();
        var closeBtn = new Button
        {
            Content = L.T("Kapat"),
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(14, 4, 14, 4),
            IsCancel = true
        };
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        bottomBar.Children.Add(closeBtn);

        Grid.SetRow(bottomBar, 2);
        grid.Children.Add(bottomBar);

        Content = grid;
        Refresh();
        UiTranslator.Apply(this);
    }

    public void Refresh()
    {
        _list.Items.Clear();
        var items = _store.Items.Where(i => !i.IsUndoSend).OrderBy(i => i.ScheduledTime).ToList();
        if (items.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = L.T("Zamanlanmış ileti yok."),
                Foreground = ThemeManager.Brush("TextSecondaryBrush"),
                Margin = new Thickness(10)
            };
            _list.Items.Add(empty);
            return;
        }

        foreach (var item in items)
        {
            _list.Items.Add(BuildItemRow(item));
        }
    }

    UIElement BuildItemRow(OutboxItem item)
    {
        var border = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = ThemeManager.Brush("BorderSubtleBrush")
        };
        var dock = new DockPanel();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);

        var sendBtn = new Button
        {
            Content = L.T("Şimdi Gönder"),
            Style = (Style)FindResource("FlatButton"),
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 2, 8, 2)
        };
        sendBtn.Click += async (_, _) =>
        {
            _store.Remove(item.Id);
            Refresh();
            var acc = _getAccount(item.AccountId);
            if (acc != null) await _sendNow(acc, item.Request);
        };
        buttons.Children.Add(sendBtn);

        var editBtn = new Button
        {
            Content = L.T("Düzenle"),
            Style = (Style)FindResource("FlatButton"),
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 2, 8, 2)
        };
        editBtn.Click += (_, _) =>
        {
            _store.Remove(item.Id);
            Close();
            _openCompose(item.Request, item.AccountId);
        };
        buttons.Children.Add(editBtn);

        var deleteBtn = new Button
        {
            Content = L.T("Sil"),
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(8, 2, 8, 2)
        };
        deleteBtn.Click += (_, _) =>
        {
            if (MessageBox.Show(this, L.F("\"{0}\" zamanlanmış iletisi silinsin mi?", item.Request.Subject), "MailStudio",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _store.Remove(item.Id);
                Refresh();
            }
        };
        buttons.Children.Add(deleteBtn);

        dock.Children.Add(buttons);

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var subj = string.IsNullOrWhiteSpace(item.Request.Subject) ? L.T("(Konu yok)") : item.Request.Subject;
        var to = string.IsNullOrWhiteSpace(item.Request.To) ? L.T("(Alıcı yok)") : item.Request.To;

        var titleBlock = new TextBlock
        {
            Text = subj,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = ThemeManager.Brush("TextPrimaryBrush")
        };
        sp.Children.Add(titleBlock);

        var info = new TextBlock
        {
            Text = L.F("Kime: {0}  ·  Zamanlandı: {1:g}", to, item.ScheduledTime.LocalDateTime),
            FontSize = 11.5,
            Foreground = ThemeManager.Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        sp.Children.Add(info);

        dock.Children.Add(sp);
        border.Child = dock;
        return border;
    }
}
