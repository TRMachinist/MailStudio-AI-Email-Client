using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MailStudio.Services;

namespace MailStudio.Views;

/// <summary>Image attachment: zoom, fit, 1:1, rotate, save as.</summary>
public sealed class ImageAttachmentWindow : Window
{
    readonly Image _image=new(){Stretch=Stretch.Uniform,RenderTransformOrigin=new Point(.5,.5)};
    readonly Slider _zoom=new(){Minimum=10,Maximum=400,Value=100,Width=180,Margin=new Thickness(8,0,8,0),VerticalAlignment=VerticalAlignment.Center};
    readonly TextBlock _zoomText=new(){VerticalAlignment=VerticalAlignment.Center,MinWidth=44,Foreground=ThemeManager.Brush("TextSecondaryBrush")};
    readonly ScrollViewer _scroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=ThemeManager.Brush("BgAppCanvasBrush")};
    readonly TextBlock _info=new(){VerticalAlignment=VerticalAlignment.Center,Foreground=ThemeManager.Brush("TextSecondaryBrush"),Margin=new Thickness(10,0,0,0)};
    BitmapImage? _bitmap;int _angle;readonly string _path;

    public ImageAttachmentWindow(string path)
    {
        _path=path;Title=System.IO.Path.GetFileName(path)+" — Resim";Width=1000;Height=740;MinWidth=520;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());Content=root;
        var bar=new Border{Padding=new Thickness(10,6,10,6),Background=ThemeManager.Brush("BgSurfaceSecondaryBrush"),BorderBrush=ThemeManager.Brush("BorderSubtleBrush"),BorderThickness=new Thickness(0,0,0,1)};root.Children.Add(bar);
        var tools=new WrapPanel{VerticalAlignment=VerticalAlignment.Center};bar.Child=tools;
        Button B(string t,Action a,string? tip=null){var b=new Button{Content=t,Margin=new Thickness(0,0,6,0),MinHeight=28,ToolTip=tip};b.Click+=(_,_)=>a();return b;}
        tools.Children.Add(B(L.T("Sığdır"),Fit,L.T("Pencereye sığdır")));tools.Children.Add(B("1:1",()=>SetZoom(100),L.T("Gerçek boyut")));
        tools.Children.Add(B("−",()=>SetZoom(_zoom.Value/1.25)));tools.Children.Add(_zoom);tools.Children.Add(_zoomText);tools.Children.Add(B("+",()=>SetZoom(_zoom.Value*1.25)));
        tools.Children.Add(B(L.T("90° döndür"),()=>{_angle=(_angle+90)%360;_image.LayoutTransform=new RotateTransform(_angle);}));
        tools.Children.Add(B(L.T("Farklı kaydet…"),SaveAs));tools.Children.Add(_info);
        _scroll.Content=_image;Grid.SetRow(_scroll,1);root.Children.Add(_scroll);
        _zoom.ValueChanged+=(_,_)=>Apply();
        _scroll.PreviewMouseWheel+=(_,e)=>{if(System.Windows.Input.Keyboard.Modifiers!=System.Windows.Input.ModifierKeys.Control)return;SetZoom(e.Delta>0?_zoom.Value*1.15:_zoom.Value/1.15);e.Handled=true;};
        Loaded+=(_,_)=>
        {
            try{var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(path);bitmap.EndInit();bitmap.Freeze();_bitmap=bitmap;_image.Source=bitmap;_info.Text=$"{bitmap.PixelWidth} × {bitmap.PixelHeight} px";Fit();}
            catch(Exception ex){Content=new TextBlock{Text=L.T("Resim açılamadı: ")+ex.Message,Margin=new Thickness(20),TextWrapping=TextWrapping.Wrap};}
        };
    }
    void SetZoom(double v)=>_zoom.Value=Math.Clamp(v,_zoom.Minimum,_zoom.Maximum);
    void Apply(){if(_bitmap==null)return;_image.Width=_bitmap.PixelWidth*_zoom.Value/100;_image.Height=_bitmap.PixelHeight*_zoom.Value/100;_zoomText.Text=$"%{_zoom.Value:0}";}
    void Fit(){if(_bitmap==null)return;var w=Math.Max(50,_scroll.ViewportWidth-24);var h=Math.Max(50,_scroll.ViewportHeight-24);SetZoom(Math.Min(w/_bitmap.PixelWidth,h/_bitmap.PixelHeight)*100);}
    void SaveAs()
    {
        var d=new Microsoft.Win32.SaveFileDialog{FileName=System.IO.Path.GetFileName(_path),Filter="Resim|*"+System.IO.Path.GetExtension(_path)+L.T("|Tüm dosyalar|*.*")};
        if(d.ShowDialog(this)!=true)return;
        try{System.IO.File.Copy(_path,d.FileName,true);_info.Text=L.T("Kaydedildi: ")+d.FileName;}catch(Exception ex){MessageBox.Show(this,ex.Message,L.T("Kaydedilemedi"));}
    }
}
