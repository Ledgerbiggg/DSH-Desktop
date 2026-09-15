using System.Diagnostics;
using System.Windows;
using Dsh.Util;
using Dsh.Views;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;

namespace Dsh.ViewModels;

/// <summary>主窗口 ViewModel：窗口标题、设置命令、版本号、启动检查更新、dsh 版本校验升级</summary>
public class MainViewModel : BindableBase
{
    private readonly ConfigService _config;
    private readonly UpdateService _updateService;
    private readonly DshHostService _dshHost;
    private readonly DshUpdateService _dshUpdate;

    /// <summary>窗口标题：deepseek harness + 版本号（版本在标题右侧）</summary>
    public string Title => $"{App.MainWindowCaption} v{Version}";

    /// <summary>版本号</summary>
    public string Version => GetVersionString();

    private string _dshVersion = "dsh v…";
    /// <summary>npm 包 dsh（DeepSeek harness）本地版本，显示于顶部导航条（应用版本已挪到窗口标题）</summary>
    public string DshVersion
    {
        get => _dshVersion;
        private set => SetProperty(ref _dshVersion, value);
    }

    /// <summary>查询 npm 包 dsh 本地版本刷新顶栏显示；检测失败提示未检测到</summary>
    public async Task LoadDshVersionAsync()
    {
        try
        {
            var v = await _dshUpdate.GetLocalVersionAsync();
            DshVersion = v is null ? "dsh 未检测到" : $"dsh v{v}";
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("查询 dsh 本地版本失败", ex);
            DshVersion = "dsh 未检测到";
        }
    }

