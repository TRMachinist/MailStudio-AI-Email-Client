using MailStudio.Services;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using static MailStudio.Views.CadSceneGeometry;

namespace MailStudio.Views;

public sealed partial class StepAttachmentWindow
{
    readonly Canvas _sceneCanvas=new(){ClipToBounds=true},_savedCanvas=new(){IsHitTestVisible=false,ClipToBounds=true};
    readonly Model3DGroup _savedGeometry=new();readonly ListBox _savedList=new(){MaxHeight=100,Margin=new Thickness(0,3,0,0)};
    readonly List<SavedMeasure> _savedMeasures=new();
    readonly Dictionary<TextBlock,Vector> _labelOffsets=new();readonly Dictionary<TextBlock,(Point3D a,Point3D b)> _labelWitness=new();
    bool _sceneMoved;Border? _sceneDialog;Point3D _sceneAnchor;Vector _sceneOffset=new(25,35);int _measureNumber;
    sealed class SavedMeasure{public string Name="";public bool Visible=true;public Model3DGroup Geometry=new();public List<(Point3D a,Point3D b,Brush color)> Lines=new();public List<(Point3D p,string text,Brush color,Vector offset,Point3D? a,Point3D? b)> Labels=new();}
    FrameworkElement SavedMeasureControls()
    {
        var p=new StackPanel();p.Children.Add(new TextBlock{Text=L.T("Tutulan ölçümler · bu dosya oturumu"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,9,0,3)});p.Children.Add(_savedList);var b=new WrapPanel();b.Children.Add(Button(L.T("Göster / gizle"),()=>{if(_savedList.SelectedIndex is int i&&i>=0&&i<_savedMeasures.Count){_savedMeasures[i].Visible=!_savedMeasures[i].Visible;UpdateSavedMeasurements();}}));b.Children.Add(Button(L.T("Sil"),()=>{if(_savedList.SelectedIndex is int i&&i>=0&&i<_savedMeasures.Count){_savedMeasures.RemoveAt(i);UpdateSavedMeasurements();}}));p.Children.Add(b);return p;
    }
    void PinMeasurement()
    {
        if(_measureResponse==null||_resultGrid.Items.Count==0){_measureHint.Text=L.T("Önce tutulacak ölçümü seçin.");return;}
        if(_savedMeasures.Count>=30){_measureHint.Text=L.T("En fazla 30 ölçüm tutulabilir; önce birini silin.");return;}
        var saved=new SavedMeasure{Name=L.F("Ölçüm {0} · {1}", ++_measureNumber, _resultChoice.SelectedItem),Geometry=_measurements.CloneCurrentValue()};saved.Lines.AddRange(_dimensionSegments);foreach(var l in _labels){bool has=_labelWitness.TryGetValue(l.label,out var w);var at=Screen(l.point);var placed=new Vector(Canvas.GetLeft(l.label)-at.X-8,Canvas.GetTop(l.label)-at.Y+24);saved.Labels.Add((l.point,l.label.Text,l.label.Foreground,placed,has?w.a:null,has?w.b:null));}_savedMeasures.Add(saved);UpdateSavedMeasurements();ClearSelection();
    }
    void UpdateSavedMeasurements()
    {
        _savedGeometry.Children.Clear();foreach(var saved in _savedMeasures.Where(s=>s.Visible))_savedGeometry.Children.Add(saved.Geometry);_savedList.ItemsSource=_savedMeasures.Select(s=>(s.Visible?"● ":"○ ")+s.Name).ToArray();DrawSavedMeasurements();
    }
    void DrawSavedMeasurements()
    {
        _savedCanvas.Children.Clear();foreach(var saved in _savedMeasures.Where(s=>s.Visible))
        {
            foreach(var (a,b,color) in saved.Lines)DrawArrow(_savedCanvas,Screen(a),Screen(b),color,1.4);
            foreach(var (p,text,color,offset,a,b) in saved.Labels){var screen=Screen(p);var label=new TextBlock{Text=text,Foreground=color,Background=Brushes.LightGoldenrodYellow,Padding=new Thickness(4,2,4,2)};label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));double x=Math.Clamp(screen.X+8+offset.X,0,Math.Max(0,_viewport.ActualWidth-label.DesiredSize.Width)),y=Math.Clamp(screen.Y-24+offset.Y,0,Math.Max(0,_viewport.ActualHeight-label.DesiredSize.Height));if(a is {} pa&&b is {} pb){var sa=Screen(pa);var sb=Screen(pb);var mid=new Point((sa.X+sb.X)/2,(sa.Y+sb.Y)/2);var shift=new Point(x+label.DesiredSize.Width/2,y+label.DesiredSize.Height/2)-mid;DrawLine(_savedCanvas,sa,sa+shift,color,.8);DrawLine(_savedCanvas,sb,sb+shift,color,.8);DrawArrow(_savedCanvas,sa+shift,sb+shift,color);}_savedCanvas.Children.Add(label);Canvas.SetLeft(label,x);Canvas.SetTop(label,y);}
        }
    }
    static void DrawLine(Canvas canvas,Point a,Point b,Brush color,double thickness=1.5)=>canvas.Children.Add(new System.Windows.Shapes.Line{X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y,Stroke=color,StrokeThickness=thickness});
    static void DrawArrow(Canvas canvas,Point a,Point b,Brush color,double thickness=1.6)
    {
        DrawLine(canvas,a,b,color,thickness);var d=b-a;if(d.Length<1)return;d.Normalize();var n=new Vector(-d.Y,d.X);foreach(var (end,sign) in new[]{(a,1),(b,-1)}){DrawLine(canvas,end,end+d*(sign*8)+n*3.5,color,thickness);DrawLine(canvas,end,end+d*(sign*8)-n*3.5,color,thickness);}
    }
    void ConfigureLabelDrag(TextBlock label)
    {
        label.Cursor=Cursors.SizeAll;label.ToolTip=L.T("Ölçü etiketini sürükleyerek yerleştirin.");Point? previous=null;
        label.MouseLeftButtonDown+=(_,e)=>{previous=e.GetPosition(_labelCanvas);label.CaptureMouse();e.Handled=true;};
        label.MouseMove+=(_,e)=>{if(previous is not {} old)return;var next=e.GetPosition(_labelCanvas);MoveDimensionLabel(label,next-old);previous=next;e.Handled=true;};
        label.MouseLeftButtonUp+=(_,e)=>{previous=null;label.ReleaseMouseCapture();e.Handled=true;};label.LostMouseCapture+=(_,_)=>previous=null;
    }
    void MoveDimensionLabel(TextBlock label,Vector delta){_labelOffsets[label]=_labelOffsets.GetValueOrDefault(label)+delta;UpdateLabels();}
    void Dimension(Point3D a,Point3D b,string text,Brush color)
    {
        _dimensionSegments.Add((a,b,color));Label(a+(b-a)*.5,text,color);_labelWitness[_labels[^1].label]=(a,b);var mesh=new MeshGeometry3D();Marker(mesh,a,_size/500);Marker(mesh,b,_size/500);_measurements.Children.Add(Model(mesh,color));
    }
    void AngleGraphic(Point3D vertex,Vector3D u,Vector3D v,double angle,string text)
    {
        if(u.Length<1e-10||v.Length<1e-10)return;u.Normalize();v.Normalize();double radius=_size*.065;var cross=Vector3D.CrossProduct(u,v);if(cross.Length<1e-8){Label(vertex,text,Brushes.DarkMagenta);return;}cross.Normalize();var tangent=Vector3D.CrossProduct(cross,u);int count=32;var last=vertex+u*radius;
        for(int i=1;i<=count;i++){double theta=angle*Math.PI/180*i/count;var next=vertex+(u*Math.Cos(theta)+tangent*Math.Sin(theta))*radius;_dimensionSegments.Add((last,next,Brushes.DarkMagenta));last=next;}
        _dimensionSegments.Add((vertex,vertex+u*radius*1.2,Brushes.DarkMagenta));_dimensionSegments.Add((vertex,vertex+v*radius*1.2,Brushes.DarkMagenta));Label(vertex+(u+v)*(radius*.6),text,Brushes.DarkMagenta);
    }
    void RenderActiveMeasurement(JsonElement selected,JsonElement[] rows,JsonElement primary)
    {
        if(primary.ValueKind==JsonValueKind.Object&&primary.TryGetProperty("a",out var a)&&primary.TryGetProperty("b",out var b)){var f=DisplayValue(primary);Dimension(Point(a),Point(b),f.value+" "+f.unit,Brushes.DarkRed);}
        else if(selected.TryGetProperty("graphic",out var graphic)&&graphic.ValueKind==JsonValueKind.Object&&Filter(graphic.GetProperty("category").GetString()!))
        {
            string key=graphic.GetProperty("key").GetString()!;var matching=rows.FirstOrDefault(r=>r.GetProperty("key").GetString()==key);
            if(matching.ValueKind==JsonValueKind.Object){var f=DisplayValue(matching);if(graphic.GetProperty("kind").GetString()=="angle"){var vertex=Point(graphic.GetProperty("vertex"));AngleGraphic(vertex,Point(graphic.GetProperty("a"))-vertex,Point(graphic.GetProperty("b"))-vertex,matching.GetProperty("value").GetDouble(),f.value+f.unit);}else Dimension(Point(graphic.GetProperty("a")),Point(graphic.GetProperty("b")),(key=="diameter_mm"?"Ø ":"")+f.value+" "+f.unit,Brushes.DarkRed);}
        }
        if(Filter("angle")&&selected.TryGetProperty("angle_directions",out var directions)&&directions.ValueKind==JsonValueKind.Array){var angle=rows.FirstOrDefault(r=>r.GetProperty("key").GetString()=="angle");if(angle.ValueKind==JsonValueKind.Object){var d=directions.EnumerateArray().Select(Point).ToArray();var f=DisplayValue(angle);AngleGraphic(Point(selected.GetProperty("anchor")),d[0]-new Point3D(),d[1]-new Point3D(),angle.GetProperty("value").GetDouble(),f.value+f.unit);}}
        if(_labels.Count==0&&rows.Length>0){var visible=rows.FirstOrDefault(r=>r.GetProperty("category").GetString()!="coordinates");if(visible.ValueKind!=JsonValueKind.Object)visible=rows[0];var f=DisplayValue(visible);Label(Point(selected.GetProperty("anchor")),visible.GetProperty("label").GetString()+": "+f.value+" "+f.unit,Brushes.DarkSlateBlue);}
    }
    void BuildSceneDialog(JsonElement selected,JsonElement[] rows,JsonElement[] distances,string? key)
    {
        if(rows.Length==0)return;_sceneAnchor=Point(selected.GetProperty("anchor"));var p=new StackPanel{MinWidth=220,MaxWidth=290};var title=new TextBlock{Text="Measurement · "+selected.GetProperty("title").GetString(),Background=ThemeManager.Brush("AccentFillBrush"),Foreground=Brushes.White,FontWeight=FontWeights.SemiBold,Padding=new Thickness(8,5,8,5),Cursor=Cursors.SizeAll};p.Children.Add(title);
        if(distances.Length>1){var options=distances.Select(d=>new DistanceOption(d.GetProperty("key").GetString()!,d.GetProperty("label").GetString()!)).ToArray();var combo=new ComboBox{ItemsSource=options,DisplayMemberPath="Label",SelectedIndex=System.Array.FindIndex(options,o=>o.Key==key),Margin=new Thickness(5)};combo.SelectionChanged+=(_,e)=>{if(combo.SelectedItem is DistanceOption choice){var list=(_distanceChoice.ItemsSource as IEnumerable<DistanceOption>)!.ToArray();_distanceChoice.SelectedIndex=System.Array.FindIndex(list,o=>o.Key==choice.Key);}e.Handled=true;};p.Children.Add(combo);}
        foreach(var r in rows.Take(9)){var f=DisplayValue(r);var row=new DockPanel{Margin=new Thickness(8,2,8,2)};var value=new TextBlock{Text=f.value+" "+f.unit,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(10,0,0,0)};DockPanel.SetDock(value,Dock.Right);row.Children.Add(value);row.Children.Add(new TextBlock{Text=r.GetProperty("label").GetString(),TextWrapping=TextWrapping.Wrap});p.Children.Add(row);}
        var keep=new Button{Content=L.T("Ölçümü tut · yeni seçime geç"),Padding=new Thickness(5,3,5,3),Margin=new Thickness(6)};keep.Click+=(_,_)=>PinMeasurement();p.Children.Add(keep);
        _sceneDialog=new Border{Child=p,Background=ThemeManager.Brush("BgPopupBrush"),BorderBrush=ThemeManager.Brush("AccentFillBrush"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(2),Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=6,Opacity=.2,ShadowDepth=2}};_sceneCanvas.Children.Add(_sceneDialog);
        Point? previous=null;title.MouseLeftButtonDown+=(_,e)=>{previous=e.GetPosition(_sceneCanvas);title.CaptureMouse();e.Handled=true;};title.MouseMove+=(_,e)=>{if(previous is not {} old)return;var next=e.GetPosition(_sceneCanvas);_sceneMoved=true;_sceneOffset+=next-old;previous=next;UpdateScenePosition();e.Handled=true;};title.MouseLeftButtonUp+=(_,e)=>{previous=null;title.ReleaseMouseCapture();e.Handled=true;};_sceneDialog.MouseDown+=(_,e)=>e.Handled=true;UpdateScenePosition();
    }
    void UpdateScenePosition()
    {
        if(_sceneDialog==null)return;_sceneDialog.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));var p=Screen(_sceneAnchor);double w=_sceneDialog.DesiredSize.Width,h=_sceneDialog.DesiredSize.Height;
        Point Clamp(Point q)=>new(Math.Clamp(q.X,4,Math.Max(4,_viewport.ActualWidth-w-4)),Math.Clamp(q.Y,4,Math.Max(4,_viewport.ActualHeight-h-4)));
        var desired=Clamp(p+_sceneOffset);var best=desired;
        if(!_sceneMoved)
        {
            var obstacles=_labels.Select(l=>{double x=Canvas.GetLeft(l.label),y=Canvas.GetTop(l.label);return double.IsFinite(x)&&double.IsFinite(y)?new Rect(x-6,y-6,l.label.DesiredSize.Width+12,l.label.DesiredSize.Height+12):Rect.Empty;}).Where(r=>!r.IsEmpty).ToArray();
            double Score(Point q){var r=new Rect(q,new Size(w,h));return obstacles.Sum(o=>{var overlap=Rect.Intersect(o,r);return overlap.IsEmpty?0:overlap.Width*overlap.Height;});}
            var candidates=new[]{desired,Clamp(p+new Vector(25,-h-45)),Clamp(p+new Vector(-w-35,-h-45)),Clamp(p+new Vector(-w-35,35))};best=candidates.MinBy(Score);
        }
        Canvas.SetLeft(_sceneDialog,best.X);Canvas.SetTop(_sceneDialog,best.Y);
    }
    async Task PreviewHoverAsync()
    {
        if(!_loaded||_closed||_hoverEnabled.IsChecked!=true||_directionPick||_last!=null||_tabs.SelectedIndex!=0)return;
        var candidate=FindCandidate(_hoverScreen);if(candidate==null){if(_hovered!=null)Recolor();_hovered=null;_hoverInfo.Text="";return;}if(_hovered!=null&&SameSelection(candidate,_hovered))return;_hovered=candidate;int version=++_hoverVersion;
        var info=await _worker.CallAsync("inspect",candidate.Payload,_life.Token);if(_closed||version!=_hoverVersion||_last!=null)return;
        Recolor();if(!_selection.Any(s=>SameSelection(s,candidate))){var model=candidate.Kind=="edge"?_edgeModels.GetValueOrDefault(candidate.Id):candidate.Kind=="face"?_faceModels.GetValueOrDefault(candidate.Id):null;if(model!=null)model.Material=model.BackMaterial=new DiffuseMaterial(Brushes.PaleGreen);}_hoverInfo.Text=L.T("Önizleme · ")+EntityText(info).Replace("\n"," · ");
    }
}
