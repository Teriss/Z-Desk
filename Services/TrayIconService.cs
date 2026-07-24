using System.Drawing;
using Forms = System.Windows.Forms;

namespace ZDesk.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    public event EventHandler? OpenManagerRequested;
    public event EventHandler? ToggleGroupsRequested;
    public event EventHandler? ExitApplicationRequested;

    public TrayIconService()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => OpenManagerRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("隐藏/显示分组", null, (_, _) => ToggleGroupsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 Z-Desk", null, (_, _) => ExitApplicationRequested?.Invoke(this, EventArgs.Empty));

        var resource = System.Windows.Application.GetResourceStream(
            new Uri("/ZDesk;component/Assets/ZDesk.ico", UriKind.Relative));
        using var sourceIcon = resource is null ? null : new Icon(resource.Stream);
        var trayIcon = (Icon)(sourceIcon ?? SystemIcons.Application).Clone();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "Z-Desk",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenManagerRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
