using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MailStudio.Views;

/// <summary>Separate message window opened by double-click (like classic Outlook).</summary>
public class MessageWindow : Window
{
    readonly WebView2 _web = new() { DefaultBackgroundColor = ThemeManager.IsDark ? System.Drawing.Color.FromArgb(0x29, 0x29, 0x29) : System.Drawing.Color.White };
    bool _closed;

    public MessageWindow(MailHeader h, MailContent c, bool blockRemote,
                         Action reply, Action replyAll, Func<Task<bool>> forward, Func<Task<bool>> delete, Func<AttachmentInfo, Task> openAttachment)
    {
        Title = h.SubjectDisplay + L.T(" - İleti");
        Width = 980; Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current.MainWindow?.Icon != null) Icon = Application.Current.MainWindow.Icon;

        Button Btn(string icon, string text, Action a)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Icons"), Margin = new Thickness(0, 1, 6, 0), Foreground = (Brush)FindResource("AccentPrimaryBrush") });
            sp.Children.Add(new TextBlock { Text = text });
            var b = new Button { Content = sp, Style = (Style)FindResource("FlatButton") };
            b.Click += (_, _) => a();
            return b;
        }

        var toolbar = new WrapPanel { Margin = new Thickness(12, 10, 12, 4) };
        toolbar.Children.Add(Btn("", L.T("Yanıtla"), () => { reply(); Close(); }));
        toolbar.Children.Add(Btn("", L.T("Tümünü Yanıtla"), () => { replyAll(); Close(); }));
        var working = false;
        async void Run(Func<Task<bool>> operation)
        {
            if (working) return;
            working = true; toolbar.IsEnabled = false;
            try { if (await operation()) Close(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("İleti")); }
            finally { working = false; toolbar.IsEnabled = true; }
        }
        toolbar.Children.Add(Btn("", L.T("İlet"), () => Run(forward)));
        toolbar.Children.Add(Btn("", L.T("Sil"), () => Run(delete)));
        toolbar.Children.Add(Btn("", L.T("Yazdır"), () => _ = _web.CoreWebView2?.ExecuteScriptAsync("window.print()")));
        var allowImages=!blockRemote;
        if(InlineImages.HasRemote(c.Html))toolbar.Children.Add(Btn("",L.T("Resimleri yükle"),()=>{allowImages=true;if(_web.CoreWebView2!=null)MailHtml.Show(_web,c.Html);}));

        var header = new StackPanel { Margin = new Thickness(18, 4, 18, 8) };
        header.Children.Add(new TextBlock { Text = h.SubjectDisplay, FontSize = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        header.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(h.FromName) ? h.FromAddress : $"{h.FromName} <{h.FromAddress}>", FontSize = 14 });
        header.Children.Add(new TextBlock { Text = L.T("Kime: ") + h.To, Foreground = ThemeManager.Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(c.Cc)) header.Children.Add(new TextBlock { Text = L.T("Bilgi: ") + c.Cc, Foreground = ThemeManager.Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(new TextBlock { Text = h.Date.ToLocalTime().ToString(L.T("dd.MM.yyyy dddd HH:mm")), Foreground = ThemeManager.Brush("TextSecondaryBrush") });

        var atts = new WrapPanel { Margin = new Thickness(18, 0, 18, 6) };
        foreach (var a in c.Attachments)
        {
            var att = a;
            atts.Children.Add(Btn("", $"{att.FileName}  ({att.SizeDisplay})", () => _ = openAttachment(att)));
        }

        var grid = new Grid();
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(toolbar);
        Grid.SetRow(header, 1); grid.Children.Add(header);
        Grid.SetRow(atts, 2); grid.Children.Add(atts);
        var border = new Border { BorderBrush = new SolidColorBrush(ThemeManager.Color("BorderSubtleBrush")), BorderThickness = new Thickness(0, 1, 0, 0), Child = _web };
        Grid.SetRow(border, 3); grid.Children.Add(border);
        Content = grid;

        Loaded += async (_, _) =>
        {
            try
            {
            await _web.EnsureCoreWebView2Async(await MailHtml.EnvAsync());
            if(_closed)return;
            var core = _web.CoreWebView2;
            core.Profile.PreferredColorScheme = ThemeManager.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            core.Settings.IsScriptEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, a) =>
            {
                if (!allowImages && a.Request.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    a.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            core.NavigationStarting += (s, a) =>
            {
                if (a.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    a.Cancel = true;
                    Process.Start(new ProcessStartInfo(a.Uri) { UseShellExecute = true });
                }
            };
            core.NewWindowRequested += (s, a) => { a.Handled = true; Process.Start(new ProcessStartInfo(a.Uri) { UseShellExecute = true }); };
            MailHtml.Show(_web, c.Html);
            }
            catch(Exception ex){if(!_closed)header.Children.Add(new TextBlock{Text=L.T("İleti görünümü açılamadı: ")+ex.Message,TextWrapping=TextWrapping.Wrap});}
        };
        Closed+=(_,_)=>{_closed=true;_web.Dispose();};
    }
}
