using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dsh.Util;

namespace Dsh.Views;

/// <summary>终端行种类：命令行/成功提示绿色，错误红色，其余浅灰</summary>
public enum TerminalLineKind
{
    Normal,
    Command,
    Error,
}

/// <summary>
/// 终端风命令输出弹窗：输出逐行流出 + 运行期 npm 同款转圈光标（停在新行行首）+ 入场动画，
/// 文本可鼠标拖选、选中后 Ctrl+C / 右键复制；右上保留系统式关闭按钮。
/// 任务运行期间支持两种取消方式（均触发 CancelRequested 由宿主善后）：
/// 未选中文字时按 Ctrl+C 中断任务（终端语义，窗口保留显示善后输出）；
/// 关闭窗口时先确认再取消，防误触杀掉升级。
/// </summary>
public partial class TerminalWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>用户请求取消任务（Ctrl+C / 运行中关闭窗口）时触发，由宿主终止后台任务并善后</summary>
    public event EventHandler? CancelRequested;

    /// <summary>运行中经确认取消后的真正关闭标志，防止 Closing 二次拦截关不掉</summary>
    private bool _forceClose;

    /// <summary>行数上限：npm 异常时输出可能很长，丢弃最早行防内存无界增长</summary>
    private const int MaxLines = 2000;

    // macOS Terminal 深色配色：普通浅灰 / 命令绿 / 错误红
    private static readonly System.Windows.Media.Brush NormalBrush = CreateFrozen(0xCC, 0xCC, 0xCC);
    private static readonly System.Windows.Media.Brush CommandBrush = CreateFrozen(0x29, 0xBB, 0x00);
    private static readonly System.Windows.Media.Brush ErrorBrush = CreateFrozen(0xFF, 0x6C, 0x60);

    private readonly Queue<(string Text, TerminalLineKind Kind)> _pending = new();
    private readonly Run _cursorRun = new("") { Foreground = CommandBrush };

    /// <summary>npm/yarn 同款 braille 转圈帧：运行中光标转圈，静默安装期也明确"正在更新"</summary>
    private const string SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    private DispatcherTimer? _flowTimer;    // 行流出节奏：模拟真实终端逐行打印
    private DispatcherTimer? _spinnerTimer; // 光标转圈：90ms 一帧
    private ScrollViewer? _scrollViewer;
    private bool _busy;
    private int _spinnerIndex;

    public TerminalWindow()
    {
        InitializeComponent();

        // 70ms 一行的流出节奏：瞬间批量到达的输出在视觉上呈现为连续滚动
        _flowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _flowTimer.Tick += (_, _) => FlowPendingLines();
        _flowTimer.Start();

        // npm 安装等待期的转圈光标：任务运行期间持续提示"终端活着、正在更新"
        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
            _cursorRun.Text = SpinnerFrames[_spinnerIndex].ToString();
        };
    }

    /// <summary>入场动画：淡入 + 轻微上滑，弹出即有生命感</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase(),
        };
        BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        EntryTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>追加输出（任意线程可调，内部调度回 UI 线程），进入队列按节奏逐行显示</summary>
    public void AppendLine(string text, TerminalLineKind kind = TerminalLineKind.Normal)
    {
        if (string.IsNullOrEmpty(text)) return;
        Dispatcher.BeginInvoke(() =>
        {
            // 按换行拆开逐行入队；孤立 \r 也归一为换行——npm 用 \r 做单行刷新，
            // 不处理会残留在段落里显示成"回车没按"的挤行观感
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                _pending.Enqueue((raw, kind));
        });
    }

    /// <summary>标记任务运行状态：运行中新行行首光标转圈（npm 同款更新动画），结束后光标熄灭</summary>
    public void SetBusy(bool busy)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _busy = busy;
            if (busy)
            {
                _spinnerIndex = 0;
                _cursorRun.Text = SpinnerFrames[0].ToString();
                AttachCursor();
                _spinnerTimer?.Start();
            }
            else
            {
                DetachCursor();
                _spinnerTimer?.Stop();
            }
        });
    }

    /// <summary>消费流出队列：正常一行行出；积压超过阈值时提速追赶，避免显示长期落后</summary>
    private void FlowPendingLines()
    {
        if (_pending.Count == 0) return;
        var burst = _pending.Count > 20 ? 4 : 1;
        for (var i = 0; i < burst && _pending.Count > 0; i++)
            AppendOneLine(_pending.Dequeue());
    }

    private void AppendOneLine((string Text, TerminalLineKind Kind) line)
    {
        // 新行落位前先把光标从当前宿主段落摘下，行落位后再挂回新行尾
        DetachCursor();

        var brush = line.Kind switch
        {
            TerminalLineKind.Command => CommandBrush,
            TerminalLineKind.Error => ErrorBrush,
            _ => NormalBrush,
        };
        // 空行同样落段，保持行距节奏与真实终端一致
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };
        para.Inlines.Add(new Run(line.Text) { Foreground = brush });
        OutputBox.Document.Blocks.Add(para);

        while (OutputBox.Document.Blocks.Count > MaxLines)
            OutputBox.Document.Blocks.Remove(OutputBox.Document.Blocks.FirstBlock);

        if (_busy) AttachCursor();
    }

    /// <summary>把转圈光标挂到文档末尾的独立空行段：模拟真实终端"回车已按、光标停在新行行首"，
    /// 后续输出永远从下一行开始，绝不贴在上一行（尤其是命令行）的行尾</summary>
    private void AttachCursor()
    {
        if (_cursorRun.Parent is Paragraph) return;
        // 末段已是空行段则直接复用，避免连续输出之间堆积多个空行
        if (OutputBox.Document.Blocks.LastBlock is Paragraph last && last.Inlines.Count == 0)
        {
            last.Inlines.Add(_cursorRun);
            return;
        }
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };
        para.Inlines.Add(_cursorRun);
        OutputBox.Document.Blocks.Add(para);
    }

    private void DetachCursor()
    {
        if (_cursorRun.Parent is not Paragraph host) return;
        host.Inlines.Remove(_cursorRun);
        // 宿主是为光标专设的空行段，摘走光标后删段，避免连续输出堆积空行
        if (host.Inlines.Count == 0)
            OutputBox.Document.Blocks.Remove(host);
    }

    /// <summary>新输出到达时贴底跟随滚动；用户上翻阅读历史时不打扰</summary>
    private void OutputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _scrollViewer ??= FindVisualChild<ScrollViewer>(OutputBox);
        if (_scrollViewer is null) return;
        var distanceToBottom =
            _scrollViewer.ExtentHeight - _scrollViewer.VerticalOffset - _scrollViewer.ViewportHeight;
        if (distanceToBottom <= 20)
            _scrollViewer.ScrollToEnd();
    }

    /// <summary>Ctrl+C：终端语义——选中了文字时放行执行复制，未选中且任务运行中则中断任务</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (!OutputBox.Selection.IsEmpty) return; // 有选中 → 交给 RichTextBox 复制
        if (!_busy) return;                       // 无任务运行 → 不响应
        e.Handled = true;
        RequestCancel();
    }

    /// <summary>任务运行中点关闭：先拦下并确认，防误触杀掉升级；确认取消则关窗，拒绝则留在窗口继续</summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose || !_busy) return;
        e.Cancel = true; // 先拦住窗口，等用户做完选择
        if (!await MessageBoxHelper.Confirm(
                "升级正在进行中，关闭窗口将取消本次升级并恢复本地服务。\n确定要取消吗？",
                "取消升级"))
            return;
        _forceClose = true;
        RequestCancel();
        Close();
    }

    /// <summary>触发取消：回显真实终端同款 ^C，再通知宿主终止后台任务</summary>
    private void RequestCancel()
    {
        AppendLine("^C", TerminalLineKind.Error);
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>任务成功收尾后自动关窗：先停留让用户看清结尾输出，再淡出收场；
    /// 停留期间若用户又发起新任务（_busy）则放弃关闭，交还给用户</summary>
    public void CloseAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsVisible || _busy) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase(),
            };
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _flowTimer?.Stop();
        _spinnerTimer?.Stop();
    }

    private static System.Windows.Media.Brush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>在 RichTextBox 模板可视树中定位内置 ScrollViewer</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
