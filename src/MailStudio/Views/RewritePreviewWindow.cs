using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>Shows the AI suggestion side by side with the current text; numbers/dimensions are highlighted and Apply is disabled if they changed.</summary>
public sealed class RewritePreviewWindow : Window
{
    static readonly Regex NumberRx = new(@"[-+]?\d+(?:[.,]\d+)*(?:\s*(?:mm|cm|m|kg|g|adet|pcs|dakika|dk|min|saat|hrs?|hours?|sn|usd|eur|tl|%|°))?(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public RewritePreviewWindow(Window owner, string original, string suggestion, bool protectNumbers, bool allowUnitTranslation=false)
    {
        Owner=owner;Title=L.T("AI önerisini incele");Width=960;Height=620;MinWidth=640;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var preserved=allowUnitTranslation?DraftRewrite.PreservesNumbers(original,suggestion):DraftRewrite.PreservesTechnicalValues(original,suggestion);
        var blocked=protectNumbers&&!preserved;

        var grid=new Grid();Content=grid;
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new(){Height=GridLength.Auto});

        // Title strip
        var head=new Border{Padding=new Thickness(16,12,16,12),BorderThickness=new Thickness(0,0,0,1),Background=ThemeManager.Brush("BgSurfaceSecondaryBrush"),BorderBrush=ThemeManager.Brush("BorderSubtleBrush")};
        var headStack=new StackPanel();head.Child=headStack;grid.Children.Add(head);
        headStack.Children.Add(new TextBlock{Text=L.T("AI önerisini incele"),FontSize=16,FontWeight=FontWeights.SemiBold});
        headStack.Children.Add(new TextBlock{Text=L.T("Öneri yalnızca Uygula ile taslağa aktarılır. İmza ve alıntı korunur. İşaretli değerler sayı, ölçü ve adet bilgileridir."),Margin=new Thickness(0,4,0,0),TextWrapping=TextWrapping.Wrap,Foreground=ThemeManager.Brush("TextSecondaryBrush")});

        var columns=new Grid{Margin=new Thickness(16,12,16,0)};columns.ColumnDefinitions.Add(new());columns.ColumnDefinitions.Add(new(){Width=new GridLength(12)});columns.ColumnDefinitions.Add(new());Grid.SetRow(columns,1);grid.Children.Add(columns);
        var originalNumbers=NumberRx.Matches(original).Select(m=>Norm(m.Value)).ToList();
        var suggestionNumbers=NumberRx.Matches(suggestion).Select(m=>Norm(m.Value)).ToList();
        columns.Children.Add(Pane(L.T("Mevcut metin"),original,0,originalNumbers,suggestionNumbers,false,null));
        var badge=blocked?L.T("Sayısal sapma algılandı"):preserved?L.T("Sayılar birebir korundu"):L.T("Birim çevrildi; sayılar korundu");
        columns.Children.Add(Pane(L.T("Önerilen metin"),suggestion,2,suggestionNumbers,originalNumbers,true,badge));

        // Sabit alt bar
        var footer=new Border{Padding=new Thickness(16,10,16,10),BorderThickness=new Thickness(0,1,0,0),Background=ThemeManager.Brush("BgSurfaceSecondaryBrush"),BorderBrush=ThemeManager.Brush("BorderSubtleBrush")};
        Grid.SetRow(footer,2);grid.Children.Add(footer);
        var footerDock=new DockPanel();footer.Child=footerDock;
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Right);footerDock.Children.Add(buttons);
        var cancel=new Button{Content=L.T("Vazgeç"),IsCancel=true,Margin=new Thickness(0,0,8,0),MinWidth=90};
        var apply=new Button{Content=blocked?L.T("Uygulanamaz"):L.T("Uygula"),IsDefault=!blocked,IsEnabled=!blocked,MinWidth=110,Style=(Style)FindResource("MsPrimaryButtonStyle")};apply.Click+=(_,_)=>DialogResult=true;
        buttons.Children.Add(cancel);buttons.Children.Add(apply);
        var note=new TextBlock{VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,12,0)};
        if(blocked){note.Text=L.T("Öneri sayı/ölçü/adet değerlerini değiştirmiş. Düzeltme uygulanamaz; metin korunuyor.");note.Foreground=ThemeManager.Brush("StatusDangerBrush");note.FontWeight=FontWeights.SemiBold;}
        else if(!preserved){note.Text=L.T("Sayı ve ölçüler değişmiş görünüyor; uygulamadan önce kontrol edin.");note.Foreground=ThemeManager.Brush("StatusWarningBrush");}
        else{note.Text=L.T("Sayı ve ölçüler değişmedi.");note.Foreground=ThemeManager.Brush("StatusSuccessBrush");}
        footerDock.Children.Add(note);
    }

    static string Norm(string v)=>Regex.Replace(v,@"\s+","").ToLowerInvariant();

    FrameworkElement Pane(string title,string text,int col,List<string> mine,List<string> other,bool isSuggestion,string? badge)
    {
        var card=new Border{BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Background=ThemeManager.Brush("BgSurfacePrimaryBrush"),BorderBrush=ThemeManager.Brush("BorderSubtleBrush"),Margin=new Thickness(0,0,0,12)};
        Grid.SetColumn(card,col);
        var dock=new DockPanel();card.Child=dock;
        var header=new DockPanel{Margin=new Thickness(12,10,12,6)};DockPanel.SetDock(header,Dock.Top);dock.Children.Add(header);
        if(badge!=null)
        {
            var danger=badge==L.T("Sayısal sapma algılandı");var warn=badge==L.T("Birim çevrildi; sayılar korundu");
            var b=new Border{CornerRadius=new CornerRadius(3),Padding=new Thickness(8,2,8,2),Background=ThemeManager.Brush(danger?"StatusDangerBgBrush":warn?"StatusWarningBgBrush":"StatusSuccessBgBrush"),BorderThickness=new Thickness(1),BorderBrush=ThemeManager.Brush(danger?"StatusDangerBorderBrush":warn?"StatusWarningBorderBrush":"BorderSubtleBrush"),
                Child=new TextBlock{Text=badge,FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=ThemeManager.Brush(danger?"StatusDangerBrush":warn?"StatusWarningBrush":"StatusSuccessBrush")}};
            DockPanel.SetDock(b,Dock.Right);header.Children.Add(b);
            if(danger)card.BorderBrush=ThemeManager.Brush("StatusDangerBorderBrush");
        }
        header.Children.Add(new TextBlock{Text=title,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        var tb=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12,0,12,12),FontSize=13.5,LineHeight=21};
        var remaining=new List<string>(other);
        int last=0;
        foreach(Match m in NumberRx.Matches(text))
        {
            if(m.Index>last)tb.Inlines.Add(new Run(text[last..m.Index]));
            var key=Norm(m.Value);var matched=remaining.Remove(key);
            var run=new Run(m.Value){FontWeight=FontWeights.SemiBold};
            if(matched)run.Background=ThemeManager.Brush("StatusSuccessBgBrush");
            else{run.Background=ThemeManager.Brush("StatusDangerBgBrush");run.Foreground=ThemeManager.Brush("StatusDangerBrush");if(!isSuggestion)run.TextDecorations=TextDecorations.Strikethrough;}
            tb.Inlines.Add(run);last=m.Index+m.Length;
        }
        if(last<text.Length)tb.Inlines.Add(new Run(text[last..]));
        dock.Children.Add(new ScrollViewer{Content=tb,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        return card;
    }
}
