using MailStudio.Services;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using static MailStudio.Views.CadSceneGeometry;

namespace MailStudio.Views;

public sealed partial class StepAttachmentWindow
{
    sealed record StockRequest(string stock_type,int[] body_ids,string orientation,double[] direction,double[] x_direction,double[] origin,double[] box_offsets,double[] cylinder_offsets,double uniform_offset);
    readonly ComboBox _stockScope=new(){ItemsSource=new[]{L.T("Tüm montaj"),L.T("Görünür gövdeler"),L.T("Seçili bileşen / gövdeler")},SelectedIndex=0,Margin=new Thickness(0,4,0,6)};
    readonly ComboBox _stockOrientation=new(){ItemsSource=new[]{L.T("Dünya koordinat sistemi (WCS)"),L.T("Yüz / kenar / orijin ile CSYS"),L.T("En iyi uyum (OCCT yönlü kutu)")},SelectedIndex=0,Margin=new Thickness(0,4,0,6)};
    readonly ListBox _stockObjects=new(){MinHeight=30,MaxHeight=90};readonly HashSet<int> _stockBodies=new();
    readonly TextBox _xDirection=new(){Text="1, 0, 0",Margin=new Thickness(4)},_origin=new(){Text="0, 0, 0",Margin=new Thickness(4)};
    readonly TextBlock _stockSummary=new(){Text=L.T("Henüz stok oluşturulmadı."),FontSize=16,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(9)};
    readonly TextBlock _stockState=new(){Text=L.T("Geometri ve koordinat sistemini seçin."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(9,0,9,8)},_frameSummary=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3)};
    readonly CheckBox _autoPreview=new(){Content=L.T("Değişiklikte otomatik önizleme"),IsChecked=true,Margin=new Thickness(0,7,0,7)},_showStockDimensions=new(){Content=L.T("Boyutları kutu / silindir üzerinde göster"),IsChecked=true,Margin=new Thickness(0,5,0,5)},_solidStock=new(){Content=L.T("Yarı saydam katı önizleme"),Margin=new Thickness(0,5,0,5)};
    readonly Canvas _stockDimensionCanvas=new(){IsHitTestVisible=false,ClipToBounds=true},_stockLabelCanvas=new(){IsHitTestVisible=false,ClipToBounds=true};
    readonly List<(Point3D a,Point3D b,string text,Brush color)> _stockDimensions=new();
    readonly DispatcherTimer _previewTimer=new(){Interval=TimeSpan.FromMilliseconds(400)};
    bool _updatingStock,_collectStock;string _framePick="z";
    object? _lastStockArgs,_appliedStockArgs;JsonElement? _lastStockResponse;bool _lastStockCylinder;
    FrameworkElement BoundingControls()
    {
        var root=new DockPanel();var summary=new StackPanel{Background=ThemeManager.Brush("StatusWarningBgBrush")};summary.Children.Add(_stockSummary);summary.Children.Add(_stockState);summary.Children.Add(MaterialPanel());DockPanel.SetDock(summary,Dock.Top);root.Children.Add(summary);
        var panel=new StackPanel{Margin=new Thickness(10)};root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        void Heading(string text)=>panel.Children.Add(new TextBlock{Text=text,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,9,0,2)});
        Heading(L.T("1 · Geometri kapsamı"));panel.Children.Add(_stockScope);var select=new WrapPanel();select.Children.Add(Button(L.T("Nesneleri seç"),()=>{_stockScope.SelectedIndex=2;_collectStock=true;_mode.SelectedIndex=3;_status.Text=L.T("Kutunun kapsayacağı bileşen/gövdeleri montaj ağacından veya parçadan seçin.");}));select.Children.Add(Button(L.T("Montaj seçimini kullan"),()=>{var ids=CurrentBodies();_stockBodies.Clear();_stockBodies.UnionWith(ids);_stockScope.SelectedIndex=2;RefreshStockObjects();ScheduleStockPreview();}));select.Children.Add(Button(L.T("Grubu temizle"),()=>{_stockBodies.Clear();RefreshStockObjects();ScheduleStockPreview();}));panel.Children.Add(select);panel.Children.Add(_stockObjects);
        Heading(L.T("2 · Koordinat sistemi / yön"));panel.Children.Add(_stockOrientation);
        var originRow=new DockPanel();originRow.Children.Add(new TextBlock{Text=L.T("Orijin"),Width=45,VerticalAlignment=VerticalAlignment.Center});originRow.Children.Add(_origin);panel.Children.Add(originRow);var xRow=new DockPanel();xRow.Children.Add(new TextBlock{Text=L.T("X yönü"),Width=45,VerticalAlignment=VerticalAlignment.Center});xRow.Children.Add(_xDirection);panel.Children.Add(xRow);
        panel.Children.Add(new TextBlock{Text=L.T("Z yönü üstteki Yön alanıdır. X ve Z dikleştirilerek sağ elli koordinat sistemi oluşturulur."),TextWrapping=TextWrapping.Wrap,Foreground=ThemeManager.Brush("TextSecondaryBrush")});
        var pick=new WrapPanel();pick.Children.Add(Button(L.T("Z yönü seç"),()=>BeginFramePick("z")));pick.Children.Add(Button(L.T("X yönü seç"),()=>BeginFramePick("x")));pick.Children.Add(Button(L.T("Orijin seç"),()=>BeginFramePick("origin")));panel.Children.Add(pick);panel.Children.Add(new Expander{Header=L.T("Hesaplanan CSYS ayrıntısı"),Content=_frameSummary});
        Heading(L.T("3 · Kutu yüz payları (mm)"));
        for(int i=0;i<3;i++){var row=new WrapPanel();foreach(int index in new[]{i*2,i*2+1}){row.Children.Add(new TextBlock{Text=new[]{"−X","+X","−Y","+Y","−Z","+Z"}[index],VerticalAlignment=VerticalAlignment.Center});row.Children.Add(_boxOffsets[index]);}panel.Children.Add(row);}
        panel.Children.Add(Button(L.T("Kutu önizle / güncelle"),async()=>await ExecuteAsync(()=>EnvelopeAsync(false))));
        Heading(L.T("Silindir payları (mm)"));for(int i=0;i<3;i++){var row=new WrapPanel();row.Children.Add(new TextBlock{Text=new[]{L.T("Radyal"),L.T("−Z uç"),L.T("+Z uç")}[i],Width=70,VerticalAlignment=VerticalAlignment.Center});row.Children.Add(_cylinderOffsets[i]);panel.Children.Add(row);}panel.Children.Add(Button(L.T("Silindir önizle / güncelle"),async()=>await ExecuteAsync(()=>EnvelopeAsync(true))));
        Heading(L.T("4 · Tüm stoğa genel ofset (mm)"));var general=new WrapPanel();general.Children.Add(_stockOffset);general.Children.Add(Button(L.T("Stoğa uygula"),async()=>await ExecuteAsync(async()=>{if(_stockCylinder is not {} type)throw new InvalidOperationException(L.T("Önce kutu veya silindir önizleyin."));await EnvelopeAsync(type);})));panel.Children.Add(general);
        panel.Children.Add(new TextBlock{Text=L.T("Genel ofset bütün kutu yüzlerine, silindir yarıçapına ve iki ucuna eklenir. Girilen toplam ofsettir; tıklamalarla birikmez."),TextWrapping=TextWrapping.Wrap,Foreground=ThemeManager.Brush("TextSecondaryBrush")});panel.Children.Add(_autoPreview);panel.Children.Add(_showStockDimensions);panel.Children.Add(_solidStock);
        var actions=new WrapPanel();actions.Children.Add(Button(L.T("Uygula"),async()=>await ExecuteAsync(ApplyStockAsync)));actions.Children.Add(Button(L.T("Düzenlemeyi iptal"),async()=>await ExecuteAsync(CancelStockAsync)));actions.Children.Add(Button(L.T("Stoğu kaldır"),ClearStock));actions.Children.Add(Button(L.T("STEP olarak kaydet"),async()=>await ExecuteAsync(ExportStockAsync)));panel.Children.Add(actions);
        _stockScope.SelectionChanged+=(_,_)=>{if(_updatingStock)return;_collectStock=_stockScope.SelectedIndex==2;RefreshStockObjects();ScheduleStockPreview();};
        _stockOrientation.SelectionChanged+=(_,_)=>{if(_updatingStock)return;if(_stockOrientation.SelectedIndex==0){_updatingStock=true;_direction.Text="0, 0, 1";_xDirection.Text="1, 0, 0";_origin.Text="0, 0, 0";_updatingStock=false;}ScheduleStockPreview();};
        foreach(var box in _boxOffsets.Concat(_cylinderOffsets).Concat(new[]{_stockOffset}))box.TextChanged+=(_,_)=>ScheduleStockPreview();
        foreach(var box in new[]{_direction,_xDirection,_origin})box.TextChanged+=(_,_)=>{if(_updatingStock||!_loaded)return;_updatingStock=true;_stockOrientation.SelectedIndex=1;_updatingStock=false;ScheduleStockPreview();};
        _showStockDimensions.Checked+=(_,_)=>UpdateStockLabels();_showStockDimensions.Unchecked+=(_,_)=>UpdateStockLabels();_solidStock.Checked+=(_,_)=>RedrawLastStock();_solidStock.Unchecked+=(_,_)=>RedrawLastStock();
        _previewTimer.Tick+=async(_,_)=>{_previewTimer.Stop();if(_stockCylinder is {} type&&_autoPreview.IsChecked==true)await ExecuteAsync(()=>EnvelopeAsync(type));};return root;
    }
    double[] OriginValues(){var values=_origin.Text.Split(new[]{',',';',' '},StringSplitOptions.RemoveEmptyEntries).Select(v=>double.Parse(v,CultureInfo.InvariantCulture)).ToArray();if(values.Length!=3||values.Any(v=>!double.IsFinite(v)))throw new FormatException(L.T("Orijin için üç sonlu koordinat girin."));return values;}
    void BeginFramePick(string target){_framePick=target;_directionPick=true;_directionMode.SelectedIndex=target=="x"?2:0;_collectStock=false;_viewport.Cursor=System.Windows.Input.Cursors.Cross;_status.Text=target=="origin"?L.T("Koordinat sistemi orijini için köşe veya yüz noktası seçin."):target.ToUpperInvariant()+L.T(" yönü için uygun yüz / kenar seçin.");}
    void SetFrameZ(double[] values)
    {
        var z=new Vector3D(values[0],values[1],values[2]);z.Normalize();double[] raw;try{raw=Triple(_xDirection.Text);}catch{raw=new[]{1d,0,0};}var x=new Vector3D(raw[0],raw[1],raw[2]);
        if(Math.Abs(Vector3D.DotProduct(x,z)/x.Length)>.99)x=new[]{new Vector3D(1,0,0),new Vector3D(0,1,0),new Vector3D(0,0,1)}.MinBy(v=>Math.Abs(Vector3D.DotProduct(v,z)));
        x-=z*Vector3D.DotProduct(x,z);x.Normalize();_updatingStock=true;_direction.Text=FormatVector(new[]{z.X,z.Y,z.Z});_xDirection.Text=FormatVector(new[]{x.X,x.Y,x.Z});_stockOrientation.SelectedIndex=1;_updatingStock=false;ScheduleStockPreview();
    }
    static string FormatVector(double[] d)=>string.Join(", ",d.Select(x=>(Math.Abs(x)<1e-10?0:x).ToString("0.#########",CultureInfo.InvariantCulture)));
    void CollectStock(Selection selected){var ids=BodiesOf(selected).ToArray();if(ids.All(_stockBodies.Contains))_stockBodies.ExceptWith(ids);else _stockBodies.UnionWith(ids);_stockScope.SelectedIndex=2;RefreshStockObjects();ScheduleStockPreview();_tabs.SelectedIndex=1;Recolor();}
    void RefreshStockObjects(){_stockObjects.ItemsSource=StockBodyIds(false).Select(id=>_bodyRecords[id].GetProperty("name").GetString()).ToArray();}
    int[] StockBodyIds(bool require=true)
    {
        var ids=(_stockScope.SelectedIndex==2?_stockBodies:_bodyRecords.Keys.Where(id=>_stockScope.SelectedIndex!=1||!_hiddenBodies.Contains(id))).OrderBy(id=>id).ToArray();if(require&&ids.Length==0)throw new InvalidOperationException(L.T("Stok için en az bir bileşen / gövde seçin."));return ids;
    }
    void ScheduleStockPreview(){if(!_loaded||_closed||_updatingStock)return;_stockVersion++;_stockState.Text=L.T("Değişiklik var; önizleme güncelleniyor.");_previewTimer.Stop();if(_stockCylinder!=null&&_autoPreview.IsChecked==true)_previewTimer.Start();}
    StockRequest StockArguments(bool cylinder)=>new(cylinder?"cylinder":"box",StockBodyIds(),_stockOrientation.SelectedIndex==0?"world":_stockOrientation.SelectedIndex==2?"auto":"custom",Triple(_direction.Text),Triple(_xDirection.Text),OriginValues(),_boxOffsets.Select(t=>Number(t.Text)).ToArray(),_cylinderOffsets.Select(t=>Number(t.Text)).ToArray(),Number(_stockOffset.Text));
    async Task UpdateStockAsync(bool cylinder)
    {
        _previewTimer.Stop();int version=++_stockVersion;bool initial=_lastStockResponse==null;_stockCylinder=cylinder;_stockState.Text=L.T("Native CAD sınırları hesaplanıyor…");
        var args=StockArguments(cylinder);
        try{var r=await _worker.CallAsync("envelope",args,_life.Token);if(_closed||version!=_stockVersion)return;_lastStockArgs=args;_lastStockResponse=r;_lastStockCylinder=cylinder;RenderStock(r,cylinder);if(initial)Fit();_stockState.Text=L.F("{0} gövde · {1} · Önizleme", args.body_ids.Length, r.GetProperty("orientation_method").GetString());}
        catch{if(!_closed&&version==_stockVersion)_stockState.Text=L.T("Önizleme hesaplanamadı; geometri ve CSYS alanlarını kontrol edin.");throw;}
    }
    void RedrawLastStock(){if(_lastStockResponse is {} r)RenderStock(r,_lastStockCylinder);}
    void RenderStock(JsonElement r,bool cylinder)
    {
        _envelopes.Children.Clear();_stockDimensions.Clear();var mesh=new MeshGeometry3D();
        if(!cylinder)
        {
            var p=r.GetProperty("corners").EnumerateArray().Select(Point).ToArray();var size=r.GetProperty("box_size").EnumerateArray().Select(v=>v.GetDouble()).ToArray();for(int i=0;i<8;i++)foreach(var bit in new[]{1,2,4})if(i<(i^bit))Rod(mesh,p[i],p[i^bit],_size/900);
            _stockDimensions.Add((p[0],p[4],$"X: {size[0]:0.###} mm",Brushes.DarkRed));_stockDimensions.Add((p[4],p[6],$"Y: {size[1]:0.###} mm",Brushes.DarkGreen));_stockDimensions.Add((p[6],p[7],$"Z: {size[2]:0.###} mm",Brushes.DarkBlue));
            _stockSummary.Text=L.F("KUTU BOYUTLARI\nX  {0:0.###} mm\nY  {1:0.###} mm\nZ  {2:0.###} mm\nHacim  {3:0.###} cm³", size[0], size[1], size[2], r.GetProperty("box_volume_mm3").GetDouble()/1000);SetStockVolume(r.GetProperty("box_volume_mm3").GetDouble());
            if(_solidStock.IsChecked==true){var solid=new MeshGeometry3D();foreach(var point in p)solid.Positions.Add(point);foreach(int i in new[]{0,4,6,0,6,2,1,3,7,1,7,5,0,2,3,0,3,1,4,5,7,4,7,6,0,1,5,0,5,4,2,6,7,2,7,3})solid.TriangleIndices.Add(i);_envelopes.Children.Add(Model(solid,new SolidColorBrush(Color.FromArgb(45,235,151,24))));}
        }
        else
        {
            var c=r.GetProperty("cylinder");var center=Point(c.GetProperty("center"));var d=Point(r.GetProperty("csys").GetProperty("z"))-new Point3D();var x=Point(r.GetProperty("csys").GetProperty("x"))-new Point3D();var y=Point(r.GetProperty("csys").GetProperty("y"))-new Point3D();double radius=c.GetProperty("radius").GetDouble(),height=c.GetProperty("height").GetDouble();
            for(int i=0;i<96;i++){double a=2*Math.PI*i/96,b=2*Math.PI*(i+1)/96;var p=(x*Math.Cos(a)+y*Math.Sin(a))*radius;var q=(x*Math.Cos(b)+y*Math.Sin(b))*radius;Rod(mesh,center+p-d*height/2,center+q-d*height/2,_size/900);Rod(mesh,center+p+d*height/2,center+q+d*height/2,_size/900);if(i%24==0)Rod(mesh,center+p-d*height/2,center+p+d*height/2,_size/900);}
            if(_solidStock.IsChecked==true){var solid=new MeshGeometry3D();solid.Positions.Add(center-d*height/2);solid.Positions.Add(center+d*height/2);for(int i=0;i<96;i++){var radial=(x*Math.Cos(2*Math.PI*i/96)+y*Math.Sin(2*Math.PI*i/96))*radius;solid.Positions.Add(center+radial-d*height/2);solid.Positions.Add(center+radial+d*height/2);}for(int i=0;i<96;i++){int a=2+2*i,b=2+2*((i+1)%96);foreach(int index in new[]{a,b,b+1,a,b+1,a+1,0,b,a,1,a+1,b+1})solid.TriangleIndices.Add(index);}_envelopes.Children.Add(Model(solid,new SolidColorBrush(Color.FromArgb(45,235,151,24))));}
            _stockDimensions.Add((center-x*radius-d*height/2,center+x*radius-d*height/2,$"Ø {radius*2:0.###} mm",Brushes.DarkRed));_stockDimensions.Add((center+x*radius-d*height/2,center+x*radius+d*height/2,$"L: {height:0.###} mm",Brushes.DarkBlue));_stockSummary.Text=L.F("SİLİNDİR BOYUTLARI\nÇap  {0:0.###} mm\nBoy  {1:0.###} mm\nHacim  {2:0.###} cm³\nRadyal sınır payı  {3:0.####} mm", radius*2, height, c.GetProperty("volume_mm3").GetDouble()/1000, c.GetProperty("radial_bound_margin_mm").GetDouble());SetStockVolume(c.GetProperty("volume_mm3").GetDouble());
        }
        mesh.Freeze();_envelopes.Children.Add(Model(mesh,Brushes.DarkOrange));var csys=r.GetProperty("csys");_frameSummary.Text=string.Join("\n",new[]{"origin","x","y","z"}.Select(k=>k+": "+string.Join(", ",csys.GetProperty(k).EnumerateArray().Select(v=>v.GetDouble().ToString("0.######")))));_status.Text=(cylinder?L.T("Silindir"):L.T("Kutu"))+L.T(" stok · Genel ofset: ")+_stockOffset.Text+L.T(" mm · Boyutlar panelde ve model üzerinde");UpdateStockLabels();
    }
    void UpdateStockLabels()
    {
        _stockDimensionCanvas.Children.Clear();_stockLabelCanvas.Children.Clear();if(_showStockDimensions.IsChecked!=true)return;var occupied=new List<Rect>();
        void Line(Point a,Point b,Brush color)=>_stockDimensionCanvas.Children.Add(new System.Windows.Shapes.Line{X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y,Stroke=color,StrokeThickness=1.6});
        foreach(var (a,b,text,color) in _stockDimensions){var p=Screen(a);var q=Screen(b);var d=q-p;if(d.Length>1){d.Normalize();var n=new Vector(-d.Y,d.X);Line(p,q,color);foreach(var (end,sign) in new[]{(p,1),(q,-1)}){Line(end,end+d*(sign*8)+n*4,color);Line(end,end+d*(sign*8)-n*4,color);}}var mid=Screen(a+(b-a)*.5);var label=new TextBlock{Text=text,Foreground=color,Background=Brushes.LightGoldenrodYellow,FontWeight=FontWeights.Bold,Padding=new Thickness(5,3,5,3)};label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));double x=Math.Clamp(mid.X+9,0,Math.Max(0,_viewport.ActualWidth-label.DesiredSize.Width)),y=Math.Clamp(mid.Y-24,0,Math.Max(0,_viewport.ActualHeight-label.DesiredSize.Height));var rect=new Rect(x,y,label.DesiredSize.Width,label.DesiredSize.Height);while(occupied.Any(r=>r.IntersectsWith(rect))&&y>label.DesiredSize.Height+6){y-=label.DesiredSize.Height+6;rect.Y=y;}occupied.Add(rect);Canvas.SetLeft(label,x);Canvas.SetTop(label,y);_stockLabelCanvas.Children.Add(label);}
    }
    void ClearStock(){_stockVersion++;_previewTimer.Stop();_stockCylinder=null;_lastStockResponse=null;_lastStockArgs=_appliedStockArgs=null;_envelopes.Children.Clear();_stockDimensions.Clear();UpdateStockLabels();_stockSummary.Text=L.T("Henüz stok oluşturulmadı.");_stockState.Text=L.T("Stok kaldırıldı.");SetStockVolume(null);}
    async Task ApplyStockAsync(){if(_stockCylinder is not {} type)throw new InvalidOperationException(L.T("Önce stok türünü seçip önizleyin."));int expected=_stockVersion+1;await EnvelopeAsync(type);if(_closed)return;if(expected!=_stockVersion||_lastStockArgs==null)throw new InvalidOperationException(L.T("Hesap sırasında parametre değişti. Güncel önizleme sonrası yeniden Uygula seçin."));_appliedStockArgs=_lastStockArgs;_collectStock=false;Recolor();_stockState.Text=L.T("Stok uygulandı. Boyutlar yukarıda ve model üzerinde.");}
    async Task CancelStockAsync()
    {
        if(_appliedStockArgs==null){ClearStock();return;}var data=JsonSerializer.SerializeToElement(_appliedStockArgs);_updatingStock=true;_stockScope.SelectedIndex=2;_stockBodies.Clear();_stockBodies.UnionWith(data.GetProperty("body_ids").EnumerateArray().Select(v=>v.GetInt32()));_stockOrientation.SelectedIndex=data.GetProperty("orientation").GetString() switch{"world"=>0,"auto"=>2,_=>1};_direction.Text=FormatVector(data.GetProperty("direction").EnumerateArray().Select(v=>v.GetDouble()).ToArray());_xDirection.Text=FormatVector(data.GetProperty("x_direction").EnumerateArray().Select(v=>v.GetDouble()).ToArray());_origin.Text=FormatVector(data.GetProperty("origin").EnumerateArray().Select(v=>v.GetDouble()).ToArray());for(int i=0;i<6;i++)_boxOffsets[i].Text=data.GetProperty("box_offsets")[i].GetDouble().ToString(CultureInfo.InvariantCulture);for(int i=0;i<3;i++)_cylinderOffsets[i].Text=data.GetProperty("cylinder_offsets")[i].GetDouble().ToString(CultureInfo.InvariantCulture);_stockOffset.Text=data.GetProperty("uniform_offset").GetDouble().ToString(CultureInfo.InvariantCulture);_updatingStock=false;RefreshStockObjects();await EnvelopeAsync(data.GetProperty("stock_type").GetString()=="cylinder");_collectStock=false;Recolor();_stockState.Text=L.T("Son uygulanan stok geri yüklendi.");
    }
    async Task ExportStockAsync()
    {
        if(_stockCylinder is not {} cylinder)throw new InvalidOperationException(L.T("Önce stok oluşturun."));var dialog=new SaveFileDialog{Title=L.T("Stok katısını STEP olarak kaydet"),Filter=L.T("STEP dosyası|*.stp;*.step"),FileName=System.IO.Path.GetFileNameWithoutExtension(_sourcePath)+"_stock.stp",DefaultExt=".stp",OverwritePrompt=true};if(dialog.ShowDialog(this)!=true)return;var args=StockArguments(cylinder);await _worker.CallAsync("export_stock",new{path=dialog.FileName,stock_args=args},_life.Token);_stockState.Text=L.T("Native stok katısı STEP olarak kaydedildi.");
    }
}
