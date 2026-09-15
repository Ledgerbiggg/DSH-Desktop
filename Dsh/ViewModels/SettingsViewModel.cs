using System.Windows.Input;
using Dsh.Models;
using Dsh.Util;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Mvvm;

namespace Dsh.ViewModels;

/// <summary>设置窗口 ViewModel：开机自启 + 快捷键录制 + 检查更新</summary>
public class SettingsViewModel : BindableBase
{
    private readonly ConfigService _config;
    private readonly AppSettings _settings;
    private readonly UpdateService _updateService;
    private readonly ThemeService _themeService;

    // —— 主题 ——
    private string _theme = ThemeService.System;
    /// <summary>主题模式：System / Light / Dark；切换后立即生效并写入配置</summary>
    public string Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value)) return;
            // 视觉偏好即时生效并落盘，不必再点「保存」
            _settings.Theme = value;
            _themeService.Apply(value, Application.Current.MainWindow);
            _config.SaveSettings(_settings);
        }
    }

    /// <summary>主题下拉选项：Value 存配置，Label 用于显示</summary>
    public List<ThemeOption> ThemeOptions { get; } =
    [
        new(ThemeService.System, "跟随系统"),
        new(ThemeService.Light, "浅色"),
        new(ThemeService.Dark, "深色"),
    ];

    // —— 开机自启 ——
    private bool _autoStart;
    /// <summary>是否开机自动启动（注册表 Run 项）</summary>
    public bool AutoStart
    {
        get => _autoStart;
        set => SetProperty(ref _autoStart, value);
    }

    // —— 静默启动 ——
    private bool _startHidden;
    /// <summary>启动时隐藏到托盘，不显示主窗口（开机自启场景常用）</summary>
    public bool StartHidden
    {
        get => _startHidden;
        set => SetProperty(ref _startHidden, value);
    }

    // —— 快捷键 ——
    private string _hotkeyModifier = "Alt";
    /// <summary>快捷键修饰键</summary>
    public string HotkeyModifier
    {
        get => _hotkeyModifier;
        set
        {
            SetProperty(ref _hotkeyModifier, value);
            RaisePropertyChanged(nameof(HotkeyDisplay));
        }
    }

    private string _hotkeyKey = "D";
    /// <summary>快捷键按键</summary>
    public string HotkeyKey
    {
        get => _hotkeyKey;
        set
        {
            SetProperty(ref _hotkeyKey, value);
            RaisePropertyChanged(nameof(HotkeyDisplay));
        }
    }

    private bool _isRecording;
    /// <summary>是否正在录制快捷键</summary>
    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    /// <summary>快捷键可读显示，如 "Alt + D"</summary>
    public string HotkeyDisplay
    {
        get
        {
            var mod = HotkeyModifier.Trim();
            var key = HotkeyKey.Trim();
            if (string.IsNullOrEmpty(mod) && string.IsNullOrEmpty(key)) return "未设置";
            if (string.IsNullOrEmpty(mod)) return key;
            return $"{mod} + {key}";
        }
    }

    // —— 检查更新 ——
    private bool _isBusy;
    /// <summary>是否正在检查/下载更新</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            CheckCommand.RaiseCanExecuteChanged();
        }
    }

    private string _updateStatusText = "";
    /// <summary>更新状态文本（检查中/下载中/结果等）</summary>
    public string UpdateStatusText
    {
        get => _updateStatusText;
        set => SetProperty(ref _updateStatusText, value);
    }

    /// <summary>当前版本号</summary>
    public string Version => MainViewModel.GetVersionString();

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand RecordCommand { get; }
    public DelegateCommand CheckCommand { get; }

    public SettingsViewModel(ConfigService config, UpdateService updateService,
        ThemeService themeService)
    {
        _config = config;
        _updateService = updateService;
        _themeService = themeService;
        _settings = config.LoadSettings();

        // 用字段赋值而非属性，避免构造期就把当前主题重复应用一遍
        _theme = ThemeService.Normalize(_settings.Theme);
        AutoStart = GetAutoStart();
        StartHidden = _settings.StartHidden;
        HotkeyModifier = _settings.ToggleHotkey.Modifier;
        HotkeyKey = _settings.ToggleHotkey.Key;

        SaveCommand = new DelegateCommand(Save);
        RecordCommand = new DelegateCommand(ToggleRecording);
        CheckCommand = new DelegateCommand(CheckAsync, () => !IsBusy)
            .ObservesProperty(() => IsBusy);
    }

    /// <summary>保存设置到 settings.json 并写入/删除注册表自启项</summary>
    private void Save()
    {
        _settings.ToggleHotkey.Modifier = HotkeyModifier;
        _settings.ToggleHotkey.Key = HotkeyKey;
        _settings.AutoStart = AutoStart;
        _settings.StartHidden = StartHidden;
        SetAutoStart(AutoStart);
        _config.SaveSettings(_settings);
        _ = MessageBoxHelper.Info("设置已保存，快捷键立即生效。");
    }

    /// <summary>切换录制状态</summary>
    private void ToggleRecording()
    {
        IsRecording = !IsRecording;
    }

    /// <summary>由 View 的 PreviewKeyDown 调用：捕获组合键</summary>
    public void CaptureKey(ModifierKeys modifiers, Key key)
    {
        if (key == Key.Escape)
        {
            IsRecording = false;
            return;
        }

        var nonModifier = GetNonModifierKey(key);
        if (nonModifier == null) return;

        if (modifiers == ModifierKeys.None)
            return;

        IsRecording = false;
        HotkeyModifier = ModifiersToString(modifiers);
        HotkeyKey = nonModifier;
    }

    /// <summary>手动检查更新</summary>
    private async void CheckAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        UpdateStatusText = "正在检查更新…";

        try
        {
            var info = await _updateService.FetchLatestAsync();
            if (info is null)
            {
                UpdateStatusText = "检查失败，请稍后重试。";
                return;
            }

            var localVersion = MainViewModel.GetVersionString();
            if (UpdateService.IsNewer(info.Version, localVersion))
            {
                var msg = $"发现新版本 v{info.Version}\n\n更新说明：\n{info.Notes}\n\n是否立即下载并安装？";
                if (!await MessageBoxHelper.Confirm(msg, "发现新版本"))
                {
                    UpdateStatusText = $"发现新版本 v{info.Version}，可稍后升级。";
                    return;
                }

                await DownloadAndRunInstallerAsync(info.Version);
            }
            else
            {
                UpdateStatusText = $"当前已是最新版本 v{localVersion}。";
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("检查更新失败", ex);
            UpdateStatusText = "检查失败，请稍后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下载安装包并启动安装程序</summary>
    private async Task DownloadAndRunInstallerAsync(string version)
    {
        UpdateStatusText = "下载中 0%";
        var progress = new Progress<int>(p => UpdateStatusText = $"下载中 {p}%");
        var path = await _updateService.DownloadInstallerAsync(version, progress);
        if (path is null)
        {
            UpdateStatusText = "下载失败，请前往 GitHub 手动下载。";
            return;
        }

        if (!await MessageBoxHelper.Confirm("下载完成，是否立即安装？\n（安装程序将以管理员权限运行）", "下载完成"))
        {
            UpdateStatusText = "安装包已下载，可稍后安装。";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
        App.RequestShutdown();
    }

    /// <summary>读取开机自启实际生效状态：Run 项存在，且未被 Windows 启动项审批标记为禁用。
    /// 只看 Run 项会误报——被禁用过的项目 Run 项仍在，界面显示已勾选却永远不会自启</summary>
    private static bool GetAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key?.GetValue("Dsh") is not string v || string.IsNullOrEmpty(v))
            return false;
        return !IsAutoStartBlocked();
    }

    /// <summary>Windows 在 StartupApproved 里记录启动项的启停，优先级高于 Run 项本身；
    /// 首字节非 02（常见 01/03）即表示已被任务管理器或优化工具禁用，
    /// 此时 Run 项写得再对也不会自启</summary>
    private static bool IsAutoStartBlocked()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return approved?.GetValue("Dsh") is byte[] { Length: > 0 } state && state[0] != 2;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("读取开机自启审批状态失败", ex);
            return false;
        }
    }

    /// <summary>开启/关闭开机自启：写入或删除注册表 Run 项</summary>
    private static void SetAutoStart(bool enable)
    {
        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? "";
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enable)
        {
            if (!string.IsNullOrEmpty(exePath))
            {
                // 加引号：安装路径含空格（如 Program Files）时不加引号会被拆成多个参数而启动失败
                key.SetValue("Dsh", $"\"{exePath}\"");
                LoggerHelper.Info($"已写入开机自启: {exePath}");
            }
            // 关键：只写 Run 项不够——StartupApproved 中残留的禁用标记优先级更高，
            // 会造成"设置里勾了却永远不自启"。开启时一并清除该标记
            ClearStartupBlockFlag();
        }
        else
        {
            key.DeleteValue("Dsh", false);
            LoggerHelper.Info("已移除开机自启");
        }
    }

    /// <summary>清除 Windows 启动项审批中的禁用标记，让 Run 项真正生效</summary>
    private static void ClearStartupBlockFlag()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
            if (approved?.GetValue("Dsh") is not null)
            {
                approved.DeleteValue("Dsh", false);
                LoggerHelper.Info("已清除开机自启的禁用标记");
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("清除开机自启禁用标记失败", ex);
        }
    }

    private static string? GetNonModifierKey(Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System)
            return null;

        return key switch
        {
            Key.Space => "Space",
            Key.OemTilde => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Back => "Back",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            _ when key >= Key.D0 && key <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            _ when key >= Key.NumPad0 && key <= Key.NumPad9 => ((int)key - (int)Key.NumPad0).ToString(),
            _ when key >= Key.A && key <= Key.Z => key.ToString(),
            _ when key >= Key.F1 && key <= Key.F24 => key.ToString(),
            _ => key.ToString(),
        };
    }

    private static string ModifiersToString(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }
}
