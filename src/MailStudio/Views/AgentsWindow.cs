using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>
/// One-click connection of MailStudio (MCP server) to the AI agents installed on this computer. Each agent's own
/// config file gets a "mailstudio" entry (a backup is written first). Agents can never send mail.
/// </summary>
public sealed class AgentsWindow : Window
{
    readonly StackPanel _rows = new();
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly string? _mcp = McpLocator.Find();

    public AgentsWindow()
    {
        Title = L.T("Yapay zeka ajanlarına bağlan"); Width = 900; Height = 680; MinWidth = 720; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        var root = new DockPanel();
        Content = root;
        var head = new Border { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 0, 0, 1), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), BorderBrush = ThemeManager.Brush("BorderSubtleBrush") };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var hs = new StackPanel(); head.Child = hs;
        hs.Children.Add(new TextBlock { Text = L.T("Yapay zeka ajanlarına bağlan"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        hs.Children.Add(new TextBlock { Text = L.T("Kullandığınız ajan (Claude, Codex, Gemini, Cursor, VS Code, Windsurf, LM Studio, Goose) MailStudio'yu MCP üzerinden kullanır: mailleri arar ve okur, ekleri (PDF, STEP, DXF, Excel) okur, skill'lerinizi çalıştırır. Yetkiyi her ajan için siz seçersiniz. Hiçbir ajan mail GÖNDEREMEZ."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = ThemeManager.Brush("TextSecondaryBrush") });
        hs.Children.Add(new TextBlock { Text = L.T("Okuma: yalnızca arar ve okur · Taslak: + Taslaklar klasörüne taslak bırakır · Düzenleme: + bayrak, okundu, klasöre taşıma"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = ThemeManager.Brush("TextSecondaryBrush"), FontSize = 11.5 });

        var foot = new StackPanel { Margin = new Thickness(16, 0, 16, 12) }; DockPanel.SetDock(foot, Dock.Bottom); root.Children.Add(foot);
        foot.Children.Add(_status);
        var fb = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; foot.Children.Add(fb);
        fb.Children.Add(Btn(L.T("Yenile"), (_, _) => Fill()));
        fb.Children.Add(Btn(L.T("Elle kurulum (MCP.md)"), (_, _) => OpenDoc()));
        fb.Children.Add(Btn(L.T("Ayar parçasını kopyala"), (_, _) => CopySnippet()));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 10, 16, 10), Content = _rows };
        root.Children.Add(scroll);
        if (_mcp == null) _status.Text = L.T("MailStudio.Mcp.exe bulunamadı. Tam MailStudio paketini kullanın.");
        Fill();
    }

    static Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3), MinWidth = 70 };
        b.Click += click; return b;
    }

    static string PermText(AgentPermission p) => p switch { AgentPermission.Read => L.T("Okuma"), AgentPermission.Organize => L.T("Düzenleme"), _ => L.T("Taslak") };

    void Fill()
    {
        _rows.Children.Clear();
        var agents = AgentConnector.Detect(_mcp);
        foreach (var a in agents.OrderByDescending(a => a.Installed).ThenBy(a => a.Name))
        {
            var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = ThemeManager.Brush("BorderSubtleBrush"), Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"), Opacity = a.Installed ? 1 : .6 };
            var g = new Grid(); card.Child = g;
            g.ColumnDefinitions.Add(new()); g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var left = new StackPanel(); g.Children.Add(left);
            var state = !a.Installed ? L.T("bu bilgisayarda bulunamadı") : a.Connected ? (a.PathOutdated ? L.T("bağlı · MailStudio yeri değişmiş, yeniden bağlayın") : L.T("bağlı") + " · " + PermText(a.Permission)) : L.T("bağlı değil");
            var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
            title.Inlines.Add(new System.Windows.Documents.Run((a.Connected && !a.PathOutdated ? "● " : "○ ") + a.Name) { Foreground = a.Connected && !a.PathOutdated ? new SolidColorBrush(Color.FromRgb(16, 124, 16)) : ThemeManager.Brush("TextPrimaryBrush") });
            title.Inlines.Add(new System.Windows.Documents.Run("  " + state) { FontWeight = FontWeights.Normal, FontSize = 12, Foreground = ThemeManager.Brush("TextSecondaryBrush") });
            left.Children.Add(title);
            left.Children.Add(new TextBlock { Text = a.ConfigPath, FontSize = 11, Foreground = ThemeManager.Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            left.Children.Add(new TextBlock { Text = a.Hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 1); g.Children.Add(right);
            var perm = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var p in Enum.GetValues<AgentPermission>()) perm.Items.Add(new ComboBoxItem { Content = PermText(p), Tag = p });
            perm.SelectedIndex = (int)a.Permission;
            right.Children.Add(perm);
            AgentPermission Chosen() => perm.SelectedItem is ComboBoxItem { Tag: AgentPermission p } ? p : AgentPermission.Draft;
            var agent = a;
            right.Children.Add(Btn(a.Connected ? L.T("Güncelle") : L.T("Bağla"), (_, _) => Connect(agent, Chosen())));
            if (a.Connected) right.Children.Add(Btn(L.T("Kaldır"), (_, _) => { try { AgentConnector.Disconnect(agent); _status.Text = L.F("{0} bağlantısı kaldırıldı.", agent.Name); } catch (Exception ex) { _status.Text = ex.Message; } Fill(); }));
            right.Children.Add(Btn(L.T("Test et"), async (_, _) => await TestAsync(Chosen())));
            foreach (var c in right.Children.OfType<Control>()) c.IsEnabled = _mcp != null;
            _rows.Children.Add(card);
        }
    }

    void Connect(AgentInfo a, AgentPermission p)
    {
        if (_mcp == null) return;
        try
        {
            var backup = AgentConnector.Connect(a, _mcp, p);
            _status.Text = L.F("{0} bağlandı ({1}). {2}", a.Name, PermText(p), a.Hint) + (backup != null ? "\n" + L.F("Önceki ayar dosyası yedeklendi: {0}", backup) : "");
        }
        catch (Exception ex) { _status.Text = L.F("{0} bağlanamadı: {1}", a.Name, ex.Message); }
        Fill();
    }

    async Task TestAsync(AgentPermission p)
    {
        if (_mcp == null) return;
        _status.Text = L.T("MCP sunucusu test ediliyor...");
        var (ok, msg) = await AgentConnector.TestAsync(_mcp, p);
        _status.Text = (ok ? "✓ " : "✗ ") + msg;
    }

    void CopySnippet()
    {
        if (_mcp == null) return;
        var json = "{\n  \"mcpServers\": {\n    \"mailstudio\": {\n      \"command\": " + System.Text.Json.JsonSerializer.Serialize(_mcp) + ",\n      \"args\": [\"--permission\", \"draft\"]\n    }\n  }\n}";
        Clipboard.SetText(json);
        _status.Text = L.T("MCP ayar parçası panoya kopyalandı (mcpServers biçimi).");
    }

    static void OpenDoc()
    {
        var doc = Path.Combine(AppContext.BaseDirectory, "docs", "MCP.md");
        try { Process.Start(new ProcessStartInfo(File.Exists(doc) ? doc : "https://github.com/TRMachinist/MailStudio-AI-Email-Client/blob/main/docs/MCP.md") { UseShellExecute = true }); } catch { }
    }
}
