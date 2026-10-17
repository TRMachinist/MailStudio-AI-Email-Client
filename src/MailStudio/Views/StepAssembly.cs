using MailStudio.Services;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using static MailStudio.Views.CadSceneGeometry;

namespace MailStudio.Views;

public sealed partial class StepAttachmentWindow
{
    sealed record NavigatorTag(bool Body,int Id,int[] Bodies);
    readonly string _sourcePath;
    readonly TreeView _assemblyTree=new(){BorderThickness=new Thickness(0),Margin=new Thickness(3)};
    readonly TextBlock _assemblyCount=new(){Margin=new Thickness(8),TextWrapping=TextWrapping.Wrap};
    readonly CheckBox _partColors=new(){Content=L.T("Parça renkleri"),IsChecked=true,Margin=new Thickness(8)};
    readonly Dictionary<int,JsonElement> _bodyRecords=new(),_componentRecords=new();
    readonly Dictionary<int,int> _faceBody=new(),_edgeBody=new(),_vertexBody=new(),_bodyComponent=new();
    readonly Dictionary<int,int[]> _bodyFaces=new(),_bodyEdges=new();
    readonly Dictionary<int,Brush> _faceBrushes=new();readonly HashSet<int> _hiddenBodies=new();
    readonly List<(NavigatorTag tag,CheckBox check)> _visibilityChecks=new();bool _syncTree;int[] _treeBodies=System.Array.Empty<int>();
    static readonly Color[] PartPalette={Color.FromRgb(181,199,216),Color.FromRgb(202,187,156),Color.FromRgb(169,198,178),Color.FromRgb(190,179,205),Color.FromRgb(205,173,165),Color.FromRgb(160,197,203),Color.FromRgb(198,198,165)};
    FrameworkElement AssemblyControls()
    {
        var panel=new DockPanel{Background=ThemeManager.Brush("BgSurfaceSecondaryBrush")};var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);panel.Children.Add(top);
        top.Children.Add(new TextBlock{Text=L.T("Montaj Gezgini"),FontWeight=FontWeights.SemiBold,FontSize=15,Margin=new Thickness(8,10,8,2)});top.Children.Add(_assemblyCount);top.Children.Add(_partColors);
        var bar=new WrapPanel{Margin=new Thickness(6)};bar.Children.Add(Button(L.T("Yalnız göster"),()=>IsolateBodies(CurrentBodies())));bar.Children.Add(Button(L.T("Hepsini göster"),()=>{_hiddenBodies.Clear();UpdateVisibility();}));bar.Children.Add(Button(L.T("Gizle"),()=>{foreach(int id in CurrentBodies())_hiddenBodies.Add(id);UpdateVisibility();}));bar.Children.Add(Button(L.T("Seçilene sığdır"),()=>FitBodies(CurrentBodies())));top.Children.Add(bar);panel.Children.Add(_assemblyTree);
        _assemblyTree.SelectedItemChanged+=async(_,e)=>{if(!_loaded||e.NewValue is not TreeViewItem{Tag:NavigatorTag tag})return;_treeBodies=tag.Bodies;var p=BodyCenter(tag.Bodies[0]);var s=new Selection(tag.Body?"body":"component",tag.Id,p);if(_collectStock)CollectStock(s);else await ExecuteAsync(()=>SelectEntityAsync(s.Kind,s.Id,s.Point));};
        _partColors.Checked+=(_,_)=>UpdatePartColors();_partColors.Unchecked+=(_,_)=>UpdatePartColors();return panel;
    }
    void LoadAssembly(JsonElement r)
    {
        foreach(var body in r.GetProperty("bodies").EnumerateArray()){int id=body.GetProperty("id").GetInt32();_bodyRecords[id]=body.Clone();_bodyComponent[id]=body.GetProperty("component_id").GetInt32();_bodyFaces[id]=body.GetProperty("face_ids").EnumerateArray().Select(x=>x.GetInt32()).ToArray();_bodyEdges[id]=body.GetProperty("edge_ids").EnumerateArray().Select(x=>x.GetInt32()).ToArray();}
        foreach(var item in r.GetProperty("components").EnumerateArray())_componentRecords[item.GetProperty("id").GetInt32()]=item.Clone();
        foreach(var f in r.GetProperty("faces").EnumerateArray())_faceBody[f.GetProperty("id").GetInt32()]=f.GetProperty("body_id").GetInt32();
        foreach(var e in r.GetProperty("edges").EnumerateArray())_edgeBody[e.GetProperty("id").GetInt32()]=e.GetProperty("body_id").GetInt32();
        foreach(var v in r.GetProperty("vertices").EnumerateArray())_vertexBody[v.GetProperty("id").GetInt32()]=v.GetProperty("body_id").GetInt32();
        TreeViewItem Item(string title,NavigatorTag tag)
        {
            var row=new DockPanel();var check=new CheckBox{IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0)};row.Children.Add(check);row.Children.Add(new TextBlock{Text=title,VerticalAlignment=VerticalAlignment.Center,ToolTip=title});var result=new TreeViewItem{Header=row,Tag=tag,IsExpanded=true,Margin=new Thickness(0,2,0,2)};_visibilityChecks.Add((tag,check));
            check.Checked+=(_,_)=>VisibilityChanged(tag,true);check.Unchecked+=(_,_)=>VisibilityChanged(tag,false);return result;
        }
        TreeViewItem Node(int id)
        {
            var n=_componentRecords[id];var ids=n.GetProperty("body_ids").EnumerateArray().Select(x=>x.GetInt32()).ToArray();var item=Item(n.GetProperty("name").GetString()!+($"  ({ids.Length})"),new(false,id,ids));
            foreach(var child in n.GetProperty("children").EnumerateArray())item.Items.Add(Node(child.GetInt32()));
            if(!n.GetProperty("is_assembly").GetBoolean()&&ids.Length>1)foreach(int body in ids)item.Items.Add(Item(_bodyRecords[body].GetProperty("name").GetString()!,new(true,body,new[]{body})));
            return item;
        }
        foreach(var n in _componentRecords.Values.Where(n=>n.GetProperty("parent_id").GetInt32()==0))_assemblyTree.Items.Add(Node(n.GetProperty("id").GetInt32()));
        UpdatePartColors();UpdateVisibility(false);RefreshStockObjects();
    }
    void VisibilityChanged(NavigatorTag tag,bool visible){if(_syncTree)return;foreach(int id in tag.Bodies){if(visible)_hiddenBodies.Remove(id);else _hiddenBodies.Add(id);}UpdateVisibility();}
    void UpdateVisibility(bool resetSelection=true)
    {
        if(_bodyRecords.Count==0)return;_model.Children.Clear();_edges.Children.Clear();foreach(var (id,model) in _faceModels)if(!_hiddenBodies.Contains(_faceBody[id]))_model.Children.Add(model);foreach(var (id,model) in _edgeModels)if(!_hiddenBodies.Contains(_edgeBody[id]))_edges.Children.Add(model);
        _syncTree=true;foreach(var (tag,check) in _visibilityChecks){int visible=tag.Bodies.Count(id=>!_hiddenBodies.Contains(id));check.IsChecked=visible==0?false:visible==tag.Bodies.Length?true:null;}_syncTree=false;
        _assemblyCount.Text=L.F("{0} bileşen · {1} / {2} gövde görünür", _componentRecords.Values.Count(n=>!n.GetProperty("is_assembly").GetBoolean()), _bodyRecords.Count-_hiddenBodies.Count, _bodyRecords.Count);
        if(resetSelection)ClearSelection();if(_stockScope.SelectedIndex==1)ScheduleStockPreview();
    }
    void UpdatePartColors()
    {
        foreach(var (face,id) in _faceBody)
        {
            var record=_bodyRecords[id];Color c=PartPalette[(id-1)%PartPalette.Length];if(record.TryGetProperty("color",out var rgb)&&rgb.ValueKind==JsonValueKind.Array){var a=rgb.EnumerateArray().Select(v=>(byte)Math.Clamp(Math.Round(v.GetDouble()*255),0,255)).ToArray();c=Color.FromRgb(a[0],a[1],a[2]);}
            _faceBrushes[face]=_partColors.IsChecked==true?new SolidColorBrush(c):FaceBrush;
        }
        Recolor();
    }
    IEnumerable<int> BodiesOf(Selection s)=>(s.Kind=="center"?s.SourceKind:s.Kind) switch{"body"=>new[]{s.Id},"component"=>_componentRecords[s.Id].GetProperty("body_ids").EnumerateArray().Select(v=>v.GetInt32()).ToArray(),"face" or "facepoint"=>new[]{_faceBody[s.Id]},"edge" or "edgepoint"=>new[]{_edgeBody[s.Id]},_=>new[]{_vertexBody[s.Id]}};
    int[] CurrentBodies()=>_treeBodies.Length>0?_treeBodies:_selection.SelectMany(BodiesOf).Distinct().ToArray();
    Point3D BodyCenter(int id){var b=_bodyRecords[id].GetProperty("bounds").EnumerateArray().Select(x=>x.GetDouble()).ToArray();return new((b[0]+b[3])/2,(b[1]+b[4])/2,(b[2]+b[5])/2);}
    string DisplayName(Selection s)=>s.Kind=="body"?_bodyRecords[s.Id].GetProperty("name").GetString()!:s.Kind=="component"?_componentRecords[s.Id].GetProperty("name").GetString()!:s.Name;
    void IsolateBodies(int[] ids){if(ids.Length==0){_status.Text=L.T("Önce montajdan bir bileşen/gövde seçin.");return;}_hiddenBodies.Clear();foreach(int id in _bodyRecords.Keys.Except(ids))_hiddenBodies.Add(id);UpdateVisibility();FitBodies(ids);}
    void FitBodies(int[] ids)
    {
        var b=Rect3D.Empty;foreach(int id in ids)foreach(int face in _bodyFaces[id])b.Union(_faceModels[face].Bounds);if(b.IsEmpty)return;_center=new(b.X+b.SizeX/2,b.Y+b.SizeY/2,b.Z+b.SizeZ/2);_size=Math.Max(1,Math.Sqrt(b.SizeX*b.SizeX+b.SizeY*b.SizeY+b.SizeZ*b.SizeZ));_camera.Width=_size*1.1*Math.Max(1,_viewport.ActualWidth/Math.Max(1,_viewport.ActualHeight));UpdateCamera();
    }
}
