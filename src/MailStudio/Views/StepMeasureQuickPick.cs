using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Media3D;
using static MailStudio.Views.CadSceneGeometry;

namespace MailStudio.Views;

public sealed partial class StepAttachmentWindow
{
    ContextMenu? _quickPickMenu;
    async Task ShowQuickPickAsync(Point screen)
    {
        if(!_loaded)return;var face=FindCandidate(screen,0);if(face==null){ClearSelection();return;}var menu=new ContextMenu{Placement=PlacementMode.MousePoint,PlacementTarget=_viewport};_quickPickMenu=menu;
        void Add(string title,Selection selection){var item=new MenuItem{Header=title};item.Click+=async(_,_)=>await ExecuteAsync(async()=>{if(_collectStock)CollectStock(selection);else await SelectEntityAsync(selection.Kind,selection.Id,selection.Point);});menu.Items.Add(item);}
        Add(face.Name,face);int body=_faceBody[face.Id];var depth=Depth(face.Point);var edge=NearestEdge(screen,depth);var vertex=NearestVertex(screen,depth);if(edge!=null)Add(edge.Name,edge);if(vertex!=null)Add(vertex.Name,vertex);
        var inspected=edge??face;var info=await _worker.CallAsync("inspect",inspected.Payload,_life.Token);if(_closed)return;
        if(info.TryGetProperty("center",out var center))Add(L.T("Analitik merkez noktası"),new("center",inspected.Id,Point(center),inspected.Kind));
        if(info.TryGetProperty("axis",out var axis)){var direction=axis.EnumerateArray().Select(x=>x.GetDouble()).ToArray();var item=new MenuItem{Header=L.T("Eksen yönü → Ölçüm vektörü")};item.Click+=async(_,_)=>await ExecuteAsync(()=>AddMeasureVectorAsync(direction));menu.Items.Add(item);}
        menu.Items.Add(new Separator());Add(L.T("Gövde · ")+_bodyRecords[body].GetProperty("name").GetString(),new("body",body,face.Point));int component=_bodyComponent[body];Add(L.T("Bileşen · ")+_componentRecords[component].GetProperty("name").GetString(),new("component",component,face.Point));
        var clear=new MenuItem{Header=L.T("Aktif seçimi temizle (Esc)")};clear.Click+=(_,_)=>ClearSelection();menu.Items.Add(new Separator());menu.Items.Add(clear);menu.IsOpen=true;
    }
}
