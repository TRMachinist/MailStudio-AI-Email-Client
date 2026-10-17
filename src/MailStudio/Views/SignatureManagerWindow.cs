using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MailStudio.Models;
using MailStudio.Services;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace MailStudio.Views;

public sealed class SignatureManagerWindow : Window
{
    readonly ComboBox _list=new(){MinWidth=180,DisplayMemberPath="Name",Margin=new Thickness(0,0,8,0)};
    readonly TextBox _name=new(){MinWidth=160,Margin=new Thickness(0,0,8,0)};
    readonly CheckBox _default=new(){Content=L.T("Varsayılan imza"),VerticalAlignment=VerticalAlignment.Center};
    readonly WebView2 _editor=new(){DefaultBackgroundColor=ThemeManager.IsDark?System.Drawing.Color.FromArgb(0x1E,0x1E,0x1E):System.Drawing.Color.White};
    readonly List<MailSignature> _signatures;
    MailSignature? _selected;bool _ready,_switching,_closed;
    readonly CancellationTokenSource _life=new();
    public IReadOnlyList<MailSignature> Signatures => _signatures;
    public string? DefaultId {get;private set;}
    public SignatureManagerWindow(Window owner,AccountConfig account)
    {
        Owner=owner;Title=L.T("İmzalar — ")+account.Email;Width=840;Height=550;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _signatures=account.Signatures.Select(s=>new MailSignature{Id=s.Id,Name=s.Name,Html=s.Html}).ToList();DefaultId=account.DefaultSignatureId;
        if(_signatures.Count==0 && !string.IsNullOrWhiteSpace(account.Signature)) {var legacy=new MailSignature{Name=L.T("Mevcut imza"),Html=account.Signature};_signatures.Add(legacy);DefaultId=legacy.Id;}
        var grid=new Grid{Margin=new Thickness(12)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=grid;
        var bar=new WrapPanel {Margin=new Thickness(0,0,0,8)};grid.Children.Add(bar);bar.Children.Add(_list);
        Button Add(string title,Action action) {var b=new Button{Content=title,Padding=new Thickness(10,4,10,4),Margin=new Thickness(0,0,6,0)};b.Click+=(_,_)=>action();return b;}
        bar.Children.Add(Add(L.T("Yeni"),async()=>{await SaveCurrentAsync();var s=new MailSignature{Name=L.T("Yeni imza")};_signatures.Add(s);Reload(s);}));
        bar.Children.Add(Add(L.T("Kaldır"),()=>{if(_selected==null)return;_signatures.Remove(_selected);if(DefaultId==_selected.Id)DefaultId=null;_selected=null;Reload(_signatures.FirstOrDefault());}));
        var tools=new WrapPanel{Margin=new Thickness(0,0,0,8)};Grid.SetRow(tools,1);grid.Children.Add(tools);tools.Children.Add(_name);tools.Children.Add(_default);
        tools.Children.Add(Add(L.T("Kalın"),async()=>await Js("ms.exec('bold')")));tools.Children.Add(Add(L.T("İtalik"),async()=>await Js("ms.exec('italic')")));
        tools.Children.Add(Add(L.T("Resim ekle"),async()=>{var d=new OpenFileDialog{Filter="Resimler|*.png;*.jpg;*.jpeg;*.gif;*.bmp"};if(d.ShowDialog(this)!=true)return;var ext=System.IO.Path.GetExtension(d.FileName).TrimStart('.').ToLowerInvariant().Replace("jpg","jpeg");var data=Convert.ToBase64String(await System.IO.File.ReadAllBytesAsync(d.FileName));await Js("ms.insertHtml("+JsonSerializer.Serialize($"<img src=\"data:image/{ext};base64,{data}\">")+")");}));
        Grid.SetRow(_editor,2);grid.Children.Add(_editor);
        var save=Add(L.T("Kaydet"),async()=>{try{await SaveCurrentAsync();if(!_closed)DialogResult=true;}catch(Exception ex){if(!_closed)MessageBox.Show(this,ex.Message,L.T("İmza kaydedilemedi"));}});save.IsEnabled=false;
        var close=Add(L.T("Vazgeç"),()=>Close());var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};footer.Children.Add(close);footer.Children.Add(save);Grid.SetRow(footer,3);grid.Children.Add(footer);
        _list.SelectionChanged+=async(_,_)=>{if(_switching||!_ready||_closed)return;_list.IsEnabled=false;try{await SaveCurrentAsync();if(!_closed)await SelectAsync(_list.SelectedItem as MailSignature);}catch(Exception ex){if(!_closed)MessageBox.Show(this,ex.Message,L.T("İmza"));}finally{if(!_closed)_list.IsEnabled=true;}};
        Loaded+=async(_,_)=>
        {
            try
            {
                await _editor.EnsureCoreWebView2Async(await MailHtml.EnvAsync());if(_closed)return;
                var core=_editor.CoreWebView2;core.Settings.AreDevToolsEnabled=false;core.Settings.AreHostObjectsAllowed=false;
                core.AddWebResourceRequestedFilter("*",Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>{if(!e.Request.Uri.StartsWith("data:")&&!e.Request.Uri.StartsWith("about:"))e.Response=core.Environment.CreateWebResourceResponse(null,403,"Blocked","");};
                core.NavigationStarting+=(_,e)=>{if(_ready)e.Cancel=true;};core.NewWindowRequested+=(_,e)=>e.Handled=true;
                core.PermissionRequested+=(_,e)=>e.State=Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Deny;
                core.DownloadStarting+=(_,e)=>e.Cancel=true;
                core.WebMessageReceived+=async(_,e)=>
                {
                    if(_closed)return;
                    try{using var message=JsonDocument.Parse(e.TryGetWebMessageAsString());if(message.RootElement.GetProperty("t").GetString()=="paste")await Js("ms.insertHtml("+JsonSerializer.Serialize(EditorContent.Sanitize(message.RootElement.GetProperty("html").GetString()))+")");}catch(Exception ex){if(!_closed)Title=L.T("İmza: ")+ex.Message;}
                };
                var done=new TaskCompletionSource();core.NavigationCompleted+=(_,_)=>done.TrySetResult();_editor.NavigateToString(EditorHtml.Page);await done.Task.WaitAsync(_life.Token);if(_closed)return;
                _ready=true;if(_signatures.Count==0)_signatures.Add(new(){Name=L.T("İmzam")});Reload(_signatures.FirstOrDefault(s=>s.Id==DefaultId)??_signatures[0]);save.IsEnabled=true;
            }
            catch(Exception ex){if(!_closed)Title=L.T("İmza editörü açılamadı: ")+ex.Message;}
        };
        Closed+=(_,_)=>{_closed=true;_life.Cancel();_editor.Dispose();};
    }
    async Task Js(string value){if(_ready&&!_closed)await _editor.CoreWebView2.ExecuteScriptAsync(value);}
    async Task SaveCurrentAsync(){if(!_ready||_selected==null)return;_selected.Name=string.IsNullOrWhiteSpace(_name.Text)?L.T("İmza"):_name.Text.Trim();var raw=await _editor.CoreWebView2.ExecuteScriptAsync("ms.draftHtml()");_selected.Html=EditorContent.Sanitize(JsonSerializer.Deserialize<string>(raw));if(_default.IsChecked==true)DefaultId=_selected.Id;else if(DefaultId==_selected.Id)DefaultId=null;}
    void Reload(MailSignature? selected){_switching=true;_list.ItemsSource=null;_list.ItemsSource=_signatures;_list.SelectedItem=selected;_switching=false;_ = SelectAsync(selected);}
    async Task SelectAsync(MailSignature? s){_selected=s;_name.Text=s?.Name??"";_default.IsChecked=s!=null&&s.Id==DefaultId;var h=s?.Html??"";if(!h.TrimStart().StartsWith('<'))h="<div>"+System.Net.WebUtility.HtmlEncode(h).Replace("\n","<br>")+"</div>";await Js("ms.setDraft("+JsonSerializer.Serialize(EditorContent.Sanitize(h))+")");}
}
