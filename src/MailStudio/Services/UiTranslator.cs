using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace MailStudio.Services;

/// <summary>
/// Translates the static texts of a XAML-defined window (Text, Content, Header, ToolTip, Title, column headers, menus).
/// Call right after InitializeComponent, before any user content is shown. Bound values are never touched.
/// </summary>
public static class UiTranslator
{
    static bool _global;

    /// <summary>
    /// Static texts inside DataTemplates / ControlTemplates (e.g. buttons in chat bubbles) are created later than the window;
    /// they are translated when they load. Bound values and user-entered text are never touched.
    /// </summary>
    static void EnsureGlobal()
    {
        if (_global) return; _global = true;
        var h = new RoutedEventHandler((s, _) => { if (s is DependencyObject d) Translate(d); });
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), FrameworkElement.LoadedEvent, h);
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, h);
        EventManager.RegisterClassHandler(typeof(Label), FrameworkElement.LoadedEvent, h);
        EventManager.RegisterClassHandler(typeof(MenuItem), FrameworkElement.LoadedEvent, h);
        EventManager.RegisterClassHandler(typeof(HeaderedContentControl), FrameworkElement.LoadedEvent, h);
    }

    public static void Apply(DependencyObject root)
    {
        if (!L.IsEnglish || root == null) return;
        EnsureGlobal();
        var seen = new HashSet<DependencyObject>();
        Walk(root, seen);
    }

    static void Walk(DependencyObject o, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(o)) return;
        Translate(o);
        if (o is FrameworkElement fe)
        {
            if (fe.ContextMenu != null) Walk(fe.ContextMenu, seen);
            if (fe.ToolTip is DependencyObject tip) Walk(tip, seen);
        }
        if (o is DataGrid dg)
            foreach (var c in dg.Columns) if (c.Header is string h) c.Header = L.T(h);
        if (o is ListView { View: GridView gv })
            foreach (var c in gv.Columns) if (c.Header is string h) c.Header = L.T(h);
        if (o is ItemsControl ic && ic is not TreeView)
        {
            if (ic.ItemsSource == null)
                for (int i = 0; i < ic.Items.Count; i++)
                    if (ic.Items[i] is string s) { var t = L.T(s); if (!ReferenceEquals(t, s) && t != s) ic.Items[i] = t; }
        }
        if (o is TextBlock tb && !Bound(tb, TextBlock.TextProperty) && tb.Inlines.Count > 1)
            foreach (var inl in tb.Inlines.ToList()) Walk(inl, seen);
        if (o is Span span) foreach (var inl in span.Inlines.ToList()) Walk(inl, seen);
        foreach (var child in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>().ToList()) Walk(child, seen);
    }

    static bool Bound(DependencyObject o, DependencyProperty p) => BindingOperations.GetBindingExpressionBase(o, p) != null;

    static void Set(DependencyObject o, DependencyProperty p)
    {
        if (Bound(o, p)) return;
        // Local values (XAML attributes / code) and values set by a DataTemplate or ControlTemplate (ParentTemplate).
        var source = DependencyPropertyHelper.GetValueSource(o, p).BaseValueSource;
        if (source is not (BaseValueSource.Local or BaseValueSource.ParentTemplate)) return;
        if (o.GetValue(p) is string s && s.Length > 0) { var t = L.T(s); if (t != s) o.SetValue(p, t); }
    }

    static void Translate(DependencyObject o)
    {
        switch (o)
        {
            case Window: Set(o, Window.TitleProperty); break;
            case TextBlock tb: if (tb.Inlines.Count <= 1) Set(o, TextBlock.TextProperty); break;
            case Run: Set(o, Run.TextProperty); break;
            case HeaderedItemsControl: Set(o, HeaderedItemsControl.HeaderProperty); break;
            case HeaderedContentControl: Set(o, HeaderedContentControl.HeaderProperty); Set(o, ContentControl.ContentProperty); break;
            case ContentControl: Set(o, ContentControl.ContentProperty); break;
        }
        if (o is FrameworkElement) Set(o, FrameworkElement.ToolTipProperty);
        if (o is MenuItem) Set(o, MenuItem.InputGestureTextProperty);
    }
}
