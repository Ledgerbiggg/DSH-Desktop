using System.Windows.Forms;

namespace Dsh.Util;

/// <summary>系统托盘服务（基于 System.Windows.Forms.NotifyIcon）：
/// 左键单击图标打开/呼出主界面，右键菜单显示/隐藏与退出</summary>
public class TrayService : IDisposable
{
    private NotifyIcon? _trayIcon;
    private bool _disposed;

    /// <summary>托盘"显示/隐藏"请求（右键菜单触发，行为为切换）</summary>
    public event EventHandler? ShowRequested;

    /// <summary>托盘"打开/呼出"请求（左键单击图标触发，行为仅为显示）</summary>
    public event EventHandler? OpenRequested;

    /// <summary>托盘"退出"请求（菜单触发）</summary>
    public event EventHandler? ExitRequested;

    /// <summary>创建并显示托盘图标</summary>
    public void Show()
    {
        if (_trayIcon is not null)
            return;

        _trayIcon = new NotifyIcon
        {
            // 单文件发布下 pack URI 与磁盘 Assets 副本均不可用，
            // 统一走 AppIcon：从 exe 内嵌 Win32 图标提取（详见 AppIcon 注释）
            Icon = AppIcon.GetTrayIcon(),
            Text = "deepseek harness",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>隐藏并销毁托盘图标</summary>
    public void Hide()
    {
        if (_trayIcon is null)
            return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    /// <summary>构建托盘右键菜单</summary>
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var toggle = new ToolStripMenuItem("显示 / 隐藏");
        toggle.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(toggle);
        menu.Items.Add(exit);
        return menu;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Hide();
    }
}
