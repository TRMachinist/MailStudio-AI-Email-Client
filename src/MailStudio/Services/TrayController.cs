using System.Windows;
using MailStudio.Models;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MailStudio.Services;

public sealed class TrayController : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Drawing.Icon _image;
    readonly Forms.ContextMenuStrip _menu;
    Action? _openMail;
    bool _disposed;
    public bool IsVisible => !_disposed && _icon.Visible;

    public TrayController(Action show, Action sync, Func<Task<bool>> exit)
    {
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/MailStudio;component/Resources/MailStudio.ico"))!.Stream;
        _image = new Drawing.Icon(resource);
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(L.T("MailStudio'yu aç"), null, (_, _) => show());
        _menu.Items.Add(L.T("Gönder / Al"), null, (_, _) => sync());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(L.T("Çıkış"), null, async (_, _) =>
        {
            _menu.Enabled = false;
            try { await exit(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "MailStudio"); }
            finally { if (!_disposed) _menu.Enabled = true; }
        });
        _icon = new Forms.NotifyIcon { Icon = _image, Text = L.T("MailStudio — yeni posta kontrol ediliyor"), ContextMenuStrip = _menu, Visible = true };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        _icon.BalloonTipClicked += (_, _) => { var open = _openMail; _openMail = null; (open ?? show)(); };
    }

    public void ShowNewMail(MailHeader header, int extraCount, Action open)
    {
        if (_disposed) return;
        _openMail = open;
        _icon.Text = L.T("MailStudio — yeni posta var");
        _icon.ShowBalloonTip(7000, header.FromDisplay,
            header.SubjectDisplay + (extraCount > 0 ? L.F("\n+{0} yeni ileti daha", extraCount) : ""), Forms.ToolTipIcon.Info);
    }
    public void ShowReminder(string title, string text, Action open)
    {
        if (_disposed) return;
        _openMail = open;
        _icon.ShowBalloonTip(7000, title, text, Forms.ToolTipIcon.Info);
    }
    public void ClearNewMail() { if (!_disposed) _icon.Text = L.T("MailStudio — yeni posta kontrol ediliyor"); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _image.Dispose();
    }
}