    private string _url = DshHostService.FallbackUrl;
    /// <summary>DeepSeek 本地服务地址（含一次性 token，由 DshHostService 启动时获取）</summary>
    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }

    /// <summary>本地服务是否已真正就绪（成功取到带 token 的地址）。
    /// 注意不能用 Url 非空判断：Url 初始即为兜底地址，判断会失准</summary>
    private bool _serviceReady;

    /// <summary>WebView2 是否显示：服务已就绪且终端已清空。
    /// WebView2 是独立 HWND（airspace），WPF 的遮罩/终端层永远盖不住它，
    /// 只能在展示终端/遮罩期间把它自身 Collapsed，否则未导航的空白 HWND 会盖住整屏</summary>
    public bool IsWebVisible => ServiceTerminal is null && _serviceReady;

    private bool _isServiceStarting = true;
    /// <summary>本地服务是否正在启动（遮罩与终端光标闪烁的依据）</summary>
    public bool IsServiceStarting
    {
        get => _isServiceStarting;
        set
        {
            if (SetProperty(ref _isServiceStarting, value))
                RaisePropertyChanged(nameof(IsStartOverlayVisible));
        }
    }

    /// <summary>启动遮罩是否显示：仅在没有网页可看时用它填充等待期（首启、终端态重试）。
    /// 网页已在显示时的重启/升级恢复交给页面自身的「重连中」提示，
    /// 避免全屏遮罩把正常页面盖掉，观感突兀</summary>
    public bool IsStartOverlayVisible => IsServiceStarting && !IsWebVisible;

    private string? _serviceTerminal;
    /// <summary>启动终端累积输出（含每轮 "PS> dsh web …" 命令回显与报错）；null 表示隐藏整屏终端。
    /// 重试/升级恢复都只追加不清空——像真终端里连续敲命令，只有成功拿到地址才清屏</summary>
    public string? ServiceTerminal
    {
        get => _serviceTerminal;
        private set
        {
            if (SetProperty(ref _serviceTerminal, value))
            {
                RaisePropertyChanged(nameof(IsWebVisible));
                RaisePropertyChanged(nameof(IsStartOverlayVisible));
            }
        }
    }

    /// <summary>向终端追加一行（null 追加空行）；上限 16000 字符防失控输出撑爆内存</summary>
    private void AppendTerminal(string? line)
    {
        var text = ServiceTerminal ?? "";
        if (text.Length > 16000) text = "…（更早输出已截断）\n" + text[^16000..];
        ServiceTerminal = text + (text.Length > 0 ? "\n" : "") + (line ?? "");
    }

    /// <summary>打开设置窗口命令</summary>
    public DelegateCommand OpenSettingsCommand { get; }

    /// <summary>重启本地服务命令：终止 dsh web 后重新拉起</summary>
    public DelegateCommand RestartCommand { get; }

    /// <summary>点击新版本标识后执行升级命令</summary>
    public DelegateCommand UpdateCommand { get; }

    /// <summary>本地服务地址就绪，请求主窗口导航到该地址（首次加载与失败重试、重启共用）</summary>
    public event EventHandler<string>? NavigateRequested;

    /// <summary>本地服务启动失败后重试</summary>
    public DelegateCommand RetryServiceCommand { get; }

    // —— 自动升级 ——
    private bool _hasUpdate;
    /// <summary>是否检测到新版本（绑定标题栏标识可见性）</summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        set => SetProperty(ref _hasUpdate, value);
    }

    private string _updateBadgeText = "";
    /// <summary>标题栏新版本标识文本</summary>
    public string UpdateBadgeText
    {
        get => _updateBadgeText;
        set => SetProperty(ref _updateBadgeText, value);
    }

    private bool _isUpdating;
    /// <summary>是否正在下载安装包（防止重复触发）</summary>
    public bool IsUpdating
    {
        get => _isUpdating;
        set => SetProperty(ref _isUpdating, value);
    }

    // —— dsh（DeepSeek harness）版本校验升级 ——
    private bool _isCheckingDshUpdate;
    /// <summary>是否正在检查/升级 dsh（防止重复触发）</summary>
    public bool IsCheckingDshUpdate
    {
        get => _isCheckingDshUpdate;
        set => SetProperty(ref _isCheckingDshUpdate, value);
    }

    private string _dshUpdateStatus = "";
    /// <summary>dsh 检查/升级进行中的状态文本，空串时隐藏</summary>
    public string DshUpdateStatus
    {
        get => _dshUpdateStatus;
        set => SetProperty(ref _dshUpdateStatus, value);
    }

    /// <summary>校验 dsh（DeepSeek harness）新版本命令</summary>
    public DelegateCommand CheckDshUpdateCommand { get; }

    /// <summary>设置窗口是否已打开</summary>
    public bool IsSettingsWindowOpen => _settingsWindow is { IsVisible: true };

    private SettingsWindow? _settingsWindow;

    /// <summary>dsh 升级进度终端弹窗；关闭后置空，下次升级重新创建（WPF Window 关闭后不能再次 Show）</summary>
    private TerminalWindow? _updateTerminal;

    public MainViewModel(ConfigService config, UpdateService updateService, DshHostService dshHost,
        DshUpdateService dshUpdate)
    {
        _config = config;
        _updateService = updateService;
        _dshHost = dshHost;
        _dshUpdate = dshUpdate;
        OpenSettingsCommand = new DelegateCommand(OpenSettings);
        RetryServiceCommand = new DelegateCommand(() => _ = StartServiceAsync());
        // 启动中禁止再次重启，避免并行拉起多个 dsh 进程争抢 3080 端口
        RestartCommand = new DelegateCommand(() => _ = RestartServiceAsync(), () => !IsServiceStarting)
            .ObservesProperty(() => IsServiceStarting);
        UpdateCommand = new DelegateCommand(UpdateAsync, () => !IsUpdating)
            .ObservesProperty(() => IsUpdating);
        // 检查/升级进行中禁止重复点击
        CheckDshUpdateCommand = new DelegateCommand(() => _ = CheckDshUpdateAsync(), () => !IsCheckingDshUpdate)
            .ObservesProperty(() => IsCheckingDshUpdate);
    }

    /// <summary>启动本地 dsh 服务，取到带 token 的地址后通知主窗口导航。
    /// 终端只服务"异常态"：进入时已是终端（上次启动失败）则按真终端追加本轮命令与输出；
    /// 网页正常时（首启之外的正常重启）不碰终端，页面自身的「重连中」就是最自然的反馈</summary>
    public async Task StartServiceAsync()
    {
        IsServiceStarting = true;
        // 刻意不清 ServiceTerminal——只有成功拿到地址才清屏回网页，
        // 已展示的报错不会因重试/升级恢复被抹掉
        var terminalMode = ServiceTerminal is not null;
        try
        {
            // 本地未安装 dsh（DeepSeek harness）时主动引导安装，并提供重新进入入口，
            // 避免直接尝试启动后只拿到笼统的失败信息
            if (!await EnsureDshInstalledAsync())
                return;

            // 重试场景先清理可能残留的进程，否则 3080 端口被占会再次启动失败
            _dshHost.Stop();
            if (terminalMode)
                EchoLaunchCommand();
            Url = await _dshHost.StartAsync();
            if (!_dshHost.HasToken)
            {
                // 启动失败：若原来是网页态，此刻才降级进终端，补回命令回显让报错有上下文
                if (!terminalMode) EchoLaunchCommand();
                _serviceReady = false;
                RaisePropertyChanged(nameof(IsWebVisible));
                RaisePropertyChanged(nameof(IsStartOverlayVisible));
                var detail = _dshHost.LastError.Trim();
                AppendTerminal(null);
                AppendTerminal(detail.Length > 0 ? detail
                    : "未能启动本地服务（无任何输出）。请在 PowerShell 中执行 dsh web 确认可正常启动，"
                      + "并检查 3080 端口未被其他程序占用。");
                return;
            }
            // 成功：清空终端回到 WebView2
            _serviceReady = true;
            ServiceTerminal = null;
            RaisePropertyChanged(nameof(IsWebVisible));
            RaisePropertyChanged(nameof(IsStartOverlayVisible));
            NavigateRequested?.Invoke(this, Url);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("启动本地服务失败", ex);
            // 终端同时展示异常信息与 dsh web 的原始输出，用户能自行定位原因
            if (!terminalMode) EchoLaunchCommand();
            _serviceReady = false;
            RaisePropertyChanged(nameof(IsWebVisible));
            RaisePropertyChanged(nameof(IsStartOverlayVisible));
            var detail = _dshHost.LastError.Trim();
            AppendTerminal(null);
            AppendTerminal("启动本地服务失败：" + ex.Message
                           + (detail.Length > 0 ? "\n" + detail : ""));
        }
        finally
        {
            IsServiceStarting = false;
        }
    }

    /// <summary>终端回显本轮要执行的命令，随后接进程输出（stdout 不含地址 + stderr + 退出码）</summary>
    private void EchoLaunchCommand() => AppendTerminal("PS> " + _dshHost.LaunchCommandText);

    /// <summary>
    /// 启动前检测本地是否已安装 dsh；未安装则弹窗引导安装教程，
    /// 用户安装后可点「重新进入」再次检测并继续，或选择退出程序。
    /// </summary>
    /// <returns>true 表示已具备启动条件可继续；false 表示用户退出</returns>
    private async Task<bool> EnsureDshInstalledAsync()
    {
        if (await _dshHost.IsDshInstalledAsync())
            return true;

        while (true)
        {
            var choice = await MessageBoxHelper.ShowDshNotInstalledAsync();
            if (choice == DshDialogResult.OpenTutorial)
            {
                // 打开教程网页，用户安装完成后点「重新进入」再检测
                OpenDshTutorial();
                continue;
            }
            if (choice == DshDialogResult.Reenter)
            {
                // 用户称已安装，再次验证；仍检测不到则继续提示
                if (await _dshHost.IsDshInstalledAsync())
                    return true;
                continue;
            }
            // 退出程序
            App.RequestShutdown();
            return false;
        }
    }

    /// <summary>用默认浏览器打开 dsh 本地安装教程</summary>
    private static void OpenDshTutorial()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = MessageBoxHelper.DshInstallTutorialUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("打开 dsh 安装教程失败", ex);
        }
    }

    /// <summary>
    /// 重启本地服务：确认后终止当前 dsh web 进程（指令取消）并重新运行，
    /// 成功后取到新的带 token 地址，由 NavigateRequested 驱动界面重新加载。
    /// </summary>
    private async Task RestartServiceAsync()
    {
        var confirmed = await MessageBoxHelper.Confirm(
            "将终止当前的 DeepSeek harness 服务并重新启动，页面会重新加载。\n\n是否继续？",
            "重启 DeepSeek harness");
        if (!confirmed) return;

        // StartServiceAsync 内部会先 Stop 清理旧进程（含其拉起的 node 子进程）再重新启动
        await StartServiceAsync();
    }

    /// <summary>
    /// 校验 DeepSeek harness（dsh）是否有新版本：
    /// 比对 npm 本地已装版本与仓库最新版本，有新版则弹窗确认后
    /// 经 npm install -g 升级，最后重启本地服务让新版本立即生效。
    /// </summary>
    private async Task CheckDshUpdateAsync()
    {
        IsCheckingDshUpdate = true;
        DshUpdateStatus = "dsh 检查更新中…";
        try
        {
            var local = await _dshUpdate.GetLocalVersionAsync();
            if (local is null)
            {
                DshUpdateStatus = "";
                _ = MessageBoxHelper.Warn(
                    "未检测到 dsh（DeepSeek harness）的本地版本信息。\n\n" +
                    "请确认已通过 npm 全局安装 dsh（npm install -g @deepseek-ai/dsh）后重试。");
                return;
            }

            var latest = await _dshUpdate.GetLatestVersionAsync();
            DshUpdateStatus = "";
            if (latest is null)
            {
                _ = MessageBoxHelper.Warn("获取 dsh 最新版本失败，请检查网络后重试。");
                return;
            }

            if (!UpdateService.IsNewer(latest, local))
            {
                _ = MessageBoxHelper.Info($"DeepSeek harness 已是最新版本 v{local}。");
                return;
            }

            // 有新版本：确认后升级（npm 下载可能较慢，期间以状态文本提示）
            if (!await MessageBoxHelper.Confirm(
                    $"发现 DeepSeek harness 新版本：v{local} → v{latest}\n\n" +
                    "是否立即升级？\n（将通过 npm install -g 重新安装，完成后自动重启本地服务）",
                    "发现 dsh 新版本"))
                return;

            // 先停掉旧版服务进程再替换文件，避免 npm 升级被运行中的进程干扰
            _dshHost.Stop();
            DshUpdateStatus = $"dsh 升级到 v{latest} 中，请稍候…";

            // 终端弹窗实时滚动 npm 输出：用户能看到真实进度；
            // 未选中文字时 Ctrl+C 或关闭窗口（先确认）可取消升级
            var term = ShowUpdateTerminal();
            using var updateCts = new CancellationTokenSource();
            // 弹窗复用时旧订阅指向已释放的 CTS，Cancel 会抛 ObjectDisposedException，静默即可
            term.CancelRequested += (_, _) => { try { updateCts.Cancel(); } catch (ObjectDisposedException) { } };
            term.AppendLine("正在停止本地 dsh 服务…");
            term.AppendLine(
                $"PS> npm install -g {DshUpdateService.PackageId}@latest", TerminalLineKind.Command);
            // SetBusy 让行尾光标开始闪烁：npm 输出稀少的等待期窗口也保持"活着"的观感
            term.SetBusy(true);
            var onLine = new Progress<string>(line => _updateTerminal?.AppendLine(line));
            var ok = await _dshUpdate.InstallLatestAsync(onLine, updateCts.Token);
            term.SetBusy(false);

            // 用户取消：npm 已被杀，重启旧版服务即可，不算失败、不弹错误框；
            // 恢复完成后与成功路径对称——收尾行 + 自动淡出关窗
            //（关窗触发的取消此时窗口已关，CloseAfter 对不可见窗口是空操作，安全）
            if (updateCts.IsCancellationRequested)
            {
                term.AppendLine("已取消升级，正在恢复本地服务…", TerminalLineKind.Error);
                DshUpdateStatus = "";
                await StartServiceAsync();
                term.AppendLine("本地服务已恢复，本次升级已取消。");
                term.CloseAfter(TimeSpan.FromMilliseconds(1500));
                return;
            }

            term.AppendLine(
                ok ? "升级完成，正在重启本地服务…" : "升级失败（npm 退出码非 0），详见上方输出。",
                ok ? TerminalLineKind.Command : TerminalLineKind.Error);
            DshUpdateStatus = "";

            if (!ok)
            {
                _ = MessageBoxHelper.Error(
                    "升级失败。可在 PowerShell 中手动执行：\nnpm install -g @deepseek-ai/dsh@latest");
                // 失败也要把服务拉回来，避免界面停留在无服务可用状态
                await StartServiceAsync();
                return;
            }

            // 升级成功：重启本地服务加载新版本，并刷新顶栏 dsh 版本
            await StartServiceAsync();
            await LoadDshVersionAsync();
            // 结果直接写进终端（绿色收尾行），稍作停留供用户看清后自动淡出关窗，
            // 不再另弹提示框——整个升级流程从弹窗到收尾一气呵成
            term.AppendLine($"DeepSeek harness 已升级到 v{latest}，本地服务已重启。", TerminalLineKind.Command);
            term.CloseAfter(TimeSpan.FromMilliseconds(1500));
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("dsh 检查/升级失败", ex);
            DshUpdateStatus = "";
            _ = MessageBoxHelper.Error("检查/升级 dsh 失败：" + ex.Message);
        }
        finally
        {
            IsCheckingDshUpdate = false;
        }
    }

    /// <summary>启动时静默检查更新：拉远程 version.json 比较版本</summary>
    public async Task CheckUpdateAtStartupAsync()
    {
        try
        {
            var info = await _updateService.FetchLatestAsync();
            if (info is null) return;

            var localVersion = GetVersionString();
            if (UpdateService.IsNewer(info.Version, localVersion))
            {
                HasUpdate = true;
                UpdateBadgeText = $"🆕 新版本 v{info.Version}";
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("启动检查更新失败", ex);
        }
    }

    /// <summary>用户点击新版本标识：弹窗确认 → 下载安装包 → 启动安装 → 退出</summary>
    private async void UpdateAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        UpdateBadgeText = "下载中…";

        try
        {
            var info = await _updateService.FetchLatestAsync();
            if (info is null)
            {
                _ = MessageBoxHelper.Warn("获取版本信息失败，请稍后重试。");
                UpdateBadgeText = "🆕 点击重试";
                return;
            }

            // 弹窗确认
            var msg = $"发现新版本 v{info.Version}\n\n更新说明：\n{info.Notes}\n\n是否立即下载并安装？";
            if (!await MessageBoxHelper.Confirm(msg, "发现新版本"))
                return;

            // 下载安装包
            UpdateBadgeText = "下载中 0%";
            var progress = new Progress<int>(p => UpdateBadgeText = $"下载中 {p}%");
            var path = await _updateService.DownloadInstallerAsync(info.Version, progress);
            if (path is null)
            {
                _ = MessageBoxHelper.Warn("下载安装包失败，请前往 GitHub 手动下载。");
                UpdateBadgeText = "🆕 下载失败，点击重试";
                return;
            }

            // 弹窗确认安装
            if (!await MessageBoxHelper.Confirm("下载完成，是否立即安装？\n（安装程序将以管理员权限运行）", "下载完成"))
                return;

            // 启动安装向导（UAC 由系统接管），退出主程序释放文件占用
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            App.RequestShutdown();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("UpdateAsync 失败", ex);
            _ = MessageBoxHelper.Error("升级失败：" + ex.Message);
            UpdateBadgeText = "🆕 升级失败，点击重试";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    /// <summary>打开/关闭设置窗口</summary>
    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Close();
            return;
        }
        try
        {
            var window = (SettingsWindow)Prism.Ioc.ContainerLocator.Container.Resolve(typeof(SettingsWindow));
            window.Owner = Application.Current.MainWindow;
            _settingsWindow = window;
            window.Closed += (_, _) => _settingsWindow = null;
            window.Show();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("打开设置窗口失败", ex);
            _ = MessageBoxHelper.Error("打开设置失败：" + ex.Message);
        }
    }

    /// <summary>打开（或复用仍在显示的）升级进度终端弹窗：非模态，随手关闭不影响升级执行</summary>
    private TerminalWindow ShowUpdateTerminal()
    {
        if (_updateTerminal is { IsVisible: true })
            return _updateTerminal;

        var window = new TerminalWindow { Owner = Application.Current.MainWindow };
        window.Closed += (_, _) => _updateTerminal = null;
        _updateTerminal = window;
        window.Show();
        return window;
    }

    /// <summary>统一读取版本号：优先 version.json，回退程序集版本</summary>
    internal static string GetVersionString()
    {
        try
        {
            var vj = System.IO.Path.Combine(AppContext.BaseDirectory, "version.json");
            if (System.IO.File.Exists(vj))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(vj));
                if (doc.RootElement.TryGetProperty("version", out var ve))
                {
                    var s = ve.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(s)) return Normalize(s);
                }
            }
        }
        catch { }

        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v is not null) return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }

        return "0.0.0";
    }

    /// <summary>把任意版本串规范成 "主.次.修" 三段</summary>
    private static string Normalize(string raw)
    {
        var parts = raw.Split('.');
        var maj = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
        var min = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;
        var bld = parts.Length > 2 && int.TryParse(parts[2], out var c) ? c : 0;
        return $"{maj}.{min}.{bld}";
    }
}
