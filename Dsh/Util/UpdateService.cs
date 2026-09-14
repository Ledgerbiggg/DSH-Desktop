using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Util;

/// <summary>远程升级信息（对应 GitHub raw version.json）</summary>
public class UpdateInfo
{
    /// <summary>最新版本号，如 "0.1.0"。远程 JSON 键为小写，
    /// System.Text.Json 默认区分大小写，必须显式映射，否则反序列化为空串</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>更新说明（展示给用户）</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>发布页地址</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

/// <summary>自动升级服务：从 GitHub 拉取 version.json 比较版本，下载安装包</summary>
public class UpdateService
{
    // GitHub raw 版本信息地址（version.json 统一放仓库根目录，push 到 main 后即可访问）
    private const string VersionJsonUrl =
        "https://raw.githubusercontent.com/Ledgerbiggg/DSH-Desktop/main/version.json";

    // 安装包下载模板（与 CI 产物命名一致）
    private const string DownloadUrlTemplate =
        "https://github.com/Ledgerbiggg/DSH-Desktop/releases/download/v{0}/Dsh-Setup-{0}.exe";

    private static readonly HttpClient Client = CreateClient(TimeSpan.FromSeconds(8));

    /// <summary>拉取远程最新版本信息；网络失败返回 null</summary>
    public async Task<UpdateInfo?> FetchLatestAsync()
    {
        try
        {
            var resp = await Client.GetStringAsync(VersionJsonUrl);
            var info = JsonSerializer.Deserialize<UpdateInfo>(resp);
            return info;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("FetchLatestAsync 失败", ex);
            return null;
        }
    }

    /// <summary>下载安装包到临时目录，报告进度百分比 0-100；失败返回 null</summary>
    public async Task<string?> DownloadInstallerAsync(
        string version, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var url = string.Format(DownloadUrlTemplate, version);
        var tempPath = Path.Combine(Path.GetTempPath(), $"Dsh-Setup-{version}.exe");

        // 长超时客户端：安装包可能较大
        using var dlClient = CreateClient(TimeSpan.FromMinutes(15));
        try
        {
            using var resp = await dlClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tempPath);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                    progress?.Report((int)(downloaded * 100 / total));
            }

            progress?.Report(100);
            return tempPath;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"下载安装包失败: {url}", ex);
            // 清理残留文件
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return null;
        }
    }

    /// <summary>判断远程版本是否比本地新（支持 SemVer 预发布后缀）</summary>
    public static bool IsNewer(string remote, string local)
    {
        if (!TryParse(remote, out var rMajor, out var rMinor, out var rPatch, out var rPre))
            return false;
        if (!TryParse(local, out var lMajor, out var lMinor, out var lPatch, out var lPre))
            return false;

        var cmp = rMajor.CompareTo(lMajor);
        if (cmp != 0) return cmp > 0;
        cmp = rMinor.CompareTo(lMinor);
        if (cmp != 0) return cmp > 0;
        cmp = rPatch.CompareTo(lPatch);
        if (cmp != 0) return cmp > 0;

        // 三元组相等：本地预览版、远程正式版 => 视为更新
        if (lPre && !rPre) return true;
        return false;
    }

    /// <summary>解析 SemVer：主.次.修[-预发布]，失败返回 false</summary>
    private static bool TryParse(string version,
        out int major, out int minor, out int patch, out bool preRelease)
    {
        major = minor = patch = 0;
        preRelease = false;

        if (string.IsNullOrWhiteSpace(version)) return false;
        var v = version.Trim().TrimStart('v', 'V');

        // 分离预发布后缀
        var mainPart = v;
        if (v.Contains('-'))
        {
            mainPart = v[..v.IndexOf('-')];
            preRelease = true;
        }

        var parts = mainPart.Split('.');
        if (parts.Length < 1 || !int.TryParse(parts[0], out major)) return false;
        if (parts.Length >= 2 && int.TryParse(parts[1], out var b)) minor = b;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var c)) patch = c;
        return true;
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Dsh-UpdateChecker", "1.0"));
        return c;
    }
}
