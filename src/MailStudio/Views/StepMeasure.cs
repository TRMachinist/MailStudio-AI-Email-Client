using MailStudio.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using static MailStudio.Views.CadSceneGeometry;

namespace MailStudio.Views;

public sealed partial class StepAttachmentWindow
{
    sealed class MeasureItem
    {
        public string Role="object";public List<Selection> Members=new();public double[]? Direction;public string Name="";
        public object Payload=>new{role=Role,name=Name,members=Members.Select(s=>s.Payload).ToArray(),direction=Direction};
        public override string ToString()=>Name;
    }
    sealed record ResultLine(string Measure,string Value,string Unit);
    readonly ObservableCollection<MeasureItem> _measureItems=new();
    readonly ListBox _objectList=new(){MinHeight=60,MaxHeight=115,Margin=new Thickness(0,4,0,4)};
    readonly ComboBox _function=new(){ItemsSource=new[]{L.T("Object · Nesne"),L.T("Object Set · Nesne kümesi"),L.T("Point · Nokta"),L.T("Vector · Yön")},SelectedIndex=0,Margin=new Thickness(0,3,0,5)};
    readonly ComboBox _method=new(){ItemsSource=new[]{L.T("Free · Otomatik"),L.T("Individual · Tek tek"),L.T("Pairs · Çiftler"),L.T("Chain · Zincir"),L.T("From Reference · İlk nesneden")},SelectedIndex=0,Margin=new Thickness(0,4,0,7)};
    readonly ComboBox _pointConstruction=new(){ItemsSource=new[]{L.T("CAD köşesi / yüz noktası"),L.T("Daire / küre merkezi")},SelectedIndex=0,Margin=new Thickness(0,3,0,4),Visibility=Visibility.Collapsed};
    readonly ComboBox _units=new(){ItemsSource=new[]{"mm","in"},SelectedIndex=0,Width=55},_precision=new(){ItemsSource=new[]{0,1,2,3,4,5,6},SelectedIndex=3,Width=45};
    readonly ComboBox _resultChoice=new(){MinWidth=180,Margin=new Thickness(0,4,0,4)},_distanceChoice=new(){Margin=new Thickness(0,3,0,4),DisplayMemberPath="Label"};
    readonly ComboBox _measureFrame=new(){ItemsSource=new[]{L.T("Absolute · Dünya XYZ"),L.T("Stok CSYS · X/Z/orijin")},SelectedIndex=0,Margin=new Thickness(0,4,0,5)};
    readonly DataGrid _resultGrid=new(){IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,HeadersVisibility=DataGridHeadersVisibility.Column,MinHeight=90,MaxHeight=235,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,SelectionMode=DataGridSelectionMode.Single,BorderThickness=new Thickness(0)};
    readonly TextBlock _measureHint=new(){Text=L.T("Nesne seçin. Uygun ölçüler otomatik hesaplanır."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,7),Foreground=ThemeManager.Brush("TextSecondaryBrush")};
    readonly Dictionary<string,CheckBox> _resultFilters=new();
    readonly CheckBox _hoverEnabled=new(){Content=L.T("Üzerine gelince ölçü önizle"),IsChecked=true,Margin=new Thickness(0,5,0,5)};
    readonly TextBlock _hoverInfo=new(){TextWrapping=TextWrapping.Wrap,Foreground=ThemeManager.Brush("TextPrimaryBrush"),Margin=new Thickness(0,3,0,3)};
    readonly DispatcherTimer _hoverTimer=new(){Interval=TimeSpan.FromMilliseconds(220)};
    Point _hoverScreen;Selection? _hovered;int _hoverVersion;bool _replaceItem,_renderingResults,_measureVectorPick;MeasureItem? _setItem;JsonElement? _measureResponse;
    sealed record DistanceOption(string Key,string Label);
    FrameworkElement MeasureControls()
    {
        var p=new StackPanel{Margin=new Thickness(10)};
        void Heading(string title)=>p.Children.Add(new TextBlock{Text=title,FontWeight=FontWeights.SemiBold,Foreground=ThemeManager.Brush("TextPrimaryBrush"),Margin=new Thickness(0,8,0,3)});
        p.Children.Add(new TextBlock{Text="Measure",FontWeight=FontWeights.SemiBold,FontSize=19});
        Heading(L.T("Function · Seçimin görevi"));p.Children.Add(_function);p.Children.Add(_pointConstruction);
        var selection=new WrapPanel();selection.Children.Add(Button(L.T("Yeni nesne"),()=>{_setItem=null;_replaceItem=false;_function.SelectedIndex=0;}));selection.Children.Add(Button(L.T("Değiştir"),()=>{if(_objectList.SelectedItem is not MeasureItem item)return;if(item.Role=="vector"){_function.SelectedIndex=3;return;}if(item.Role=="set"){_function.SelectedIndex=1;_setItem=item;_measureHint.Text=L.T("Kümeye geometri ekleyin; tekrar seçmek kümeden çıkarır.");return;}_replaceItem=true;_setItem=null;_measureHint.Text=L.T("Listedeki nesnenin yerine yeni geometri seçin.");}));selection.Children.Add(Button(L.T("Kaldır"),async()=>await ExecuteAsync(RemoveMeasureItemAsync)));selection.Children.Add(Button("↑",async()=>await ExecuteAsync(()=>MoveMeasureItemAsync(-1))));selection.Children.Add(Button("↓",async()=>await ExecuteAsync(()=>MoveMeasureItemAsync(1))));p.Children.Add(selection);
        _objectList.ItemsSource=_measureItems;p.Children.Add(_objectList);
        Heading(L.T("Measurement Method · Ölçüm yöntemi"));p.Children.Add(_method);
        Heading(L.T("Result Filters · Gösterilecek sonuçlar"));var filters=new WrapPanel();
        foreach(var (key,title) in new[]{("distance",L.T("Mesafe")),("angle",L.T("Açı")),("length",L.T("Uzunluk")),("radial","R / Ø"),("area",L.T("Alan")),("volume",L.T("Hacim")),("coordinates","XYZ / CG")}){var cb=new CheckBox{Content=title,IsChecked=key!="coordinates",Margin=new Thickness(0,3,10,3)};_resultFilters[key]=cb;filters.Children.Add(cb);cb.Checked+=(_,_)=>RenderSession(false);cb.Unchecked+=(_,_)=>RenderSession(false);}p.Children.Add(filters);
        var settings=new Expander{Header=L.T("Ayarlar · birim / hassasiyet / koordinat sistemi"),Margin=new Thickness(0,7,0,5)};var sp=new StackPanel();var row=new WrapPanel();row.Children.Add(new TextBlock{Text=L.T("Birim"),Margin=new Thickness(0,4,8,0)});row.Children.Add(_units);row.Children.Add(new TextBlock{Text=L.T("Ondalık"),Margin=new Thickness(10,4,8,0)});row.Children.Add(_precision);sp.Children.Add(row);sp.Children.Add(_measureFrame);sp.Children.Add(_hoverEnabled);sp.Children.Add(new TextBlock{Text=L.T("Vektör eklemek için Function → Vector seçin, üstte normal / eksen / kenar türünü belirleyip geometriye tıklayın."),TextWrapping=TextWrapping.Wrap,Foreground=ThemeManager.Brush("TextSecondaryBrush")});settings.Content=sp;p.Children.Add(settings);
        p.Children.Add(_measureHint);p.Children.Add(_hoverInfo);
        Heading(L.T("Results · Sonuçlar"));p.Children.Add(_resultChoice);p.Children.Add(_distanceChoice);
        ScrollViewer.SetHorizontalScrollBarVisibility(_resultGrid,ScrollBarVisibility.Disabled);
        _resultGrid.Columns.Add(new DataGridTextColumn{Header=L.T("Ölçü"),Binding=new System.Windows.Data.Binding("Measure"),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});_resultGrid.Columns.Add(new DataGridTextColumn{Header=L.T("Değer"),Binding=new System.Windows.Data.Binding("Value"),Width=85});_resultGrid.Columns.Add(new DataGridTextColumn{Header=L.T("Birim"),Binding=new System.Windows.Data.Binding("Unit"),Width=45});p.Children.Add(_resultGrid);
        var actions=new WrapPanel{Margin=new Thickness(0,8,0,0)};actions.Children.Add(Button(L.T("Ölçümü tut"),PinMeasurement));actions.Children.Add(Button(L.T("Yeni ölçüm"),ClearSelection));actions.Children.Add(Button(L.T("Kopyala"),()=>{if(!string.IsNullOrWhiteSpace(_result.Text))Clipboard.SetText(_result.Text);}));p.Children.Add(actions);p.Children.Add(SavedMeasureControls());
        _function.SelectionChanged+=(_,_)=>{_setItem=null;_replaceItem=false;_pointConstruction.Visibility=_function.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;if(_function.SelectedIndex==2)_mode.SelectedIndex=2;if(_function.SelectedIndex==3){_measureVectorPick=true;_directionPick=true;_framePick="measure";_viewport.Cursor=Cursors.Cross;_measureHint.Text=L.T("Ölçüm vektörü için yüz / eksen / düz kenar seçin.");}else if(_measureVectorPick){_directionPick=false;_measureVectorPick=false;_viewport.Cursor=null;}};
        _method.SelectionChanged+=async(_,_)=>await ExecuteAsync(EvaluateSessionAsync);
        _measureFrame.SelectionChanged+=async(_,_)=>await ExecuteAsync(EvaluateSessionAsync);
        _units.SelectionChanged+=(_,_)=>RenderSession(false);_precision.SelectionChanged+=(_,_)=>RenderSession(false);
        _resultChoice.SelectionChanged+=(_,_)=>{if(!_renderingResults)RenderSession(false);};
        _distanceChoice.SelectionChanged+=(_,_)=>{if(!_renderingResults)RenderSession(false);};
        _hoverTimer.Tick+=async(_,_)=>{_hoverTimer.Stop();await ExecuteAsync(PreviewHoverAsync);};
        return new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    static bool SameSelection(Selection a,Selection b)=>a.Kind==b.Kind&&a.Id==b.Id&&a.SourceKind==b.SourceKind&&(a.Kind is "body" or "component" or "face" or "edge" or "vertex" or "center"||(a.Point-b.Point).Length<1e-6);
    void SyncMeasureItems(){_selection.Clear();_selection.AddRange(_measureItems.SelectMany(i=>i.Members));_objectList.Items.Refresh();Recolor();}
    async Task AddMeasureSelectionAsync(Selection candidate)
    {
        _collectStock=false;_selectionVersion++;
        if(_function.SelectedIndex==2&&_pointConstruction.SelectedIndex==1&&candidate.Kind!="center")
        {var source=candidate.Kind is "facepoint"?"face":candidate.Kind is "edgepoint"?"edge":candidate.Kind;var info=await _worker.CallAsync("inspect",new{kind=source,id=candidate.Id,point=Array(candidate.Point)},_life.Token);if(!info.TryGetProperty("center",out var center))throw new InvalidOperationException(L.T("Daire kenarı, silindirik yüz veya küre seçin; bu nesnede analitik merkez yok."));candidate=new("center",candidate.Id,Point(center),source);}
        if(_replaceItem&&_objectList.SelectedItem is MeasureItem replaced){replaced.Role=_function.SelectedIndex==2?"point":"object";replaced.Direction=null;replaced.Members.Clear();replaced.Members.Add(candidate);replaced.Name=DisplayName(candidate);_replaceItem=false;}
        else if(_function.SelectedIndex==1)
        {
            if(_setItem==null||!_measureItems.Contains(_setItem)){_setItem=new(){Role="set"};_measureItems.Add(_setItem);}var old=_setItem.Members.FirstOrDefault(s=>SameSelection(s,candidate));if(old!=null)_setItem.Members.Remove(old);else _setItem.Members.Add(candidate);_setItem.Name=L.F("Nesne kümesi ({0})", _setItem.Members.Count);if(_setItem.Members.Count==0){_measureItems.Remove(_setItem);_setItem=null;}
        }
        else
        {
            var old=_measureItems.FirstOrDefault(i=>i.Role!="set"&&i.Members.Count==1&&SameSelection(i.Members[0],candidate));if(old!=null)_measureItems.Remove(old);else _measureItems.Add(new(){Role=_function.SelectedIndex==2?"point":"object",Members=new(){candidate},Name=DisplayName(candidate)});
        }
        SyncMeasureItems();_tabs.SelectedIndex=0;await EvaluateSessionAsync();
    }
    async Task RemoveMeasureItemAsync(){if(_objectList.SelectedItem is MeasureItem item){_measureItems.Remove(item);if(item==_setItem)_setItem=null;_selectionVersion++;SyncMeasureItems();await EvaluateSessionAsync();}}
    async Task MoveMeasureItemAsync(int delta){int old=_objectList.SelectedIndex,next=old+delta;if(old<0||next<0||next>=_measureItems.Count)return;_measureItems.Move(old,next);_objectList.SelectedIndex=next;_selectionVersion++;SyncMeasureItems();await EvaluateSessionAsync();}
    async Task AddMeasureVectorAsync(double[] vector){var existing=_measureItems.FirstOrDefault(i=>i.Role=="vector");if(existing!=null)_measureItems.Remove(existing);_measureItems.Add(new(){Role="vector",Direction=vector,Name=L.T("Vektör [")+FormatVector(vector)+"]"});_directionPick=_measureVectorPick=false;_framePick="z";_viewport.Cursor=null;_function.SelectedIndex=0;_selectionVersion++;await EvaluateSessionAsync();}
    async Task EvaluateSessionAsync()
    {
        if(!_loaded)return;int version=++_selectionVersion;
        if(!_measureItems.Any(i=>i.Members.Count>0)){_measureResponse=null;RenderSession();return;}
        _measureHint.Text=L.T("Native CAD ölçüleri hesaplanıyor…");bool custom=_measureFrame.SelectedIndex==1;try{var r=await _worker.CallAsync("measure_session",new{items=_measureItems.Select(i=>i.Payload).ToArray(),method=new[]{"free","individual","pairs","chain","reference"}[_method.SelectedIndex],csys_z=custom?Triple(_direction.Text):new[]{0d,0,1},csys_x=custom?Triple(_xDirection.Text):new[]{1d,0,0},origin=custom?OriginValues():new[]{0d,0,0}},_life.Token);
        if(_closed||version!=_selectionVersion)return;_measureResponse=r;RenderSession();_status.Text=L.T("Ölçüler seçim listesi ve yöntemden native CAD geometrisiyle hesaplandı.");}catch{if(!_closed&&version==_selectionVersion){_measureResponse=null;RenderSession();_measureHint.Text=L.T("Geçerli ölçüm hesaplanamadı. Seçimi ve koordinat sistemini kontrol edin.");}throw;}
    }
    bool Filter(string category)=>_resultFilters.TryGetValue(category,out var cb)&&cb.IsChecked==true;
    (string value,string unit) DisplayValue(JsonElement row)
    {
        string unit=row.GetProperty("unit").GetString()!;double v=row.GetProperty("value").GetDouble();if(_units.SelectedIndex==1){if(unit=="mm"){v/=25.4;unit="in";}else if(unit=="mm²"){v/=25.4*25.4;unit="in²";}else if(unit=="mm³"){v/=25.4*25.4*25.4;unit="in³";}}return(v.ToString("F"+_precision.SelectedIndex,CultureInfo.CurrentCulture),unit);
    }
    void RenderSession(bool refreshChoices=true)
    {
        if(_renderingResults)return;_renderingResults=true;
        try
        {
            ClearMeasurement();_sceneCanvas.Children.Clear();_resultGrid.ItemsSource=null;_result.Text="";
            if(_measureResponse is not {} response){_resultChoice.ItemsSource=null;_distanceChoice.ItemsSource=null;_measureHint.Text=L.T("Nesneleri tek listeye seçin; uygun ölçüler otomatik gösterilir.");return;}
            var all=response.GetProperty("results").EnumerateArray().ToArray();
            if(refreshChoices){int prior=_resultChoice.Items.Count==all.Length?_resultChoice.SelectedIndex:-1;_resultChoice.ItemsSource=all.Select(r=>r.GetProperty("title").GetString()).ToArray();_resultChoice.SelectedIndex=all.Length==0?-1:Math.Clamp(prior<0?all.Length-1:prior,0,all.Length-1);_distanceChoice.Tag=null;}
            int index=Math.Clamp(_resultChoice.SelectedIndex,0,Math.Max(0,all.Length-1));if(all.Length==0)return;var selected=all[index];
            var rows=selected.GetProperty("rows").EnumerateArray().Where(r=>Filter(r.GetProperty("category").GetString()!)).ToArray();var distances=rows.Where(r=>r.GetProperty("category").GetString()=="distance").ToArray();string identity=index.ToString();
            string? key=(_distanceChoice.SelectedItem as DistanceOption)?.Key;
            if((string?)_distanceChoice.Tag!=identity||refreshChoices){_distanceChoice.ItemsSource=distances.Select(r=>new DistanceOption(r.GetProperty("key").GetString()!,r.GetProperty("label").GetString()!)).ToArray();_distanceChoice.SelectedIndex=0;_distanceChoice.Tag=identity;key=(_distanceChoice.SelectedItem as DistanceOption)?.Key;}
            _distanceChoice.Visibility=distances.Length>1?Visibility.Visible:Visibility.Collapsed;
            var primary=distances.FirstOrDefault(r=>r.GetProperty("key").GetString()==key);var visible=rows.Where(r=>r.GetProperty("category").GetString()!="distance"||r.GetProperty("key").GetString()==key).ToArray();
            _resultGrid.ItemsSource=visible.Select(r=>{var f=DisplayValue(r);return new ResultLine(r.GetProperty("label").GetString()!,f.value,f.unit);}).ToArray();
            _result.Text=selected.GetProperty("title").GetString()+"\n"+string.Join("\n",visible.Select(r=>{var f=DisplayValue(r);return r.GetProperty("label").GetString()+": "+f.value+" "+f.unit;}));
            _measureHint.Text=response.GetProperty("hint").GetString();if(primary.ValueKind==JsonValueKind.Object&&primary.TryGetProperty("note",out var note))_measureHint.Text+="\n"+note.GetString();
            RenderActiveMeasurement(selected,visible,primary);BuildSceneDialog(selected,visible,distances,key);UpdateLabels();
        }
        finally{_renderingResults=false;}
    }
}
