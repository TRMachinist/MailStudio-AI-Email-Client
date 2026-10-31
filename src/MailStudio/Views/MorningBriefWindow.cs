using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MailStudio.Services;
using MailStudio.Views.Calendar;

namespace MailStudio.Views;

/// <summary>
/// The day at a glance: appointments, due tasks, quote deadlines, unanswered mails and the new mail sorted into
/// requests for quote, revisions and orders. Built from local data only; clicking a line opens the mail / quote /
/// task / appointment it is about.
/// </summary>
public sealed class MorningBriefWindow : Window
{
    readonly Action<BriefItem> _open;
    public MorningBrief Brief { get; }

    static readonly Dictionary<string, (string Glyph, string Brush)> Look = new()
    {
        ["calendar"] = ("", "AccentPrimaryBrush"), ["tasks"] = ("", "AccentPrimaryBrush"), ["quotes"] = ("", "AccentPrimaryBrush"),
        ["followups"] = ("", "StatusDangerBrush"), ["rfq"] = ("", "AccentPrimaryBrush"), ["revisions"] = ("", "AccentPrimaryBrush"),
        ["orders"] = ("", "AccentPrimaryBrush"), ["other"] = ("", "TextSecondaryBrush"),
    };

    public MorningBriefWindow(MorningBrief brief, Action<BriefItem> open)
    {
        Brief = brief; _open = open;
        UiTranslator.SetSkip(this, true);
        Title = L.T("Sabah özeti");
        Width = 860; Height = 820; MinWidth = 620; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var head = new StackPanel { Margin = new Thickness(22, 18, 22, 6) };
        var hour = DateTime.Now.Hour;
        head.Children.Add(PimUi.Text(hour < 12 ? L.T("Günaydın") : hour < 18 ? L.T("İyi günler") : L.T("İyi akşamlar"), 24, bold: true));
        head.Children.Add(PimUi.Text(brief.Day.ToString("D", L.Culture) + "  ·  " + L.F("{0:g} sonrası {1} yeni mail, {2} okunmamış", brief.Since.LocalDateTime, brief.NewMails, brief.UnreadMails), 12.5, brush: "TextSecondaryBrush"));

        var body = new StackPanel { Margin = new Thickness(22, 6, 22, 12) };
        if (brief.IsEmpty)
            body.Children.Add(PimUi.Text(L.T("Bugün için bekleyen bir şey yok: randevu, geciken görev, teklif son tarihi veya yeni önemli mail bulunmadı."), 13, brush: "TextMutedBrush"));
        foreach (var s in brief.Sections.Where(s => s.Items.Count > 0)) body.Children.Add(Section(s));

        var foot = new DockPanel { Margin = new Thickness(22, 8, 22, 14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var copy = new Button { Content = L.T("Panoya kopyala"), Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0) };
        copy.SetResourceReference(StyleProperty, "RibbonCmd"); copy.Click += (_, _) => { try { Clipboard.SetText(brief.ToText()); } catch { } };
        var close = new Button { Content = L.T("Kapat"), MinWidth = 90, Height = 30, IsCancel = true, IsDefault = true };
        close.SetResourceReference(StyleProperty, "RibbonPrimaryCmd"); close.Click += (_, _) => Close();
        buttons.Children.Add(copy); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right); foot.Children.Add(buttons);
        var auto = new CheckBox { Content = L.T("Her gün ilk açılışta göster"), IsChecked = AppSettings.Shared.MorningBriefAuto, VerticalAlignment = VerticalAlignment.Center };
        auto.Click += (_, _) => { AppSettings.Shared.MorningBriefAuto = auto.IsChecked == true; try { AppSettings.Shared.Save(); } catch { } };
        foot.Children.Add(auto);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    FrameworkElement Section(BriefSection s)
    {
        var (glyph, brush) = Look.TryGetValue(s.Key, out var l) ? l : ("", "AccentPrimaryBrush");
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        card.SetResourceReference(Border.BackgroundProperty, "BgSurfaceSecondaryBrush");
        var sp = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        title.Children.Add(icon);
        title.Children.Add(PimUi.Text($"{s.Title}  ({s.Items.Count})", 14, bold: true));
        sp.Children.Add(title);
        foreach (var item in s.Items.Take(12))
        {
            var row = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var g = new StackPanel();
            var t = PimUi.Text((item.Urgent ? "● " : "") + item.Title, 13, bold: item.Urgent, brush: item.Urgent ? "StatusDangerBrush" : "TextPrimaryBrush");
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            g.Children.Add(t);
            g.Children.Add(PimUi.Text(item.Detail, 11.5, brush: "TextSecondaryBrush"));
            row.Child = g;
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "BgListHoverBrush");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => _open(item);
            row.ToolTip = item.Mail != null ? L.T("Tıkla: maili aç") : item.Quote != null ? L.T("Tıkla: Teklif Masası'nda göster") : item.Task != null ? L.T("Tıkla: görevlerde göster") : item.Event != null ? L.T("Tıkla: takvimde göster") : null;
            sp.Children.Add(row);
        }
        if (s.Items.Count > 12) sp.Children.Add(PimUi.Text(L.F("… ve {0} tane daha", s.Items.Count - 12), 11.5, brush: "TextMutedBrush"));
        card.Child = sp;
        return card;
    }
}
