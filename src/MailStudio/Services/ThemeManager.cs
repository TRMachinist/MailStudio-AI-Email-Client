using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MailStudio.Services;

public enum ThemeMode { System, Light, Dark }

/// <summary>Switches the light/dark theme dictionary at runtime; WebView2 and CAD surfaces follow via ThemeChanged.</summary>
public static class ThemeManager
{
    static ResourceDictionary? _active;
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; }
    public static event Action? ThemeChanged;

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        var dark = mode switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => SystemPrefersDark() };
        var app = Application.Current;
        if (app == null) return;
        var dict = new ResourceDictionary { Source = new Uri(dark ? "pack://application:,,,/MailStudio;component/Themes/Dark.xaml" : "pack://application:,,,/MailStudio;component/Themes/Light.xaml", UriKind.Absolute) };
        var merged = app.Resources.MergedDictionaries;
        if (_active != null) merged.Remove(_active);
        foreach (var old in merged.Where(d => d.Source != null && (d.Source.OriginalString.EndsWith("Light.xaml") || d.Source.OriginalString.EndsWith("Dark.xaml"))).ToList()) merged.Remove(old);
        merged.Insert(0, dict);
        _active = dict;
        IsDark = dark;
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        ThemeChanged?.Invoke();
    }

    public static ThemeMode Parse(string? value) => Enum.TryParse<ThemeMode>(value, true, out var m) ? m : ThemeMode.System;

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>Matches the window title bar to the theme (Windows 10 20H1+).</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) { window.SourceInitialized += (_, _) => ApplyTitleBar(window); return; }
            int dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
        catch { }
    }

    public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    public static Color Color(string key) => Brush(key) is SolidColorBrush s ? s.Color : Colors.Transparent;
    public static string Hex(string key) { var c = Color(key); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
