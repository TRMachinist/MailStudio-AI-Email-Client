using System.Windows;
using System.Windows.Controls;
using MailStudio.Services;

namespace MailStudio.Views;

public enum MissingAttachmentResult
{
    AddAttachment,
    SendAnyway,
    Cancel
}

public static class MissingAttachmentDialog
{
    public static MissingAttachmentResult Show(Window? owner, string keyword = "")
    {
        var result = MissingAttachmentResult.Cancel;
        var dlg = new Window
        {
            Title = L.T("Ek unutulmuş olabilir"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = ThemeManager.Brush("BgSurfacePrimaryBrush"),
            ShowInTaskbar = false
        };

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var icon = new TextBlock
        {
            Text = "📎",
            FontSize = 24,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var title = new TextBlock
        {
            Text = L.T("Eki eklemeyi unuttunuz mu?"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerRow.Children.Add(icon);
        headerRow.Children.Add(title);
        Grid.SetRow(headerRow, 0);
        root.Children.Add(headerRow);

        var msgStack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        var body = new TextBlock
        {
            Text = L.T("İletinizde ekten bahsedilmiş ancak hiçbir dosya eklenmemiş. Yine de gönderilsin mi?"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = ThemeManager.Brush("TextPrimaryBrush"),
            LineHeight = 18
        };
        msgStack.Children.Add(body);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var hint = new TextBlock
            {
                Text = L.F("Tespit edilen ifade: \"{0}\"", keyword),
                FontSize = 11,
                Foreground = ThemeManager.Brush("TextMutedBrush"),
                Margin = new Thickness(0, 6, 0, 0)
            };
            msgStack.Children.Add(hint);
        }
        Grid.SetRow(msgStack, 1);
        root.Children.Add(msgStack);

        var bp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var addBtn = new Button
        {
            Content = L.T("Dosya Ekle"),
            Style = (Style)dlg.FindResource("PrimaryButton"),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        addBtn.Click += (_, _) => { result = MissingAttachmentResult.AddAttachment; dlg.DialogResult = true; dlg.Close(); };

        var sendAnywayBtn = new Button
        {
            Content = L.T("Yine de Gönder"),
            Style = (Style)dlg.FindResource("FlatButton"),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        sendAnywayBtn.Click += (_, _) => { result = MissingAttachmentResult.SendAnyway; dlg.DialogResult = true; dlg.Close(); };

        var cancelBtn = new Button
        {
            Content = L.T("İptal"),
            Style = (Style)dlg.FindResource("FlatButton"),
            Padding = new Thickness(14, 6, 14, 6),
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => { result = MissingAttachmentResult.Cancel; dlg.Close(); };

        bp.Children.Add(addBtn);
        bp.Children.Add(sendAnywayBtn);
        bp.Children.Add(cancelBtn);
        Grid.SetRow(bp, 2);
        root.Children.Add(bp);

        dlg.Content = root;
        UiTranslator.Apply(dlg);
        dlg.ShowDialog();
        return result;
    }
}
