using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MailStudio.Models;
using MailStudio.Services;
using MailStudio.Services.Calendar;

namespace MailStudio.Views;

/// <summary>
/// Contact card displayed when clicking a sender/recipient: shows recent emails, attachments, open tasks, and quotes.
/// </summary>
public class ContactCardWindow : Window
{
    readonly string _name;
    readonly string _address;
    readonly MailCache _cache;
    readonly TaskStore _tasks;
    readonly QuoteStore _quotes;
    readonly ContactStore? _contacts;
    readonly Contact? _contact;

    public string ContactName => _name;
    public string ContactAddress => _address;

    public ContactCardWindow(string name, string address, MailCache cache, TaskStore tasks, QuoteStore quotes, ContactStore? contacts)
    {
        _name = (name ?? "").Trim();
        _address = (address ?? "").Trim();
        _cache = cache;
        _tasks = tasks;
        _quotes = quotes;
        _contacts = contacts;
        _contact = contacts?.FindByEmail(_address);

        Title = L.T("Kişi Kartı") + (string.IsNullOrWhiteSpace(_name) ? $" - {_address}" : $" - {_name}");
        Width = 680;
        Height = 560;
        MinWidth = 540;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.Brush("BgSurfacePrimaryBrush");
        Foreground = ThemeManager.Brush("TextPrimaryBrush");
        FontSize = 13;

        BuildUi();
        UiTranslator.Apply(this);
    }

