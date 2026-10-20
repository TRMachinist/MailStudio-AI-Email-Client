using System.Windows;
using System.Windows.Controls;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views;

public class OutboxWindow : Window
{
    readonly OutboxStore _store;
    readonly Action<ComposeRequest, string?> _openCompose;
    readonly Action _runQueue;
    readonly ListBox _list = new();

    /// <param name="runQueue">Runs the outbox now (the app's single sender: no second send path).</param>
    public OutboxWindow(OutboxStore store, Action<ComposeRequest, string?> openCompose, Action runQueue, Window? owner = null)
    {
        _store = store;
        _openCompose = openCompose;
        _runQueue = runQueue;
        Owner = owner;
        UiTranslator.SetSkip(_list, true);
        Action refresh = () => Dispatcher.BeginInvoke(Refresh);
        store.Changed += refresh;
        Closed += (_, _) => store.Changed -= refresh;

        Title = L.T("Giden Kutusu - Zamanlanmış İletiler");
        Width = 760;
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
        var items = _store.Items.OrderBy(i => i.ScheduledTime).ToList();
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

        Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Style = (Style)FindResource("FlatButton"), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2, 8, 2), IsEnabled = item.CanUserAct };
            b.Click += click;
            buttons.Children.Add(b);
            return b;
        }

        Btn(item.Status == OutboxStatus.Uncertain ? L.T("Yine de gönder") : L.T("Şimdi Gönder"), (_, _) =>
        {
            if (item.Status == OutboxStatus.Uncertain &&
                MessageBox.Show(this, L.T("Bu ileti daha önce gönderilirken program kapandı; gitmiş olabilir. Gönderilmiş Öğeler'i kontrol ettiniz mi? Yine de gönderilsin mi?"),
                    "MailStudio", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (item.Status == OutboxStatus.Uncertain) item.Request.SubmissionState = null;
            if (_store.Release(item.Id)) _runQueue();
        });
        Btn(L.T("Düzenle"), (_, _) =>
        {
            if (!_store.Remove(item.Id)) return;
            Close();
            _openCompose(item.Request, item.AccountId);
        });
        Btn(L.T("Sil"), (_, _) =>
        {
            if (MessageBox.Show(this, L.F("\"{0}\" iletisi Giden Kutusu'ndan silinsin mi? (Gönderilmez.)", item.Request.Subject), "MailStudio",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _store.Remove(item.Id);
        });

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

        var state = item.Status switch
        {
            OutboxStatus.Held => L.T("Bekletiliyor – zamanı program kapalıyken geçti"),
            OutboxStatus.Sending => L.T("Gönderiliyor…"),
            OutboxStatus.Failed => L.T("Gönderilemedi: ") + item.Error,
            OutboxStatus.Uncertain => L.T("Sonuç belirsiz: ") + item.Error,
            _ => item.IsUndoSend ? L.T("Geri alma süresi") : L.T("Zamanlandı")
        };
        var info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = L.F("Kime: {0}  ·  {1:g}  ·  {2}", to, item.ScheduledTime.LocalDateTime, state),
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
