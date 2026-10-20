using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MailStudio.Models;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>Classic Outlook "Desktop Alert": a new mail card that appears at the bottom right and fades after a few seconds.</summary>
public class DesktopAlert : Window
{
    static readonly List<DesktopAlert> Open = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };

    public DesktopAlert(MailHeader h, int extraCount, Action onClick)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Hand;
        FontFamily = (FontFamily)Application.Current.FindResource("FontPrimary"); FontSize = 13; Foreground = ThemeManager.Brush("TextPrimaryBrush");

        var close = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 10, Foreground = ThemeManager.Brush("TextMutedBrush"),
                                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0) };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; FadeClose(); };

        var text = new StackPanel { Margin = new Thickness(12, 0, 6, 0) };
        text.Children.Add(new TextBlock { Text = h.FromDisplay, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = h.SubjectDisplay, Foreground = ThemeManager.Brush("AccentPrimaryBrush"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        text.Children.Add(new TextBlock { Text = h.Preview, Foreground = ThemeManager.Brush("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        if (extraCount > 0)
            text.Children.Add(new TextBlock { Text = L.F("+{0} yeni ileti daha", extraCount), Foreground = ThemeManager.Brush("TextMutedBrush"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });

        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(6), Background = ThemeManager.Brush("AccentFillBrush"), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 20, Foreground = Brushes.White,
                                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(close, 2); grid.Children.Add(close);

        Content = new Border
        {
            Background = ThemeManager.Brush("BgPopupBrush"), BorderBrush = ThemeManager.Brush("BorderStrongBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.22 },
            Child = grid
        };

        MouseLeftButtonUp += (_, _) => { onClick(); FadeClose(); };
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();
        _timer.Tick += (_, _) => FadeClose();
        Loaded += (_, _) => Position();
        Closed += (_, _) => { Open.Remove(this); Restack(); };
    }

    /// <summary>At most this many cards are stacked; older ones make room so the stack never runs off the screen.</summary>
    const int MaxOpen = 3;

    public void ShowAlert()
    {
        while (Open.Count >= MaxOpen) { var oldest = Open[0]; Open.RemoveAt(0); oldest._timer.Stop(); oldest.Close(); }
        Open.Add(this);
        Opacity = 0;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        _timer.Start();
    }

    void Position()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 4;
        var idx = Open.IndexOf(this);
        double bottom = wa.Bottom;
        for (int i = 0; i < idx; i++) bottom -= Open[i].ActualHeight;
        Top = Math.Max(wa.Top, bottom - ActualHeight);
    }

    static void Restack() { foreach (var a in Open) a.Position(); }

    void FadeClose()
    {
        _timer.Stop();
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }
}
