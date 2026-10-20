using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MailStudio.Services;
using static MailStudio.Views.CadSceneGeometry;

namespace MailStudio.Views;

public sealed partial class StepAttachmentWindow : Window
{
    sealed record Selection(string Kind,int Id,Point3D Point,string? SourceKind=null)
    {
        public object Payload=>new{kind=Kind,id=Id,point=Array(Point),source_kind=SourceKind};
        public string Name=>(Kind switch{"face"=>L.T("Yüz"),"edge"=>L.T("Kenar"),"vertex"=>L.T("Köşe"),"body"=>L.T("Gövde"),"component"=>L.T("Bileşen"),"center"=>L.T("Merkez"),_=>L.T("Nokta")})+" "+Id;
    }
    readonly TechnicalWorker _worker;readonly CancellationTokenSource _life=new();
    readonly Viewport3D _viewport=new();readonly OrthographicCamera _camera=new();
    readonly Model3DGroup _model=new(),_edges=new(),_envelopes=new(),_measurements=new(),_pmiGraphics=new();
    readonly ModelVisual3D _pmiVisual=new(),_edgeVisual=new();readonly MeshGeometry3D _pmiLines=new();
    readonly Dictionary<GeometryModel3D,int> _ids=new(),_edgeIds=new();
    readonly Dictionary<int,GeometryModel3D> _faceModels=new(),_edgeModels=new();readonly Dictionary<int,Point3D[]> _edgePoints=new();readonly Dictionary<int,string> _edgeTypes=new();readonly List<Point3D> _vertices=new();
    readonly List<Selection> _selection=new();readonly List<(Point3D point,TextBlock label)> _labels=new();readonly List<(Point3D a,Point3D b,Brush color)> _dimensionSegments=new();readonly Canvas _labelCanvas=new(){ClipToBounds=true},_dimensionCanvas=new(){IsHitTestVisible=false,ClipToBounds=true};
    readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(10)};
    readonly TextBox _result=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(10)};
    readonly ListBox _pmi=new(){Margin=new Thickness(4)};readonly TabControl _tabs=new();
    readonly ComboBox _mode=new(){Width=110,Margin=new Thickness(4),ItemsSource=new[]{L.T("Yüz"),L.T("Kenar"),L.T("Nokta"),L.T("Gövde"),L.T("Bileşen"),L.T("Otomatik")},SelectedIndex=5};
    readonly ComboBox _distanceMode=new(){Width=140,Margin=new Thickness(4),ItemsSource=new[]{L.T("Nokta mesafesi"),L.T("Minimum mesafe")},SelectedIndex=1};
    readonly ComboBox _directionMode=new(){Width=155,Margin=new Thickness(4),ItemsSource=new[]{L.T("Yüz normali"),L.T("Silindir / daire ekseni"),L.T("Düz kenar yönü")},SelectedIndex=0};
    readonly TextBox _direction=new(){Text="0, 0, 1",Width=115,Margin=new Thickness(4)};
    readonly TextBox[] _boxOffsets=Enumerable.Range(0,6).Select(_=>new TextBox{Text="0",Width=55,Margin=new Thickness(4)}).ToArray();
    readonly TextBox[] _cylinderOffsets=Enumerable.Range(0,3).Select(_=>new TextBox{Text="0",Width=55,Margin=new Thickness(4)}).ToArray();
    readonly TextBox _stockOffset=new(){Text="0",Width=70,Margin=new Thickness(4)};
    readonly CheckBox _showPmi=new(){Content=L.T("Grafik PMI"),IsChecked=true,Margin=new Thickness(8,4,8,4),VerticalAlignment=VerticalAlignment.Center};
    readonly CheckBox _showEdges=new(){Content=L.T("Kenar çizgileri"),IsChecked=true,Margin=new Thickness(8,4,8,4),VerticalAlignment=VerticalAlignment.Center};
    static readonly Brush FaceBrush=new SolidColorBrush(Color.FromRgb(188,204,216));
    Brush _edgeBrush=Brushes.Black;Grid? _canvas;bool _darkScene;readonly CheckBox _darkSceneCheck=new(){Content=L.T("Koyu sahne"),Margin=new Thickness(8,4,8,4),VerticalAlignment=VerticalAlignment.Center,ToolTip=L.T("CAD stüdyo kontrast modu: koyu zemin, açık kenar çizgileri")};
    Point3D _center;double _size=100;Vector3D _eye=EyeFrom(.7,.5),_up=new(0,0,1);Point? _last,_down,_rightDown;bool _rightUsedForNav;enum NavMode{None,Rotate,Pan,Zoom}NavMode _navMode;bool _dragged,_loaded,_closed,_directionPick;bool? _stockCylinder;int _selectionVersion,_stockVersion;
    public StepAttachmentWindow(string path,string? workerExecutable=null)
    {
        _worker=new(workerExecutable);_sourcePath=path;Title=System.IO.Path.GetFileName(path)+" — STEP / CAD";Width=1450;Height=880;MinWidth=1120;MinHeight=650;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        var viewBar=new WrapPanel{Margin=new Thickness(6,4,6,2)};var viewBarHost=new Border{Child=viewBar,Background=ThemeManager.Brush("BgSurfaceSecondaryBrush"),BorderBrush=ThemeManager.Brush("BorderSubtleBrush"),BorderThickness=new Thickness(0,0,0,1)};root.Children.Add(viewBarHost);
        viewBar.Children.Add(Button(L.T("Sığdır"),Fit));viewBar.Children.Add(Button(L.T("İzometrik"),()=>View(.7,.5)));viewBar.Children.Add(Button(L.T("Üst"),()=>View(-Math.PI/2,Math.PI/2)));viewBar.Children.Add(Button(L.T("Alt"),()=>View(-Math.PI/2,-Math.PI/2)));viewBar.Children.Add(Button(L.T("Ön"),()=>View(-Math.PI/2,0)));viewBar.Children.Add(Button(L.T("Arka"),()=>View(Math.PI/2,0)));viewBar.Children.Add(Button(L.T("Sağ"),()=>View(0,0)));viewBar.Children.Add(Button(L.T("Sol"),()=>View(Math.PI,0)));var normalButton=Button(L.T("Yüzeye dik bak (N)"),async()=>await ExecuteAsync(LookAtSelectedFaceAsync));normalButton.ToolTip=L.T("Seçili yüzeyin normaline dik bakar. Aynı tuşa tekrar basınca arka taraftan bakar.");viewBar.Children.Add(normalButton);
        viewBar.Children.Add(_showEdges);viewBar.Children.Add(_showPmi);viewBar.Children.Add(new TextBlock{Text=L.T("Seçim:"),VerticalAlignment=VerticalAlignment.Center});viewBar.Children.Add(_mode);
        viewBar.Children.Add(Button(L.T("Seçimi temizle (Esc)"),ClearSelection));viewBar.Children.Add(_darkSceneCheck);
        viewBar.Children.Add(Button(L.T("Revizyon Karşılaştır"),async()=>await CompareRevisionAsync()));
        _showEdges.Checked+=(_,_)=>_edgeVisual.Content=_edges;_showEdges.Unchecked+=(_,_)=>_edgeVisual.Content=null;
        _showPmi.Checked+=(_,_)=>{_pmiVisual.Content=_pmiGraphics;Fit();};_showPmi.Unchecked+=(_,_)=>{_pmiVisual.Content=null;Fit();};
        _mode.SelectionChanged+=(_,_)=>{_hovered=null;_hoverInfo.Text="";_status.Text=L.T("Seçim filtresi değişti; nesne listeniz korunuyor.");};
        var measureBar=new WrapPanel{Margin=new Thickness(6,2,6,4)};var measureBarHost=new Border{Child=measureBar,Background=ThemeManager.Brush("BgSurfaceSecondaryBrush"),BorderBrush=ThemeManager.Brush("BorderSubtleBrush"),BorderThickness=new Thickness(0,0,0,1)};Grid.SetRow(measureBarHost,1);root.Children.Add(measureBarHost);
        measureBar.Children.Add(Button("Measure",()=>_tabs.SelectedIndex=0));measureBar.Children.Add(Button(L.T("Ölçüm vektörü seç"),()=>{_tabs.SelectedIndex=0;_function.SelectedIndex=3;}));measureBar.Children.Add(_directionMode);
        measureBar.Children.Add(new TextBlock{Text=L.T("Stok Z:"),VerticalAlignment=VerticalAlignment.Center});measureBar.Children.Add(_direction);
        measureBar.Children.Add(Button(L.T("Stok yönünü seç"),()=>{_measureVectorPick=false;_framePick="z";_directionPick=true;_viewport.Cursor=Cursors.Cross;_status.Text=L.T("Stok yönü için uygun geometriye tıklayın.");}));
        measureBar.Children.Add(Button(L.T("Stok araçları"),()=>_tabs.SelectedIndex=1));
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(230)});body.ColumnDefinitions.Add(new());body.ColumnDefinitions.Add(new(){Width=new GridLength(380)});Grid.SetRow(body,2);root.Children.Add(body);
        body.Children.Add(AssemblyControls());
        var canvas=new Grid{Background=new SolidColorBrush(Color.FromRgb(235,240,245)),Focusable=true};_canvas=canvas;canvas.Children.Add(_viewport);canvas.Children.Add(_dimensionCanvas);canvas.Children.Add(_labelCanvas);canvas.Children.Add(_stockDimensionCanvas);canvas.Children.Add(_stockLabelCanvas);canvas.Children.Add(_savedCanvas);canvas.Children.Add(_sceneCanvas);Grid.SetColumn(canvas,1);body.Children.Add(canvas);
        _viewport.Camera=_camera;_camera.NearPlaneDistance=.001;_camera.FarPlaneDistance=1e9;
        var scene=new Model3DGroup();scene.Children.Add(new AmbientLight(Color.FromRgb(140,140,140)));scene.Children.Add(new DirectionalLight(Colors.White,new Vector3D(-1,-2,-3)));scene.Children.Add(_model);scene.Children.Add(_envelopes);scene.Children.Add(_measurements);scene.Children.Add(_savedGeometry);
        _viewport.Children.Add(new ModelVisual3D{Content=scene});_edgeVisual.Content=_edges;_viewport.Children.Add(_edgeVisual);_pmiVisual.Content=_pmiGraphics;_viewport.Children.Add(_pmiVisual);
        Grid.SetColumn(_tabs,2);body.Children.Add(_tabs);_tabs.Items.Add(new TabItem{Header="Measure",Content=MeasureControls()});_tabs.Items.Add(new TabItem{Header=L.T("Bounding Box / Stok"),Content=BoundingControls()});_tabs.Items.Add(new TabItem{Header="PMI",Content=_pmi});
        _tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,_tabs))_collectStock=_tabs.SelectedIndex==1&&_stockScope.SelectedIndex==2;};
        var statusHost=new Border{Child=_status,Background=ThemeManager.Brush("BgSurfaceSecondaryBrush"),BorderBrush=ThemeManager.Brush("BorderSubtleBrush"),BorderThickness=new Thickness(0,1,0,0)};Grid.SetRow(statusHost,3);root.Children.Add(statusHost);
        _status.Foreground=ThemeManager.Brush("TextSecondaryBrush");_status.Margin=new Thickness(10,5,10,5);
        var sceneSetting=AppSettings.Shared.CadScene;_darkScene=sceneSetting=="Dark"||(sceneSetting!="Light"&&ThemeManager.IsDark);_darkSceneCheck.IsChecked=_darkScene;ApplyScene();
        _darkSceneCheck.Checked+=(_,_)=>{_darkScene=true;ApplyScene();SaveScene();};_darkSceneCheck.Unchecked+=(_,_)=>{_darkScene=false;ApplyScene();SaveScene();};
        // NX layout: left click selects (dragging does not rotate); middle drag = rotate; middle+right or Shift+middle = pan; Ctrl+middle = zoom; wheel = zoom; right click = quick pick menu.
        canvas.MouseDown+=(_,e)=>
        {
            var pos=e.GetPosition(canvas);
            if(e.ChangedButton==MouseButton.Right)
            {
                if(e.MiddleButton==MouseButtonState.Pressed){_navMode=NavMode.Pan;_rightUsedForNav=true;}
                else{_rightDown=pos;_rightUsedForNav=false;}
                e.Handled=true;return;
            }
            if(e.ChangedButton==MouseButton.Left){canvas.Focus();_down=_last=pos;_dragged=false;_navMode=NavMode.None;canvas.CaptureMouse();return;}
            if(e.ChangedButton!=MouseButton.Middle)return;
            canvas.Focus();_down=_last=pos;_dragged=false;
            _navMode=e.RightButton==MouseButtonState.Pressed||Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?NavMode.Pan:Keyboard.Modifiers.HasFlag(ModifierKeys.Control)?NavMode.Zoom:NavMode.Rotate;
            if(e.RightButton==MouseButtonState.Pressed)_rightUsedForNav=true;
            canvas.CaptureMouse();e.Handled=true;
        };
        canvas.MouseMove+=(_,e)=>
        {
            if(_last is not {} old||_down is not {} down){_hoverScreen=e.GetPosition(_viewport);_hoverTimer.Stop();_hoverTimer.Start();return;}
            var now=e.GetPosition(canvas);if((now-down).Length>4)_dragged=true;if(!_dragged||_navMode==NavMode.None)return;
            if(e.MiddleButton==MouseButtonState.Pressed&&e.RightButton==MouseButtonState.Pressed){_navMode=NavMode.Pan;_rightUsedForNav=true;}
            var dx=now.X-old.X;var dy=now.Y-old.Y;
            if(_navMode==NavMode.Pan){Basis(out var right,out var up);_center-=right*(dx*_camera.Width/Math.Max(1,_viewport.ActualWidth));_center+=up*(dy*_camera.Width/Math.Max(1,_viewport.ActualWidth));}
            else if(_navMode==NavMode.Zoom){_camera.Width=Math.Clamp(_camera.Width*Math.Pow(1.006,dy),_size/1000,_size*100);UpdateLabels();}
            else Orbit(dx,dy);
            _last=now;UpdateCamera();
        };
        canvas.MouseUp+=async(_,e)=>
        {
            if(e.ChangedButton==MouseButton.Right)
            {
                e.Handled=true;
                var used=_rightUsedForNav||_navMode!=NavMode.None&&e.MiddleButton==MouseButtonState.Pressed;
                if(!used&&_rightDown is {} rd&&(e.GetPosition(canvas)-rd).Length<5){var where=e.GetPosition(_viewport);_rightDown=null;await ExecuteAsync(()=>ShowQuickPickAsync(where));}
                _rightDown=null;if(e.MiddleButton!=MouseButtonState.Pressed)_rightUsedForNav=false;else _navMode=NavMode.Rotate;
                return;
            }
            if(_last==null)return;
            if(e.ChangedButton==MouseButton.Middle&&e.RightButton==MouseButtonState.Pressed){_rightUsedForNav=true;}
            canvas.ReleaseMouseCapture();var wasDrag=_dragged;_last=_down=null;_navMode=NavMode.None;
            if(!wasDrag&&e.ChangedButton==MouseButton.Left)await ExecuteAsync(()=>PickAsync(e.GetPosition(_viewport)));
        };
        canvas.MouseWheel+=(_,e)=>{_camera.Width=Math.Clamp(_camera.Width*(e.Delta>0?.85:1.18),_size/1000,_size*100);UpdateLabels();};canvas.SizeChanged+=(_,_)=>UpdateLabels();
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){ClearSelection();e.Handled=true;}else if(e.Key==Key.N&&Keyboard.FocusedElement is not TextBox){_ = ExecuteAsync(LookAtSelectedFaceAsync);e.Handled=true;}};
        foreach(var (element,name) in new (DependencyObject,string)[]{(_mode,L.T("CAD seçim türü")),(_direction,L.T("Ölçüm ve stok yön vektörü")),(_directionMode,L.T("Parçadan yön seçme türü")),(_stockOffset,L.T("Stok genel ofseti mm"))})AutomationProperties.SetName(element,name);
        Loaded+=async(_,_)=>await ExecuteAsync(async()=>Load(await _worker.CallAsync("step_open",new{path},_life.Token)));
        Closed+=(_,_)=>{_closed=true;_previewTimer.Stop();_hoverTimer.Stop();_life.Cancel();_worker.Dispose();_life.Dispose();};
    }
    static Button Button(string title,Action run){var b=new Button{Content=title,Padding=new Thickness(9,3,9,3),MinHeight=28,Margin=new Thickness(0,1,4,2)};b.Click+=(_,_)=>run();return b;}
    void ApplyScene()
    {
        if(_canvas==null)return;
        _canvas.Background=_darkScene
            ? new LinearGradientBrush(Color.FromRgb(0x1E,0x23,0x2B),Color.FromRgb(0x2B,0x31,0x3A),90)
            : new LinearGradientBrush(Color.FromRgb(0xDD,0xE3,0xEA),Color.FromRgb(0xF4,0xF6,0xF8),90);
        _edgeBrush=_darkScene?new SolidColorBrush(Color.FromRgb(0xD6,0xDB,0xE1)):Brushes.Black;
        Recolor();
    }
    void SaveScene(){try{AppSettings.Shared.CadScene=_darkScene?"Dark":"Light";AppSettings.Shared.Save();}catch{}}
    static double Number(string value)=>double.TryParse(value.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:throw new FormatException(L.T("Sonlu bir mm değeri girin."));
    static double[] Triple(string value){var a=value.Split(new[]{',',';',' '},StringSplitOptions.RemoveEmptyEntries).Select(s=>double.Parse(s,CultureInfo.InvariantCulture)).ToArray();if(a.Length!=3||a.Any(x=>!double.IsFinite(x))||a.All(x=>x==0))throw new FormatException(L.T("Sıfır olmayan üç yön bileşeni girin; örnek: 0, 0, 1. Ondalık ayırıcı nokta."));return a;}
    async Task ExecuteAsync(Func<Task> work){try{await work();}catch(OperationCanceledException){}catch(Exception ex){if(!_closed)_status.Text=ex.Message;}}
    void Load(JsonElement r)
    {
        SetPartVolume(r);
        var b=r.GetProperty("bounds").EnumerateArray().Select(v=>v.GetDouble()).ToArray();_center=new((b[0]+b[3])/2,(b[1]+b[4])/2,(b[2]+b[5])/2);_size=Math.Max(1,Math.Sqrt(Math.Pow(b[3]-b[0],2)+Math.Pow(b[4]-b[1],2)+Math.Pow(b[5]-b[2],2)));
        foreach(var f in r.GetProperty("faces").EnumerateArray()){var model=Model(Mesh(f),FaceBrush);int id=f.GetProperty("id").GetInt32();_model.Children.Add(model);_ids[model]=id;_faceModels[id]=model;}
        foreach(var e in r.GetProperty("edges").EnumerateArray()){int id=e.GetProperty("id").GetInt32();var points=e.GetProperty("points").EnumerateArray().Select(Point).ToArray();_edgePoints[id]=points;_edgeTypes[id]=e.GetProperty("type").GetString()??"";var mesh=new MeshGeometry3D();for(int i=1;i<points.Length;i++)Rod(mesh,points[i-1],points[i],_size/1500);mesh.Freeze();var model=Model(mesh,_edgeBrush);_edges.Children.Add(model);_edgeIds[model]=id;_edgeModels[id]=model;}
        foreach(var item in r.GetProperty("vertices").EnumerateArray())_vertices.Add(Point(item.GetProperty("point")));
        _pmiGraphics.Children.Add(Model(_pmiLines,Brushes.MidnightBlue));_pmi.Items.Add(L.F("Semantik PMI: {0} kayıt", r.GetProperty("pmi").GetArrayLength()));
        foreach(var item in r.GetProperty("pmi").EnumerateArray())
        {
            if(item.TryGetProperty("presentation",out var presentation))
            {
                foreach(var line in presentation.GetProperty("lines").EnumerateArray()){var points=line.EnumerateArray().Select(Point).ToArray();for(int i=1;i<points.Length;i++)Rod(_pmiLines,points[i-1],points[i],_size/2200);}
                foreach(var f in presentation.GetProperty("faces").EnumerateArray())_pmiGraphics.Children.Add(Model(Mesh(f),Brushes.MidnightBlue));
            }
            _pmi.Items.Add(new TextBlock{Text=string.Join("\n",item.EnumerateObject().Where(p=>p.Name!="presentation").Select(p=>p.Name+": "+p.Value)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4)});
        }
        _pmiLines.Freeze();_pmi.Items.Add(new TextBlock{Text=r.GetProperty("pmi_notice").GetString(),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4)});
        LoadAssembly(r);_loaded=true;Fit();DefaultReadout();_status.Text=L.F("{0} gövde · {1} CAD yüzü · Sol: seç · Orta: döndür · Orta+sağ veya Shift+orta: kaydır · Ctrl+orta / tekerlek: yakınlaştır · Sağ: hızlı seçim · Esc: iptal", r.GetProperty("body_count"), r.GetProperty("face_count"));
    }
    void DefaultReadout(){_measureHint.Text=L.T("Object / Object Set / Point / Vector ile nesneleri tek listeye seçin. Uygun ölçüler otomatik çıkarılır.");_result.Text="";}
    void Fit(){if(_loaded){var b=_model.Bounds;if(_showPmi.IsChecked==true)b.Union(_pmiGraphics.Bounds);b.Union(_envelopes.Bounds);if(!b.IsEmpty){_center=new(b.X+b.SizeX/2,b.Y+b.SizeY/2,b.Z+b.SizeZ/2);_size=Math.Max(1,Math.Sqrt(b.SizeX*b.SizeX+b.SizeY*b.SizeY+b.SizeZ*b.SizeZ));}}_camera.Width=_size*1.15*Math.Max(1,_viewport.ActualWidth/Math.Max(1,_viewport.ActualHeight));UpdateCamera();}
    void View(double yaw,double pitch){_eye=EyeFrom(yaw,pitch);_up=Math.Abs(Math.Cos(pitch))<1e-6?new Vector3D(-Math.Cos(yaw)*Math.Sign(Math.Sin(pitch)),-Math.Sin(yaw)*Math.Sign(Math.Sin(pitch)),0):new Vector3D(0,0,1);if(_up.Length<1e-9)_up=new(0,1,0);Fit();}
    static Vector3D EyeFrom(double yaw,double pitch)=>new(Math.Cos(pitch)*Math.Cos(yaw),Math.Cos(pitch)*Math.Sin(yaw),Math.Sin(pitch));
    /// <summary>Unlimited rotation: horizontal screen motion rotates around the camera "up" axis, vertical around the camera "right" axis; no lock at the poles.</summary>
    void Orbit(double dx,double dy)
    {
        Basis(out var right,out var up);
        var yawRotation=new AxisAngleRotation3D(up,-dx*.45);var pitchRotation=new AxisAngleRotation3D(right,-dy*.45);
        var m=new RotateTransform3D(yawRotation).Value*new RotateTransform3D(pitchRotation).Value;
        _eye=m.Transform(_eye);_up=m.Transform(up);_eye.Normalize();_up.Normalize();UpdateCamera();
    }
    /// <summary>Turns the camera to look from the given direction (normal); the up direction keeps world Z where possible.</summary>
    void LookAlong(Vector3D normal)
    {
        normal.Normalize();_eye=normal;var up=Math.Abs(Vector3D.DotProduct(normal,new Vector3D(0,0,1)))>.98?new Vector3D(0,1,0):new Vector3D(0,0,1);
        up-=normal*Vector3D.DotProduct(up,normal);up.Normalize();_up=up;UpdateCamera();
    }
    void UpdateCamera(){var dir=_eye;dir.Normalize();var up=_up-dir*Vector3D.DotProduct(_up,dir);if(up.Length<1e-9)up=Math.Abs(dir.Z)>.9?new Vector3D(0,1,0):new Vector3D(0,0,1);up.Normalize();_up=up;_camera.Position=_center+dir*(_size*3);_camera.LookDirection=-dir;_camera.UpDirection=up;UpdateLabels();}
    void Basis(out Vector3D right,out Vector3D up){var look=_camera.LookDirection;look.Normalize();right=Vector3D.CrossProduct(look,_camera.UpDirection);right.Normalize();up=Vector3D.CrossProduct(right,look);up.Normalize();}
    Point Screen(Point3D p)=>Project(p,_camera,_viewport.ActualWidth,_viewport.ActualHeight);
    double Depth(Point3D p){var look=_camera.LookDirection;look.Normalize();return Vector3D.DotProduct(p-_camera.Position,look);}
    void UpdateLabels()
    {
        _dimensionCanvas.Children.Clear();foreach(var (a,b,color) in _dimensionSegments)DrawArrow(_dimensionCanvas,Screen(a),Screen(b),color);
        var occupied=new List<Rect>();foreach(var (point,label) in _labels)
        {
            label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));var p=Screen(point);var offset=_labelOffsets.GetValueOrDefault(label);double w=label.DesiredSize.Width,h=label.DesiredSize.Height,x=Math.Clamp(p.X+8+offset.X,0,Math.Max(0,_viewport.ActualWidth-w)),y=Math.Clamp(p.Y-24+offset.Y,0,Math.Max(0,_viewport.ActualHeight-h));var rect=new Rect(x,y,w,h);
            if(offset.Length<.01)for(int i=0;i<10&&occupied.Any(r=>r.IntersectsWith(rect));i++){y=Math.Max(0,y-h-6);rect=new(x,y,w,h);}occupied.Add(rect);Canvas.SetLeft(label,x);Canvas.SetTop(label,y);
            if(_labelWitness.TryGetValue(label,out var witness)){var a=Screen(witness.a);var b=Screen(witness.b);var mid=new Point((a.X+b.X)/2,(a.Y+b.Y)/2);var shift=new Point(x+w/2,y+h/2)-mid;DrawLine(_dimensionCanvas,a,a+shift,label.Foreground,.8);DrawLine(_dimensionCanvas,b,b+shift,label.Foreground,.8);DrawArrow(_dimensionCanvas,a+shift,b+shift,label.Foreground);}
            else DrawLine(_dimensionCanvas,p,new Point(x,y+h/2),label.Foreground,.8);
        }
        UpdateStockLabels();UpdateScenePosition();DrawSavedMeasurements();
    }
    void Label(Point3D p,string value,Brush color){var label=new TextBlock{Text=value,Foreground=color,Background=Brushes.LightGoldenrodYellow,Padding=new Thickness(5,3,5,3),FontWeight=FontWeights.SemiBold,FontSize=13};_labels.Add((p,label));_labelCanvas.Children.Add(label);ConfigureLabelDrag(label);}
    void ClearMeasurement(){_measurements.Children.Clear();_labels.Clear();_labelCanvas.Children.Clear();_dimensionSegments.Clear();_dimensionCanvas.Children.Clear();_labelOffsets.Clear();_labelWitness.Clear();_sceneCanvas.Children.Clear();_sceneDialog=null;}
    void Recolor(){foreach(var (id,model) in _faceModels)model.Material=model.BackMaterial=new DiffuseMaterial(_faceBrushes.GetValueOrDefault(id,FaceBrush));foreach(var model in _edgeModels.Values)model.Material=model.BackMaterial=new DiffuseMaterial(_edgeBrush);for(int i=0;i<_selection.Count;i++){var s=_selection[i];var brush=i==0?Brushes.DodgerBlue:Brushes.DarkOrange;if(s.Kind is "body" or "component"){foreach(var body in BodiesOf(s))foreach(var id in _bodyFaces[body])_faceModels[id].Material=_faceModels[id].BackMaterial=new DiffuseMaterial(brush);}else{var model=s.Kind=="edge"?_edgeModels.GetValueOrDefault(s.Id):s.Kind=="face"?_faceModels.GetValueOrDefault(s.Id):null;if(model!=null)model.Material=model.BackMaterial=new DiffuseMaterial(brush);}}if(_collectStock)foreach(int body in _stockBodies)foreach(int id in _bodyFaces[body])_faceModels[id].Material=_faceModels[id].BackMaterial=new DiffuseMaterial(Brushes.LightSeaGreen);}
    void ClearSelection(){_selectionVersion++;_hoverVersion++;_hovered=null;_hoverInfo.Text="";_directionPick=_measureVectorPick=_collectStock=false;_framePick="z";_viewport.Cursor=null;_selection.Clear();_measureItems.Clear();_function.SelectedIndex=0;_setItem=null;_replaceItem=false;_measureResponse=null;Recolor();RenderSession();DefaultReadout();if(_loaded)_status.Text=L.T("Aktif seçim temizlendi. Tutulan ölçümler ve stok korunur.");}
    Selection? FindCandidate(Point screen,int? forcedMode=null)
    {
        var hits=new List<RayMeshGeometry3DHitTestResult>();VisualTreeHelper.HitTest(_viewport,null,r=>{if(r is RayMeshGeometry3DHitTestResult hit)hits.Add(hit);return HitTestResultBehavior.Continue;},new PointHitTestParameters(screen));
        var face=hits.Where(h=>h.ModelHit is GeometryModel3D m&&_ids.ContainsKey(m)).OrderBy(h=>h.DistanceToRayOrigin).FirstOrDefault();double depth=face==null?double.PositiveInfinity:Depth(face.PointHit);
        var mode=_directionPick?(_framePick=="origin"?2:_directionMode.SelectedIndex==2?1:_directionMode.SelectedIndex==1?-1:0):_mode.SelectedIndex;
        if(forcedMode.HasValue)mode=forcedMode.Value;
        if(!forcedMode.HasValue&&!_directionPick&&_function.SelectedIndex==2&&_pointConstruction.SelectedIndex==1)mode=-2;
        Selection? selected=null;
        if(mode is 2 or 5)selected=NearestVertex(screen,depth);
        if(selected==null&&(mode is 1 or -1 or -2 or 5))selected=NearestEdge(screen,depth,mode is -1 or -2?"Circle":null);
        if(selected==null&&mode!=1&&face?.ModelHit is GeometryModel3D fm)selected=new(mode==2?"facepoint":"face",_ids[fm],face.PointHit);
        if(selected!=null&&(mode is 3 or 4)&&selected.Kind=="face"){int body=_faceBody[selected.Id];selected=new(mode==3?"body":"component",mode==3?body:_bodyComponent[body],selected.Point);}return selected;
    }
    async Task PickAsync(Point screen)
    {
        if(!_loaded)return;var selected=FindCandidate(screen);
        if(selected==null){if(_directionPick)_status.Text=L.T("Uygun geometri seçilemedi; yeniden seçin veya Esc ile iptal edin.");else ClearSelection();return;}
        if(_directionPick){await DirectionAsync(selected);return;}if(_collectStock){CollectStock(selected);return;}_treeBodies=System.Array.Empty<int>();await SelectEntityAsync(selected.Kind,selected.Id,selected.Point);
    }
    Selection? NearestEdge(Point screen,double visibleDepth,string? requiredType=null)
    {
        double best=9;Selection? result=null;
        foreach(var (id,points) in _edgePoints.Where(p=>!_hiddenBodies.Contains(_edgeBody[p.Key])&&(requiredType==null||_edgeTypes[p.Key]==requiredType)))for(int i=1;i<points.Length;i++)
        {
            var a=Screen(points[i-1]);var b=Screen(points[i]);var d=b-a;double t=d.LengthSquared<1e-10?0:Math.Clamp(Vector.Multiply(screen-a,d)/d.LengthSquared,0,1);var p=a+d*t;double distance=(p-screen).Length;
            if(distance>=best)continue;var world=points[i-1]+(points[i]-points[i-1])*t;if(Depth(world)>visibleDepth+_size*.004)continue;best=distance;result=new("edge",id,world);
        }
        return result;
    }
    Selection? NearestVertex(Point screen,double visibleDepth)
    {
        double best=8,bestDepth=double.PositiveInfinity;Selection? result=null;for(int i=0;i<_vertices.Count;i++){if(_hiddenBodies.Contains(_vertexBody[i+1]))continue;var p=_vertices[i];double distance=(Screen(p)-screen).Length,depth=Depth(p);if(depth>visibleDepth+_size*.004||distance>best+.15||distance>8)continue;if(Math.Abs(distance-best)<.15&&depth>=bestDepth)continue;best=distance;bestDepth=depth;result=new("vertex",i+1,p);}return result;
    }
    async Task SelectEntityAsync(string kind,int id,Point3D p)
    {
        await AddMeasureSelectionAsync(new(kind,id,p));
    }
    static string EntityText(JsonElement info)
    {
        var kind=info.GetProperty("kind").GetString();string title=kind switch{"face"=>L.T("Yüz"),"edge"=>L.T("Kenar"),"vertex"=>L.T("Köşe"),"body"=>L.T("Gövde"),"component"=>L.T("Bileşen"),"center"=>L.T("Merkez"),_=>L.T("Nokta")};string text=$"{title} #{info.GetProperty("id")}\n"+(info.TryGetProperty("name",out var part)?part.GetString()+"\n":"")+(info.TryGetProperty("type",out var type)?type.GetString()+"\n":"");
        foreach(var (key,name,unit) in new[]{("volume_mm3",L.T("Hacim"),"mm³"),("area_mm2",L.T("Alan"),"mm²"),("length_mm",L.T("Kenar uzunluğu"),"mm"),("radius_mm",L.T("Yarıçap"),"mm"),("diameter_mm",L.T("Çap"),"mm")})if(info.TryGetProperty(key,out var value))text+=$"{name}: {value.GetDouble():0.######} {unit}\n";
        text+=L.T("CAD noktası: ")+string.Join(", ",info.GetProperty("point").EnumerateArray().Select(v=>v.GetDouble().ToString("0.######")))+" mm";return text;
    }
    async Task DirectionAsync(Selection selected)
    {
        int version=_selectionVersion;var info=await _worker.CallAsync("inspect",selected.Payload,_life.Token);if(_closed||version!=_selectionVersion||!_directionPick)return;
        if(_measureVectorPick){var vectorKey=_directionMode.SelectedIndex==0?"normal":_directionMode.SelectedIndex==1?"axis":"direction";if(!info.TryGetProperty(vectorKey,out var v))throw new InvalidOperationException(L.T("Seçilen yönde normal / eksen / düz kenar bulunamadı."));await AddMeasureVectorAsync(v.EnumerateArray().Select(x=>x.GetDouble()).ToArray());return;}
        if(_framePick=="origin"){_updatingStock=true;_origin.Text=FormatVector(info.GetProperty("point").EnumerateArray().Select(v=>v.GetDouble()).ToArray());_stockOrientation.SelectedIndex=1;_updatingStock=false;_directionPick=false;_framePick="z";_viewport.Cursor=null;ScheduleStockPreview();_status.Text=L.T("CSYS orijini CAD noktasından alındı.");return;}
        var key=_directionMode.SelectedIndex==0?"normal":_directionMode.SelectedIndex==1?"axis":"direction";
        if(!info.TryGetProperty(key,out var direction))throw new InvalidOperationException(_directionMode.SelectedIndex==1?L.T("Silindirik yüz veya daire kenarı seçin."):_directionMode.SelectedIndex==2?L.T("Düz CAD kenarı seçin."):L.T("Yüz normali bu noktada tanımlı değil; başka bir nokta seçin."));
        if(_framePick=="x"){_updatingStock=true;_xDirection.Text=FormatVector(direction.EnumerateArray().Select(x=>x.GetDouble()).ToArray());_stockOrientation.SelectedIndex=1;_updatingStock=false;ScheduleStockPreview();}else SetDirection(direction.EnumerateArray().Select(x=>x.GetDouble()).ToArray());_framePick="z";_directionPick=false;_viewport.Cursor=null;_status.Text=selected.Name+L.T(" üzerinden yön alındı: ")+_direction.Text;
        if(_measureItems.Count>0&&_measureFrame.SelectedIndex==1)await MeasureAsync();
    }
    void SetDirection(double[] d)=>SetFrameZ(d);
    async Task MeasureAsync()=>await EvaluateSessionAsync();
    async Task EnvelopeAsync(bool cylinder)=>await UpdateStockAsync(cylinder);

    async Task CompareRevisionAsync()
    {
        var oldPath = await RevisionLookup.PickOlderAsync(this, _sourcePath);
        if (string.IsNullOrEmpty(oldPath) || !File.Exists(oldPath)) return;
        if (RevisionLookup.ShowDiff != null) { await RevisionLookup.ShowDiff(oldPath, _sourcePath); return; }

        _status.Text = L.T("Önceki revizyonla karşılaştırılıyor...");
        try
        {
            var report = await RevisionService.CompareAsync(oldPath, _sourcePath, () => _worker, _life.Token);
            var win = new RevisionDiffWindow(report) { Owner = this };
            win.Show();
            _status.Text = L.F("Revizyon karşılaştırma tamamlandı: {0} fark bulundu.", report.TotalChanges);
        }
        catch (Exception ex)
        {
            _status.Text = L.T("Karşılaştırma hatası: ") + ex.Message;
        }
    }
}
