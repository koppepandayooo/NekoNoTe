using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace NekoNoTe.Updates;

public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string DownloadUrl, string FileName, long Size, string? Sha256);

/// <summary>GitHub のリリースを見て、新しい版があればインストーラーをダウンロードする</summary>
public static class UpdateService
{
    private const string Repository = "koppepandayooo/NekoNoTe";
    private const string InstallerPrefix = "NekoNoTe-Setup-";
    private static readonly string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NekoNoTe");
        return client;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>インストーラーで入れた NekoNoTe の時だけ、ここからアップデートできる</summary>
    public static bool CanInstall =>
        Environment.ProcessPath is { } exe && File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "unins000.exe"));

    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith(InstallerPrefix, StringComparison.Ordinal) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return null;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.Ordinal))
                sha = d["sha256:".Length..];
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new ReleaseInfo(version, tag, notes, url, Path.GetFileName(name), asset.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    /// <summary>インストーラーをダウンロードして確かめ、そのパスを返す</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NekoNoTe-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, release.FileName);
        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                progress.Report(Math.Min(1.0, (double)total / release.Size));
            }
        }

        if (new FileInfo(path).Length != release.Size)
            throw new InvalidDataException("ダウンロードしたファイルの大きさが違います。もう一度試してください。");
        if (release.Sha256 is { } expected)
        {
            await using var check = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ダウンロードしたファイルが壊れています。もう一度試してください。");
        }
        return path;
    }
}