    void BuildUi()
    {
        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Tabs
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer buttons

        // --- Header Section ---
        var headerBorder = new Border
        {
            Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"),
            BorderBrush = ThemeManager.Brush("BorderSubtleBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 16, 20, 16)
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Initials Avatar
        var initials = GetInitials();
        var avatar = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(26),
            Background = ThemeManager.Brush("AccentHeaderBrush"),
            Margin = new Thickness(0, 0, 16, 0),
            Child = new TextBlock
            {
                Text = initials,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(avatar, 0);
        headerGrid.Children.Add(avatar);

        // Contact Info
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var displayName = !string.IsNullOrWhiteSpace(_name) ? _name : _contact?.DisplayName is { Length: > 0 } cn ? cn : _address;
        infoStack.Children.Add(new TextBlock
        {
            Text = displayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Foreground = ThemeManager.Brush("TextPrimaryBrush")
        });

        infoStack.Children.Add(new TextBlock
        {
            Text = _address,
            Foreground = ThemeManager.Brush("TextSecondaryBrush"),
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 0)
        });

        var companyRole = string.Join(" · ", new[] { _contact?.Company, _contact?.JobTitle }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(companyRole))
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = companyRole,
                Foreground = ThemeManager.Brush("TextSecondaryBrush"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        Grid.SetColumn(infoStack, 1);
        headerGrid.Children.Add(infoStack);

        // Action Buttons
        var actionStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var newMailBtn = new Button
        {
            Content = L.T("Yeni İleti"),
            Style = Application.Current.FindResource("PrimaryButton") as Style,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        newMailBtn.Click += (_, _) =>
        {
            AddressChips.NewMailTo?.Invoke(!string.IsNullOrWhiteSpace(_name) ? $"{_name} <{_address}>; " : $"{_address}; ");
            Close();
        };
        actionStack.Children.Add(newMailBtn);

        var editContactBtn = new Button
        {
            Content = L.T("Kişiyi Düzenle"),
            Style = Application.Current.FindResource("FlatButton") as Style,
            Padding = new Thickness(10, 5, 10, 5)
        };
        editContactBtn.Click += (_, _) =>
        {
            var c = _contact ?? new Contact { Email = _address };
            if (_contact == null && !string.IsNullOrWhiteSpace(_name)) c.SetName(_name);
            var win = new ContactEditWindow(c) { Owner = this };
            if (win.ShowDialog() == true && _contacts != null)
            {
                _contacts.Save(c);
            }
        };
        actionStack.Children.Add(editContactBtn);

        Grid.SetColumn(actionStack, 2);
        headerGrid.Children.Add(actionStack);
        headerBorder.Child = headerGrid;

        Grid.SetRow(headerBorder, 0);
        mainGrid.Children.Add(headerBorder);

        // --- Tabs Section ---
        var tabs = new TabControl
        {
            Margin = new Thickness(16, 12, 16, 8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };

        tabs.Items.Add(BuildEmailsTab());
        tabs.Items.Add(BuildAttachmentsTab());
        tabs.Items.Add(BuildTasksTab());
        tabs.Items.Add(BuildQuotesTab());

        Grid.SetRow(tabs, 1);
        mainGrid.Children.Add(tabs);

        // --- Footer Section ---
        var footerBorder = new Border
        {
            Background = ThemeManager.Brush("BgSurfaceSecondaryBrush"),
            BorderBrush = ThemeManager.Brush("BorderSubtleBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 16, 10)
        };
        var footerDock = new DockPanel();
        var closeBtn = new Button
        {
            Content = L.T("Kapat"),
            IsCancel = true,
            Style = Application.Current.FindResource("MsButtonStyle") as Style,
            Width = 90,
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        footerDock.Children.Add(closeBtn);
        footerBorder.Child = footerDock;

        Grid.SetRow(footerBorder, 2);
        mainGrid.Children.Add(footerBorder);

        Content = mainGrid;
    }

    TabItem BuildEmailsTab()
    {
        var tab = new TabItem { Header = L.T("Son İletiler") };
        var mails = _cache.GetMailsForContact(_address, 25);
        if (mails.Count == 0)
        {
            tab.Content = EmptyNotice(L.T("Bu kişiyle ilişkili ileti bulunamadı."));
            return tab;
        }

        var list = new ListBox
        {
            ItemsSource = mails,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemContainerStyle = MailItemContainerStyle()
        };
        tab.Content = list;
        return tab;
    }

    TabItem BuildAttachmentsTab()
    {
        var tab = new TabItem { Header = L.T("Ekler") };
        var atts = _cache.GetAttachmentsForContact(_address, 25);
        if (atts.Count == 0)
        {
            tab.Content = EmptyNotice(L.T("Bu kişiden gelen veya kişiye gönderilen ek bulunamadı."));
            return tab;
        }

        var list = new ListBox
        {
            ItemsSource = atts,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemContainerStyle = AttachmentItemContainerStyle()
        };
        tab.Content = list;
        return tab;
    }

    TabItem BuildTasksTab()
    {
        var tab = new TabItem { Header = L.T("Açık Görevler") };
        var openTasks = _tasks.Tasks
            .Where(t => !t.Completed && (
                (!string.IsNullOrEmpty(_address) && (t.SourceFrom.Contains(_address, StringComparison.OrdinalIgnoreCase) || t.Notes.Contains(_address, StringComparison.OrdinalIgnoreCase))) ||
                (!string.IsNullOrEmpty(_name) && (t.Title.Contains(_name, StringComparison.OrdinalIgnoreCase) || t.Notes.Contains(_name, StringComparison.OrdinalIgnoreCase)))
            ))
            .ToList();

        if (openTasks.Count == 0)
        {
            tab.Content = EmptyNotice(L.T("Bu kişiyle ilişkili açık görev bulunamadı."));
            return tab;
        }

        var list = new ListBox
        {
            ItemsSource = openTasks,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemContainerStyle = TaskItemContainerStyle()
        };
        tab.Content = list;
        return tab;
    }

    TabItem BuildQuotesTab()
    {
        var tab = new TabItem { Header = L.T("Teklifler") };
        var quotes = _quotes.Quotes
            .Where(q =>
                (!string.IsNullOrEmpty(_address) && ((q.ContactEmail != null && q.ContactEmail.Equals(_address, StringComparison.OrdinalIgnoreCase)) || q.Notes.Contains(_address, StringComparison.OrdinalIgnoreCase))) ||
                (!string.IsNullOrEmpty(_name) && (q.Customer.Contains(_name, StringComparison.OrdinalIgnoreCase) || q.Contact.Contains(_name, StringComparison.OrdinalIgnoreCase))) ||
                (_contact?.Company is { Length: > 0 } comp && q.Customer.Contains(comp, StringComparison.OrdinalIgnoreCase))
            )
            .ToList();

        if (quotes.Count == 0)
        {
            tab.Content = EmptyNotice(L.T("Bu kişiyle veya firmasıyla ilişkili teklif bulunamadı."));
            return tab;
        }

        var list = new ListBox
        {
            ItemsSource = quotes,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ItemContainerStyle = QuoteItemContainerStyle()
        };
        tab.Content = list;
        return tab;
    }

    static UIElement EmptyNotice(string text) =>
        new Border
        {
            Padding = new Thickness(24),
            Child = new TextBlock
            {
                Text = text,
                Foreground = ThemeManager.Brush("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Italic
            }
        };

    string GetInitials()
    {
        var src = !string.IsNullOrWhiteSpace(_name) ? _name : _address;
        var p = src.Split(new[] { ' ', '.', '@', '_' }, StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 0 ? "?" : p.Length == 1 ? p[0][..1].ToUpperInvariant() : (p[0][..1] + p[1][..1]).ToUpperInvariant();
    }

    static Style MailItemContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 0, 4)));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, ThemeManager.Brush("BgSurfaceSecondaryBrush"));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));

        var grid = new FrameworkElementFactory(typeof(Grid));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var c2 = new FrameworkElementFactory(typeof(ColumnDefinition)); c2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(c1); grid.AppendChild(c2);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var subj = new FrameworkElementFactory(typeof(TextBlock));
        subj.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SubjectDisplay"));
        subj.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var prev = new FrameworkElementFactory(typeof(TextBlock));
        prev.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Preview"));
        prev.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextSecondaryBrush"));
        prev.SetValue(TextBlock.FontSizeProperty, 11.5);
        stack.AppendChild(subj); stack.AppendChild(prev);
        grid.AppendChild(stack);

        var date = new FrameworkElementFactory(typeof(TextBlock));
        date.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DateDisplay"));
        date.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextSecondaryBrush"));
        date.SetValue(TextBlock.FontSizeProperty, 11.5);
        date.SetValue(Grid.ColumnProperty, 1);
        grid.AppendChild(date);

        factory.AppendChild(grid);
        template.VisualTree = factory;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    static Style AttachmentItemContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, ThemeManager.Brush("BgSurfaceSecondaryBrush"));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));

