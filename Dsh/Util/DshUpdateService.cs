using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Dsh.Util;

/// <summary>
/// DeepSeek harness（npm 包 @deepseek-ai/dsh）版本管理服务：
/// 查询本地已安装版本、npm 仓库最新版本，并执行全局包升级。
/// 与 DshHostService 保持一致，统一经 PowerShell 调用 npm，
/// 确保 PATH 解析上下文与拉起 dsh web 时相同。
/// </summary>
public class DshUpdateService
{
    /// <summary>dsh 的 npm 包名</summary>
    public const string PackageId = "@deepseek-ai/dsh";

    // 匹配 npm ls 树形输出中的 "└── @deepseek-ai/dsh@x.y.z"
    //（缩进与树形字符随 npm 版本变化，不能写死，只锚定包名@版本）
    private static readonly Regex LocalVersionPattern =
        new(Regex.Escape(PackageId) + @"@(\d+(?:\.\d+)*(?:-[\w.+-]+)?)", RegexOptions.Compiled);

    // 匹配 npm view 输出中独立成行的版本号（如 "0.3.1"，容忍 v 前缀与预发布后缀）
    private static readonly Regex LatestVersionPattern =
        new(@"^\s*v?(\d+(?:\.\d+)*(?:-[\w.+-]+)?)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>查询本地已安装的 dsh 版本；未安装或检测失败返回 null</summary>
    public async Task<string?> GetLocalVersionAsync()
    {
        var result = await RunNpmAsync($"ls -g '{PackageId}' --depth=0", 30_000).ConfigureAwait(false);
        if (result is null) return null;
        // 未安装时 npm ls 退出码非 0 且输出 "(empty)"，正则无命中即视为未安装
        var matches = LocalVersionPattern.Matches(result.Output);
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
    }

    /// <summary>查询 npm 仓库中 dsh 的最新版本；网络失败返回 null</summary>
    public async Task<string?> GetLatestVersionAsync()
    {
        var result = await RunNpmAsync($"view '{PackageId}' version", 30_000).ConfigureAwait(false);
        if (result is null || result.ExitCode != 0) return null;
        var matches = LatestVersionPattern.Matches(result.Output);
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
    }

    /// <summary>升级 dsh 到最新版（npm install -g 包名@latest）；
    /// onLine 实时回传 npm 输出行，供升级终端弹窗滚动展示进度；
    /// ct 取消时强杀 npm 进程树，返回 false；返回是否成功</summary>
    public async Task<bool> InstallLatestAsync(IProgress<string>? onLine = null, CancellationToken ct = default)
    {
        // 全量下载安装可能较慢（国内网络尤甚），超时放宽到 5 分钟
        var result = await RunNpmAsync($"install -g '{PackageId}@latest'", 300_000, onLine, ct).ConfigureAwait(false);
        return result is { ExitCode: 0 };
    }

    /// <summary>npm 命令执行结果：退出码 + stdout/stderr 合并输出</summary>
    private sealed record NpmResult(int ExitCode, string Output);

    /// <summary>
    /// 经 PowerShell 执行 npm 命令并等待退出。包名用单引号包住，
    /// 避免 PowerShell 对 @ 开头记号做特殊解析。
    /// onLine 非空时逐行实时回传输出（升级弹窗展示进度）。
    /// ct 取消或超时会强杀 npm 整棵进程树；返回 null 表示启动失败、
    /// 被取消或超时（此时无可靠输出可用）。
    /// </summary>
    private static async Task<NpmResult?> RunNpmAsync(string args, int timeoutMs,
        IProgress<string>? onLine = null, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command npm " + args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return null;

            // 用户取消与超时统一走 Token：触发即杀整棵进程树（npm 会派生子进程）
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            using var killer = timeoutCts.Token.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            });

            // 逐行读 + 实时回调，两流并行读取，防止管道写满后 npm 阻塞导致死锁
            var stdout = ReadStreamAsync(process.StandardOutput, onLine);
            var stderr = ReadStreamAsync(process.StandardError, onLine);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (timeoutCts.IsCancellationRequested)
            {
                // 进程被杀后读流也可能异常中断，兜底收尾再返回
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
                if (!ct.IsCancellationRequested)
                    LoggerHelper.Error($"npm 命令超时被终止: npm {args}");
                return null;
            }

            var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine
                                                      + (await stderr.ConfigureAwait(false));
            return new NpmResult(process.ExitCode, output);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"执行 npm 命令失败: npm {args}", ex);
            return null;
        }
    }

    /// <summary>逐行读取进程输出：累积完整文本供正则解析，非空行经 onLine 实时回调</summary>
    private static async Task<string> ReadStreamAsync(StreamReader reader, IProgress<string>? onLine)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;
            sb.AppendLine(line);
            if (onLine is not null && line.Length > 0)
                onLine.Report(line);
        }
        return sb.ToString();
    }
}
