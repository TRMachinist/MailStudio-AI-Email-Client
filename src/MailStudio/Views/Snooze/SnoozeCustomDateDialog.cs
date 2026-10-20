using System;
using System.Windows;
using System.Windows.Controls;
using MailStudio.Services;

namespace MailStudio.Views.Snooze;

public sealed class SnoozeCustomDateDialog : Window
{
    readonly Calendar.DateTimeField _dateTimeField;
    public DateTimeOffset? SelectedTime { get; private set; }

    public SnoozeCustomDateDialog(DateTimeOffset? initial = null)
    {
        Title = L.T("Maili Ertele");
        Width = 400;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgSurfacePrimaryBrush");

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title / description
        var title = new TextBlock
        {
            Text = L.T("İletinin yeniden gösterileceği tarih ve saati seçin:"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        // Custom DateTime Field
        var initVal = (initial ?? SnoozeStore.GetTomorrowMorning()).LocalDateTime;
        _dateTimeField = new Calendar.DateTimeField(initVal, showTime: true)
        {
            Margin = new Thickness(0, 0, 0, 20)
        };

        // Presets buttons
        var presetsPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 16) };

        void AddPreset(string label, Func<DateTimeOffset> calc)
        {
            var btn = new Button
            {
                Content = label,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 2)
            };
            btn.SetResourceReference(StyleProperty, "FlatButton");
            btn.Click += (_, _) =>
            {
                var dt = calc();
                _dateTimeField.Value = dt.LocalDateTime;
            };
            presetsPanel.Children.Add(btn);
        }

        AddPreset(L.T("Bugün akşam (18:00)"), () => SnoozeStore.GetThisEvening());
        AddPreset(L.T("Yarın sabah (09:00)"), () => SnoozeStore.GetTomorrowMorning());
        AddPreset(L.T("Gelecek hafta (Pazartesi 09:00)"), () => SnoozeStore.GetNextWeek());

        Grid.SetRow(presetsPanel, 1);
        root.Children.Add(presetsPanel);

        Grid.SetRow(_dateTimeField, 2);
        root.Children.Add(_dateTimeField);

        // Buttons
        var btnBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var okBtn = new Button
        {
            Content = L.T("Ertele"),
            Width = 90,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        okBtn.SetResourceReference(StyleProperty, "PrimaryButton");
        okBtn.Click += (_, _) =>
        {
            SelectedTime = new DateTimeOffset(_dateTimeField.Value);
            try { if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) DialogResult = true; } catch { }
            Close();
        };

        var cancelBtn = new Button
        {
            Content = L.T("İptal"),
            Width = 90,
            Height = 32,
            IsCancel = true
        };
        cancelBtn.SetResourceReference(StyleProperty, "FlatButton");
        cancelBtn.Click += (_, _) =>
        {
            try { if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) DialogResult = false; } catch { }
            Close();
        };

        btnBar.Children.Add(okBtn);
        btnBar.Children.Add(cancelBtn);
        Grid.SetRow(btnBar, 4);
        root.Children.Add(btnBar);

        Content = root;
        UiTranslator.Apply(this);
    }

    public static DateTimeOffset? Show(Window? owner, DateTimeOffset? initial = null)
    {
        var dlg = new SnoozeCustomDateDialog(initial);
        if (owner != null && owner.IsVisible) dlg.Owner = owner;
        var res = dlg.ShowDialog();
        return res == true ? dlg.SelectedTime : null;
    }
}
