using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using Dsh.Models;
using Dsh.Util;
using Dsh.ViewModels;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace Dsh.Views;

/// <summary>主窗口：WebView2 加载 DeepSeek 本地服务，全局热键呼出/隐藏，托盘常驻</summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>单实例唤出消息</summary>
    private const int WmShowInstance = 0x0401;

    private readonly MainViewModel _vm;
    private readonly HotkeyManager _hotkeyManager;
    private readonly TrayService _trayService;
    private readonly ConfigService _config;
    private readonly DshHostService _dshHost;
    private readonly ThemeService _themeService;
    private AppSettings _settings;
    private HwndSource? _hwndSource;
    // 服务已就绪但 WebView2 仍在初始化时暂存的目标地址
    private string? _pendingUrl;

    public MainWindow(MainViewModel vm, HotkeyManager hotkeyManager,
        TrayService trayService, ConfigService config, DshHostService dshHost,
        ThemeService themeService)
    {
        InitializeComponent();
        // 容错设置窗口图标（单文件发布下 pack URI 失效，见 AppIcon）
        try
        {
            Icon = AppIcon.GetWindowIcon();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("设置窗口图标失败（已忽略）", ex);
        }

        _vm = vm;
        _hotkeyManager = hotkeyManager;
        _trayService = trayService;
        _config = config;
        _dshHost = dshHost;
        _themeService = themeService;
        _settings = _config.LoadSettings();
        DataContext = vm;

        // 终端输出走 VM 累积字符串：内容变化时刷新富文本并贴底滚动
        vm.PropertyChanged += OnViewModelPropertyChanged;

        // 服务地址就绪（首次启动、失败重试、重启）后导航到带 token 的地址
        vm.NavigateRequested += (_, url) => Navigate(url);
        _trayService.ShowRequested += (_, _) => ToggleWindow();
        _trayService.OpenRequested += (_, _) => ShowWindow();
        _trayService.ExitRequested += (_, _) => ExitApp();

        // 设置保存后重新注册热键
        _config.SettingsSaved += (_, _) =>
        {
            _settings = _config.LoadSettings();
            RegisterHotkey();
        };

        ApplySavedWindowState();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    /// <summary>运行时基础服务是否已启动（防止 Loaded 与静默启动路径重复执行）</summary>
    private bool _runtimeStarted;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 静默启动时运行时已在后台启动（StartRuntime 幂等短路），这里补 WebView2 初始化；
        // WebView2 与 dsh 服务启动并行，服务先就绪则地址暂存 _pendingUrl，待其就绪后补导航
        StartRuntime();
        _ = InitWebViewAsync();
    }

    /// <summary>
    /// 静默启动入口（App.InitializeShell 在 StartHidden 开启时调用）：
    /// 窗口从头到尾不显示、不渲染。关键点：
    /// ① 必须在 EnsureHandle 之前压 Visibility=Hidden——WPF 创建 HWND 时若
    ///    Visibility 为默认 Visible，CreateWindowEx 会带 WS_VISIBLE 样式，
    ///    窗口创建那一瞬间就显示了（EnsureHandle 只是不调 Show，挡不住这个）；
    /// ② ShowInTaskbar 同样必须在句柄创建前关掉，否则任务栏图标闪现；
    /// ③ 句柄创建后 SetWindowText 补标题——窗口未布局时 XAML 的 Title 绑定
    ///    不求值，HWND 标题为空会让 App.NotifyMainWindow 按标题前缀查找时
    ///    找不到窗口，导致二次启动唤出失效。
    /// EnsureHandle 会触发 SourceInitialized（消息钩子/主题监听/热键句柄就绪），
    /// 但不触发 Loaded——WebView2 与首次导航推迟到窗口被呼出时再初始化。
    /// </summary>
    internal void StartHiddenToTray()
    {
        // 顺序不可颠倒：先压可见性，再创建句柄
        Visibility = Visibility.Hidden;
        ShowInTaskbar = false;
        // 仅创建 HWND，不显示窗口、不触发 Loaded
        var handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowText(handle, App.FullMainWindowCaption);
        StartRuntime();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);

    /// <summary>启动运行时基础服务：消息钩子、托盘图标、dsh 后台服务、全局热键、更新检查。
    /// 显示启动（Loaded）与静默启动共用，幂等</summary>
    private void StartRuntime()
    {
        if (_runtimeStarted) return;
        _runtimeStarted = true;

        try
        {
            // 挂载窗口消息钩子：处理全局热键与单实例唤出
            //（静默启动时窗口尚未布局，FromVisual 可能取不到，回退按句柄查找）
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource
                          ?? HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(WndProc);

            _trayService.Show();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("启动基础服务异常", ex);
        }

        // dsh 服务启动不依赖窗口可见，尽早拉起
        _ = _vm.StartServiceAsync();

        // 热键注册整体容错，绝不阻塞界面
        try { RegisterHotkey(); }
        catch (Exception ex) { LoggerHelper.Error("RegisterHotkey 异常", ex); }

        // 启动时静默检查更新（不阻塞 UI）
        _ = _vm.CheckUpdateAtStartupAsync();
        // 顶栏显示 npm 包 dsh 的本地版本（应用版本已挪到窗口标题右侧）
        _ = _vm.LoadDshVersionAsync();
    }

    /// <summary>VM 属性变化：仅关心终端输出</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ServiceTerminal))
            UpdateTerminalOutput();
    }

    /// <summary>刷新终端富文本并按需贴底：用户上翻阅读或正在选中时不打扰（与升级终端一致）</summary>
    private void UpdateTerminalOutput()
    {
        TerminalRun.Text = _vm.ServiceTerminal ?? "";
        var distanceToBottom =
            TerminalOutput.ExtentHeight - TerminalOutput.ViewportHeight - TerminalOutput.VerticalOffset;
        if (distanceToBottom <= 20)
            TerminalOutput.ScrollToVerticalOffset(TerminalOutput.ExtentHeight);
    }

    /// <summary>终端内 Enter/F5 = 重跑上一条命令（真终端习惯，标题栏亦有重启按钮）</summary>
    private void TerminalOutput_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.F5)) return;
        e.Handled = true;
        _vm.RetryServiceCommand.Execute();
    }

    /// <summary>空白区拖选的锚点：文本区按下为 null（交原生拖选），空白区按下锚定文档末尾</summary>
    private TextPointer? _blankDragAnchor;
    private bool _blankDragging;

    /// <summary>按下：命中文本则放行原生拖选；落在文本下方空白区则自行接管——
    /// RichTextBox 在空白处命不中文本位置、不会进入选择模式，导致必须精确点到
    /// 最后一个字之后才能拖选。这里把空白区视作"文档末尾的一格"，
    /// 复刻真终端"从最底部任意空白按下往上拖、整块选中"的手感</summary>
    private void TerminalOutput_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(TerminalOutput);
        if (TerminalOutput.GetPositionFromPoint(position, true) is not null &&
            !IsBeyondLastLine(position))
            return; // 文本区：原生拖选（含双击选词等行为）

        _blankDragAnchor = TerminalOutput.Document.ContentEnd;
        _blankDragging = true;
        TerminalOutput.Focus();            // 接管了按下事件，需自行保证键盘焦点，Ctrl+C 才能复制
        TerminalOutput.CaptureMouse();     // 拖出控件外也能持续扩选
        e.Handled = true;
    }

    private void TerminalOutput_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_blankDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndBlankDrag();
            return;
        }
        // 空白处往上拖时吸附到最近文本行；仍在空白则继续锚在文档末尾
        var current = TerminalOutput.GetPositionFromPoint(e.GetPosition(TerminalOutput), true)
                      ?? TerminalOutput.Document.ContentEnd;
        TerminalOutput.Selection.Select(_blankDragAnchor, current);
        e.Handled = true;
    }

    private void TerminalOutput_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_blankDragging) return;
        EndBlankDrag();
        e.Handled = true;
    }

    private void EndBlankDrag()
    {
        _blankDragging = false;
        _blankDragAnchor = null;
        TerminalOutput.ReleaseMouseCapture();
    }

    /// <summary>判断按下点是否位于最后一行文本之下（含行尾之后的行内空白不算）——
    /// 用文档末尾位置的行首坐标做基准，避免 GetPositionFromPoint 在远处空白仍吸附出结果</summary>
    private bool IsBeyondLastLine(System.Windows.Point position)
    {
        var end = TerminalOutput.Document.ContentEnd;
        var rect = end.GetCharacterRect(LogicalDirection.Backward);
        return position.Y > rect.Bottom + rect.Height / 2;
    }

    /// <summary>初始化 WebView2 并补上暂存的导航地址</summary>
    private async Task InitWebViewAsync()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            // 页面加载前 WebView2 露出的是自身默认白底，与黑色终端来回闪；
            // 固定为终端同色，从隐藏到导航完成的各窗口期保持一体黑
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0C, 0x0C, 0x0C);
            if (_pendingUrl is not null)
                Navigate(_pendingUrl);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("WebView2 初始化异常", ex);
        }
    }

    /// <summary>导航到指定地址；WebView2 尚未就绪时暂存，待初始化完成后补上</summary>
    private void Navigate(string url)
    {
        if (WebView.CoreWebView2 is null)
        {
            _pendingUrl = url;
            return;
        }
        _pendingUrl = null;
        try
        {
            WebView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"导航失败: {url}", ex);
        }
    }

    /// <summary>窗口句柄创建后挂上系统主题监听（跟随系统模式），并子类化窗口过程修复顶部缩放</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 句柄就绪后才能挂上系统主题监听（跟随系统模式），启动时补上这一环。
        // 静默启动不再在此隐藏窗口——那条"先显示再隐藏"的路径会闪屏，
        // 已改为 App.InitializeShell 静默路径根本不调用 Show（见 StartHiddenToTray）
        _themeService.Apply(_settings.Theme, this);

        // 子类化窗口过程：处理顶部边缘缩放（见 SubclassWndProc 注释）。
        // 正常显示与静默启动（EnsureHandle）都会经过这里，两条路径统一覆盖
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            _subclassProc = SubclassWndProc;
            _oldWndProc = SetWindowLongPtrSafe(hwnd, GwlWndProc,
                Marshal.GetFunctionPointerForDelegate(_subclassProc));
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("子类化窗口过程失败（顶部缩放修复不生效，其余功能不受影响）", ex);
        }
    }

    /// <summary>窗口消息处理：WM_HOTKEY + 单实例唤出</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkeyManager.HandleMessage(msg, wParam))
        {
            handled = true;
        }
        else if (msg == WmShowInstance)
        {
            handled = true;
            ToggleWindow();
        }
        return IntPtr.Zero;
    }

    /// <summary>注册全局热键（呼出/隐藏窗口）</summary>
    private void RegisterHotkey()
    {
        try
        {
            _hotkeyManager.UnregisterAll();
            _hotkeyManager.HotkeyPressed -= OnHotkeyPressed;
            _hotkeyManager.HotkeyPressed += OnHotkeyPressed;

            var hwnd = new WindowInteropHelper(this).Handle;
            var binding = _settings.ToggleHotkey;
            if (string.IsNullOrEmpty(binding.Key)) return;

            if (!_hotkeyManager.Register(hwnd, "ToggleWindow",
                    binding.Modifier, binding.Key, 0x1000))
            {
                LoggerHelper.Info($"热键注册失败: {binding.Modifier}+{binding.Key} — {_hotkeyManager.LastError}");
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("RegisterHotkey 整体异常", ex);
        }
    }

    /// <summary>热键触发：呼出/隐藏窗口</summary>
    private void OnHotkeyPressed(object? sender, string actionId)
    {
        if (actionId == "ToggleWindow")
            ToggleWindow();
    }

    /// <summary>呼出/隐藏窗口切换</summary>
    private bool _isToggling;

    private void ToggleWindow()
    {
        if (_isToggling) return;
        _isToggling = true;
        Dispatcher.BeginInvoke(new Action(() => _isToggling = false),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowWindow();
            return;
        }
        if (!IsActive)
        {
            Activate();
            return;
        }
        Hide();
    }

    /// <summary>显示并激活窗口</summary>
    private void ShowWindow()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Show();
        ShowInTaskbar = true;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// 关闭按钮 → 隐藏到托盘常驻；应用级退出（托盘"退出"/升级安装）放行真正关闭。
    /// 注意：若在此无条件 e.Cancel = true，WPF 会连带取消 Application.Shutdown，
    /// 导致进程与 dsh 后台服务都退不掉（升级时还会占用 exe 文件）。
    /// </summary>
    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting) return;

        e.Cancel = true;
        Hide();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        SaveWindowState();
        // 还原子类化窗口过程，避免窗口销毁期间消息进入已失效委托
        if (_oldWndProc != IntPtr.Zero)
        {
            try
            {
                SetWindowLongPtrSafe(new WindowInteropHelper(this).Handle, GwlWndProc, _oldWndProc);
            }
            catch { /* 还原失败随窗口销毁无实际影响 */ }
            _oldWndProc = IntPtr.Zero;
        }
        _hwndSource?.RemoveHook(WndProc);
        _hotkeyManager.Dispose();
        // 真正退出时终止托管的 dsh web 服务，避免 3080 端口残留
        _dshHost.Stop();
    }

    /// <summary>恢复上次窗口位置与大小；记忆值逐维度保底最小尺寸（MinWidth/MinHeight），
    /// 防止历史坏数据（如最小化瞬间保存的 160×28 标题条尺寸）还原出小窗</summary>
    private void ApplySavedWindowState()
    {
        var win = _settings.Window;
        // 记忆值小于窗口最小值（或为 NaN）时该维度回落到最小尺寸，保证窗口永不小于 360×400
        Width = win.Width > MinWidth ? win.Width : MinWidth;
        Height = win.Height > MinHeight ? win.Height : MinHeight;
        if (win.RememberPosition && win.Left is not null && win.Top is not null)
        {
            Left = win.Left.Value;
            Top = win.Top.Value;
        }
    }

    /// <summary>记录窗口位置与大小到 settings.json；
    /// 尺寸不在合理区间时跳过保存（保留上次有效值），防止异常状态值污染记忆</summary>
    private void SaveWindowState()
    {
        var win = _settings.Window;
        if (WindowState == WindowState.Normal)
        {
            win.Left = Left;
            win.Top = Top;
        }
        // 最小化/未布局瞬间的残留小值（如 160×28）不落盘，避免下次启动还原成小窗
        if (Width >= MinWidth && Height >= MinHeight)
        {
            win.Width = Width;
            win.Height = Height;
        }
        _config.SaveSettings(_settings);
    }

    /// <summary>托盘"退出"：真正结束进程（dsh 后台服务随窗口关闭终止）</summary>
    private void ExitApp()
    {
        SaveWindowState();
        _trayService.Hide();
        // 先停后台服务再退出：即便后续窗口关闭流程出意外，也不会残留 dsh 进程
        _dshHost.Stop();
        App.RequestShutdown();
    }

    #region 顶部边缘缩放（子类化窗口过程）

    /// <summary>非客户区命中测试消息</summary>
    private const int WmNcHitTest = 0x0084;

    /// <summary>命中结果常量：顶边 / 左上角 / 右上角</summary>
    private const int HtTop = 12, HtTopLeft = 13, HtTopRight = 14;

    /// <summary>窗口过程替换索引</summary>
    private const int GwlWndProc = -4;

    /// <summary>顶部缩放边缘厚度（DIP），与内容区左/右/下留出的 8px 缩放缝保持一致</summary>
    private const double TopResizeEdge = 8;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>子类化过程委托引用：必须持有防止被 GC 回收导致崩溃</summary>
    private WndProcDelegate? _subclassProc;

    /// <summary>原窗口过程地址</summary>
    private IntPtr _oldWndProc;

    /// <summary>子类化窗口过程：先于 WPF 的 HwndSource hook 链收到消息。
    /// 顶部无法缩放的根因：WPF-UI 的 TitleBar 接管整条标题栏，WindowChrome 的
    /// 元素命中检查优先于 resize 边框判定，顶边（含边缘几像素）都返回 HTCLIENT，
    /// 永远轮不到缩放命中（左/右/底边无 TitleBar 遮挡所以正常）。
    /// 且 WPF-UI 在 HwndSource 的 hook 链中注册在先、先执行并标记 handled，
    /// 普通的 AddHook 收不到 WM_NCHITTEST，只能在子类化层面更早拦截。
    /// 此处顶边 8px 返回 HTTOP 系列恢复上下缩放</summary>
    private IntPtr SubclassWndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmNcHitTest && WindowState == WindowState.Normal && ResizeMode != ResizeMode.NoResize)
        {
            try
            {
                if (GetWindowRect(hWnd, out var rc))
                {
                    // 按 DPI 把边缘厚度换算为物理像素，与实际命中区域一致
                    var edge = (int)Math.Ceiling(TopResizeEdge * GetDpiForWindow(hWnd) / 96.0);
                    // lParam 低/高 16 位为鼠标屏幕像素坐标（有符号）
                    var relX = (short)(lParam.ToInt64() & 0xFFFF) - rc.left;
                    var relY = (short)((lParam.ToInt64() >> 16) & 0xFFFF) - rc.top;
                    if (relY <= edge)
                    {
                        if (relX <= edge) return (IntPtr)HtTopLeft;
                        if (relX >= rc.right - rc.left - edge) return (IntPtr)HtTopRight;
                        return (IntPtr)HtTop;
                    }
                }
            }
            catch { /* 异常时交回原过程按默认逻辑处理 */ }
        }
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>跨位数安全的 SetWindowLong(Ptr)：32 位进程无 SetWindowLongPtr 导出</summary>
    private static IntPtr SetWindowLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr value)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    #endregion
}
