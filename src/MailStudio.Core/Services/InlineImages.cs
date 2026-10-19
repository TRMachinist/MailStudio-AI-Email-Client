using System.Net;
using System.Text.RegularExpressions;

namespace MailStudio.Services;

public static class InlineImages
{
    public static bool References(string html,string? id,string? location)=>Replace(html,id,location,"image/png",Array.Empty<byte>())!=html;
    public static string Replace(string html,string? contentId,string? location,string mime,byte[] bytes)
    {
        if(!mime.StartsWith("image/",StringComparison.OrdinalIgnoreCase))return html;
        var data=$"data:{mime.ToLowerInvariant()};base64,{Convert.ToBase64String(bytes)}";
        string Normalize(string value){try{return Uri.UnescapeDataString(WebUtility.HtmlDecode(value)).Trim('<','>');}catch{return value;}}
        var id=Normalize(contentId??"");var loc=Normalize(location??"");
        string? Resolve(string source)
        {
            var value=Normalize(source);
            var match=id.Length>0&&value.StartsWith("cid:",StringComparison.OrdinalIgnoreCase)&&Normalize(value[4..]).Equals(id,StringComparison.OrdinalIgnoreCase)
                || loc.Length>0&&value.Equals(loc,StringComparison.OrdinalIgnoreCase);
            return match?data:null;
        }
        var document=new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);var changed=false;
        foreach(var element in document.QuerySelectorAll("[src],[background]"))foreach(var attribute in new[]{"src","background"})
            if(element.GetAttribute(attribute) is {} original && Resolve(original) is {} resolved){element.SetAttribute(attribute,resolved);changed=true;}
        foreach(var element in document.QuerySelectorAll("[style]"))
        {
            var original=element.GetAttribute("style")!;
            var replacement=Regex.Replace(original,"url\\(\\s*['\"]?(?<uri>[^)'\"]+)['\"]?\\s*\\)",m=>Resolve(m.Groups["uri"].Value.Trim()) is {} resolved?"url('"+resolved+"')":m.Value,RegexOptions.IgnoreCase);
            if(replacement!=original){element.SetAttribute("style",replacement);changed=true;}
        }
        return changed?document.DocumentElement!.OuterHtml:html;
    }
    public static bool HasRemote(string html)=>Regex.IsMatch(html,@"(?:src\s*=\s*['"" ]*(?:https?:)?//|url\(\s*['"" ]*(?:https?:)?//)",RegexOptions.IgnoreCase);
}