        var grid = new FrameworkElementFactory(typeof(Grid));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        var c2 = new FrameworkElementFactory(typeof(ColumnDefinition)); c2.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var c3 = new FrameworkElementFactory(typeof(ColumnDefinition)); c3.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(c1); grid.AppendChild(c2); grid.AppendChild(c3);

        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, "\uE723");
        icon.SetValue(TextBlock.FontFamilyProperty, Application.Current.FindResource("Icons"));
        icon.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(icon);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(Grid.ColumnProperty, 1);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("FileName"));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var subj = new FrameworkElementFactory(typeof(TextBlock));
        subj.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Subject"));
        subj.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextSecondaryBrush"));
        subj.SetValue(TextBlock.FontSizeProperty, 11.5);
        stack.AppendChild(name); stack.AppendChild(subj);
        grid.AppendChild(stack);

        var rightStack = new FrameworkElementFactory(typeof(StackPanel));
        rightStack.SetValue(Grid.ColumnProperty, 2);
        var size = new FrameworkElementFactory(typeof(TextBlock));
        size.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SizeDisplay"));
        size.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextSecondaryBrush"));
        size.SetValue(TextBlock.FontSizeProperty, 11.5);
        var dt = new FrameworkElementFactory(typeof(TextBlock));
        dt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DateDisplay"));
        dt.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextSecondaryBrush"));
        dt.SetValue(TextBlock.FontSizeProperty, 11.0);
        rightStack.AppendChild(size); rightStack.AppendChild(dt);
        grid.AppendChild(rightStack);

        factory.AppendChild(grid);
        template.VisualTree = factory;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    static Style TaskItemContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, ThemeManager.Brush("BgSurfaceSecondaryBrush"));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var notes = new FrameworkElementFactory(typeof(TextBlock));
        notes.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Notes"));
        notes.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextSecondaryBrush"));
        notes.SetValue(TextBlock.FontSizeProperty, 11.5);
        stack.AppendChild(title); stack.AppendChild(notes);

        factory.AppendChild(stack);
        template.VisualTree = factory;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    static Style QuoteItemContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, ThemeManager.Brush("BgSurfaceSecondaryBrush"));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));

        var grid = new FrameworkElementFactory(typeof(Grid));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var c2 = new FrameworkElementFactory(typeof(ColumnDefinition)); c2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(c1); grid.AppendChild(c2);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var part = new FrameworkElementFactory(typeof(TextBlock));
        part.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("PartNumber"));
        part.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var cust = new FrameworkElementFactory(typeof(TextBlock));
        cust.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Customer"));
        cust.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("TextSecondaryBrush"));
        cust.SetValue(TextBlock.FontSizeProperty, 11.5);
        stack.AppendChild(part); stack.AppendChild(cust);
        grid.AppendChild(stack);

        var st = new FrameworkElementFactory(typeof(TextBlock));
        st.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("StatusDisplayName"));
        st.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("AccentHeaderBrush"));
        st.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        st.SetValue(Grid.ColumnProperty, 1);
        grid.AppendChild(st);

        factory.AppendChild(grid);
        template.VisualTree = factory;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
