using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MailStudio.Views;

/// <summary>
/// Hosts a Windows preview handler (the IPreviewHandler used by Explorer's preview pane) in a WPF area.
/// Word, Excel, PowerPoint, Visio, Outlook (.msg) and other handlers registered on the system render their own formats.
/// The document is read-only; the handler runs in a separate process (prevhost.exe / Office).
/// </summary>
public sealed class PreviewHandlerHost : HwndHost
{
    const string PreviewHandlerGuid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    IntPtr _hwnd; object? _handler; string? _path; IStream? _stream;
    public string? Error { get; private set; }
    public string? HandlerClsid { get; private set; }

    public static string? FindHandler(string extension)
    {
        try
        {
            uint size = 260; var sb = new StringBuilder((int)size);
            var hr = AssocQueryString(0x40 /*ASSOCF_INIT_DEFAULTTOSTAR*/ , 16 /*ASSOCSTR_SHELLEXTENSION*/, extension, PreviewHandlerGuid, sb, ref size);
            if (hr == 0 && sb.Length > 0) return sb.ToString();
        }
        catch { }
        return null;
    }

    public bool Open(string path)
    {
        _path = path;
        HandlerClsid = FindHandler(Path.GetExtension(path));
        if (HandlerClsid == null) { Error = L.T("Bu dosya türü için Windows'ta kayıtlı bir önizleme işleyicisi yok."); return false; }
        return true;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(0, "static", "", 0x40000000 /*WS_CHILD*/ | 0x10000000 /*WS_VISIBLE*/ | 0x02000000 /*WS_CLIPCHILDREN*/, 0, 0, 10, 10, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _hwnd);
    }

    /// <summary>Called after the window is created; the Office handler may take a few seconds on first start.</summary>
    public bool StartPreview()
    {
        if (_path == null || HandlerClsid == null || _hwnd == IntPtr.Zero) return false;
        var errors = new List<string>();
        // Handlers support different initialization interfaces; we try them in Explorer's order with a clean instance each time.
        foreach (var mode in new[] { "file", "stream", "item" })
            foreach (var ctx in new uint[] { 4 /*LOCAL_SERVER*/, 1 /*INPROC*/ })
            {
                string step = L.T("oluşturma");
                try
                {
                    var clsid = new Guid(HandlerClsid); var iid = new Guid("00000000-0000-0000-C000-000000000046");
                    var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ctx, ref iid, out _handler);
                    if (hr != 0 || _handler == null) { errors.Add(L.F("{0}/{1}: oluşturulamadı 0x{2:X8}", mode, ctx, hr)); continue; }
                    step = L.T("başlatma");
                    if (mode == "file" && ctx == 4) errors.Add("QI " + Probe(_handler));
                    if (mode == "file")
                    {
                        if (_handler is not IInitializeWithFile f) { Unload(); continue; }
                        if (_handler is IObjectWithSite ows) { try { ows.SetSite(new Frame()); } catch { } }
                        Exception? last = null; var ok = false;
                        foreach (var grf in new uint[] { 0, 0x40, 0x20 })
                        { try { f.Initialize(_path, grf); ok = true; break; } catch (Exception ex) { last = ex; errors.Add($"grf{grf:X}:{ex.HResult:X8}"); } }
                        if (!ok) throw last!;
                    }
                    else if (mode == "stream")
                    {
                        if (_handler is not IInitializeWithStream s) { Unload(); continue; }
                        hr = SHCreateStreamOnFileEx(_path, 0x00000020, 0, false, null, out _stream);
                        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                        s.Initialize(_stream, 0);
                    }
                    else
                    {
                        if (_handler is not IInitializeWithItem it) { Unload(); continue; }
                        SHCreateItemFromParsingName(_path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
                        it.Initialize(item, 0);
                    }
                    step = "pencere";
                    var p = (IPreviewHandler)_handler;
                    var r = Rect();
                    p.SetWindow(_hwnd, ref r);
                    if (_handler is IPreviewHandlerVisuals v) { try { v.SetBackgroundColor(Services.ThemeManager.IsDark ? 0x00292929u : 0x00FFFFFFu); } catch { } }
                    step = L.T("çizim");
                    p.DoPreview();
                    Mode = $"{mode}/{(ctx == 4 ? L.T("ayrı süreç") : L.T("süreç içi"))}";
                    return true;
                }
                catch (Exception ex) { errors.Add($"{mode}/{ctx} {step}: {ex.Message}"); Unload(); }
            }
        Error = string.Join(" | ", errors.Take(4));
        return false;
    }

    public string? Mode { get; private set; }

    static string Probe(object o)
    {
        var unk = Marshal.GetIUnknownForObject(o); var sb = new StringBuilder();
        foreach (var (n, g) in new[] { ("file", "b7d14566-0509-4cce-a71f-0a554233bd9b"), ("stream", "b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), ("item", "7f73be3f-fb79-493c-a6c7-7ee14e245841"), ("ph", "8895b1c6-b41f-4c1c-a562-0d564250836f"), ("site", "FC4801A3-2BA9-11CF-A229-00AA003D7352") })
        {
            var iid = new Guid(g); var hr = Marshal.QueryInterface(unk, ref iid, out var ptr);
            sb.Append($"{n}=0x{hr:X8} "); if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        }
        Marshal.Release(unk); return sb.ToString();
    }

    RECT Rect()
    {
        GetClientRect(_hwnd, out var r); return r;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_handler is IPreviewHandler p) { var r = Rect(); try { p.SetRect(ref r); } catch { } }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0005 /*WM_SIZE*/ && _handler is IPreviewHandler p) { var r = Rect(); try { p.SetRect(ref r); } catch { } }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    void Unload()
    {
        if (_handler is IPreviewHandler p) { try { p.Unload(); } catch { } }
        if (_handler != null && Marshal.IsComObject(_handler)) { try { Marshal.FinalReleaseComObject(_handler); } catch { } }
        _handler = null;
        if (_stream != null) { try { Marshal.FinalReleaseComObject(_stream); } catch { } _stream = null; }
    }

    protected override void DestroyWindowCore(HandleRef hwnd) { Unload(); DestroyWindow(hwnd.Handle); }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(IntPtr pmsg);
    }
    [ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPreviewHandlerVisuals { void SetBackgroundColor(uint color); void SetFont(IntPtr plf); void SetTextColor(uint color); }
    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode); }
    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithStream { void Initialize(IStream pstream, uint grfMode); }
    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithItem { void Initialize(IShellItem psi, uint grfMode); }
    [ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectWithSite { void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site); void GetSite(ref Guid riid, out IntPtr site); }
    [ComImport, Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPreviewHandlerFrame { [PreserveSig] int GetWindowContext(IntPtr pinfo); [PreserveSig] int TranslateAccelerator(IntPtr pmsg); }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Frame : IPreviewHandlerFrame { public int GetWindowContext(IntPtr pinfo) => unchecked((int)0x80004001); public int TranslateAccelerator(IntPtr pmsg) => 1; }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int AssocQueryString(uint flags, uint str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate, IStream? pstmTemplate, out IStream ppstm);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
