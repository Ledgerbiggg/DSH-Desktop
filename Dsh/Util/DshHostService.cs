using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Util;

/// <summary>
/// DeepSeek 本地服务（dsh web）进程托管服务。
/// dsh web 每次启动都会生成一次性 token，可用地址形如 http://127.0.0.1:3080/?token=xxx，
/// 不带 token 访问会被判为无权限，因此必须由本服务拉起进程并从标准输出提取带 token 的地址。
/// 该进程即 Web 服务本体（长期运行），需随本程序退出一并终止，避免端口残留占用。
/// </summary>
public class DshHostService : IDisposable
{
    /// <summary>未取到 token 时的兜底地址</summary>
    public const string FallbackUrl = "http://127.0.0.1:3080/";

    // 匹配 dsh 输出的 "dsh web: http://127.0.0.1:3080/?token=xxx"
    private static readonly Regex UrlPattern =
        new(@"https?://\d{1,3}(?:\.\d{1,3}){3}:\d+/\S*", RegexOptions.Compiled);

    private Process? _process;
    private IntPtr _jobHandle;

    /// <summary>最近一次启动失败的诊断输出（stderr/退出码/超时原因），成功启动后清空；供终端风格错误面板展示</summary>
    public string LastError { get; private set; } = "";

    /// <summary>LastError 拼接锁：stderr/退出事件来自线程池线程</summary>
    private readonly object _lastErrorLock = new();

    /// <summary>追加诊断行；设上限防异常进程的失控输出撑爆内存</summary>
    private void AppendLastError(string line)
    {
        lock (_lastErrorLock)
        {
            if (LastError.Length < 8000)
                LastError += line + Environment.NewLine;
        }
    }

    /// <summary>实际可访问的服务地址（含 token）</summary>
    public string Url { get; private set; } = FallbackUrl;

    /// <summary>是否成功取到带 token 的地址</summary>
    public bool HasToken => Url.Contains("token=", StringComparison.OrdinalIgnoreCase);

    // 实际执行的 dsh 启动命令（不含 PowerShell 包装层），供终端回显
    private const string DshLaunchArgs = "dsh web --no-open";

    /// <summary>启动命令回显文本（终端 "PS> " 后的部分）</summary>
    public string LaunchCommandText => DshLaunchArgs;

    /// <summary>
    /// 启动 dsh web 并等待其输出带 token 的地址。
    /// 使用 --no-open 阻止 dsh 自行拉起默认浏览器——页面由本程序内嵌的 WebView2 呈现。
    /// </summary>
    /// <param name="timeoutMs">等待地址输出的最长时间</param>
    /// <returns>带 token 的地址；启动失败时返回兜底地址</returns>
    public async Task<string> StartAsync(int timeoutMs = 20000)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 每次启动重置上次的诊断输出，避免重试时展示旧失败信息
        LastError = "";

        try
        {
            var psi = new ProcessStartInfo
            {
                // dsh 是 npm 全局安装生成的 dsh.ps1，不是可执行文件，只能经 PowerShell 调用。
                // 注意 -Command 后直接跟命令，不能加引号——加引号 PowerShell 只会回显字符串而不执行
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + DshLaunchArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                var line = e.Data.Trim();
                LoggerHelper.Info($"dsh web: {line}");
                var m = UrlPattern.Match(line);
                if (m.Success) tcs.TrySetResult(m.Value);
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var line = e.Data.Trim();
                    LoggerHelper.Error($"dsh web stderr: {line}");
                    AppendLastError(line);
                }
            };

            // 进程提前退出说明启动失败（dsh 未安装或端口已被占用），无需空等到超时
            _process.Exited += (_, _) =>
            {
                tcs.TrySetResult("");
                // 退出码是排查「装了但起不来」的关键线索（如 1=脚本报错、9009=命令不存在）
                try { AppendLastError($"（dsh web 进程已退出，退出码 {_process.ExitCode}）"); }
                catch { /* 进程对象已释放时拿不到退出码，忽略 */ }
            };

            if (!_process.Start())
            {
                LoggerHelper.Error("dsh web 进程启动失败");
                Url = FallbackUrl;
                return Url;
            }

            AttachToJobObject(_process);

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (finished == tcs.Task && !string.IsNullOrEmpty(tcs.Task.Result))
            {
                Url = tcs.Task.Result;
                LastError = "";
                LoggerHelper.Info($"dsh web 已就绪: {Url}");
                return Url;
            }

            LoggerHelper.Error("dsh web 未在超时时间内输出地址（dsh 未安装，或 3080 端口已被占用？）");
            AppendLastError($"等待 dsh web 输出服务地址超时（{timeoutMs / 1000} 秒）——dsh 未安装或损坏，或 3080 端口被占用。");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("启动 dsh web 失败", ex);
        }

        Url = FallbackUrl;
        return Url;
    }

    /// <summary>
    /// 检测本地是否安装了 dsh（DeepSeek harness）：在 PATH 中查找 dsh 命令。
    /// 复用与 dsh web 相同的 PowerShell 上下文，因此安装但 PATH 未生效的情况也能一致判定。
    /// </summary>
    /// <returns>已安装返回 true；未安装或检测异常返回 false</returns>
    public async Task<bool> IsDshInstalledAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                // Get-Command 命中 npm 全局安装的 dsh.ps1 即视为已安装；否则 exit 1
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " +
                    "\"if (Get-Command dsh -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return false;

            // 防止异常情况下 PowerShell 卡住，设置较短超时后判定为未安装
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("检测 dsh 安装状态失败（按未安装处理）", ex);
            return false;
        }
    }

    /// <summary>
    /// 清理上一实例异常退出（被强杀/崩溃/处于父作业）残留的 dsh web 进程。
    /// 常规退出走 Stop/ProcessExit/Job Object 兜底，但进程被 Task Manager 强杀且处于父作业时
    /// 上述机制可能全部失效；本方法在每次拉起新服务前执行，确保 3080 端口不被旧进程长期占用。
    /// 此时本实例尚未启动服务，只会命中上一实例的残留进程，不会误伤自身。
    /// </summary>
    public static void KillStaleDshProcesses()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                // 命中命令行含 "dsh web" 的 powershell 进程（即本程序拉起的服务），
                // 连同其 node 子进程一并强杀（-Recurse 杀进程树）
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " +
                    "\"Get-CimInstance Win32_Process -Filter 'CommandLine LIKE '%dsh web%' " +
                    "| ForEach-Object { Stop-Process -Id $_.ProcessId -Force -Recurse }\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return;
            // 同步等待清理完成，保证随后拉起的新服务能立即占用 3080
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("清理残留 dsh web 进程失败（忽略）", ex);
        }
    }

    /// <summary>终止 dsh web 进程树，随程序退出调用</summary>
    public void Stop()
    {
        try
        {
            // PowerShell 会再拉起 node 作为真正的服务进程，需连同子进程一起终止
            if (_process is not null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                // 等进程真正退出，确保 3080 端口在本程序重启（如升级后自动拉起）前已释放
                _process.WaitForExit(1500);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("终止 dsh web 进程失败", ex);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            // 关闭作业句柄会触发 KILL_ON_JOB_CLOSE，回收 Kill 未覆盖到的残留进程
            if (_jobHandle != IntPtr.Zero)
            {
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 把 dsh 进程纳入 Job Object 并启用「作业句柄关闭即终止成员进程」。
    /// 本程序若被强制结束就来不及执行 Stop，会残留 node 服务进程长期占用 3080，
    /// 导致下次启动失败；交给作业对象后由系统在句柄关闭时统一回收。
    /// </summary>
    private void AttachToJobObject(Process process)
    {
        try
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero) return;

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformationClass,
                        ptr, (uint)length))
                {
                    LoggerHelper.Error($"SetInformationJobObject 失败: {Marshal.GetLastWin32Error()}");
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            // 子进程会继承作业成员资格，node 服务进程随之纳入同一作业
            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
                LoggerHelper.Error($"AssignProcessToJobObject 失败: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            // 本进程已属于其他作业时会失败；忽略即可，仍有 Stop() 兜底
            LoggerHelper.Error("将 dsh 进程加入 Job Object 失败（忽略）", ex);
        }
    }

    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Dispose() => Stop();
}
