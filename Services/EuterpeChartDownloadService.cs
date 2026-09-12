using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MdModManager.Helpers;

namespace MdModManager.Services;

public sealed record EuterpeChartDownloadProgress(int CompletedFiles, int TotalFiles, long DownloadedBytes, long TotalBytes);

public interface IEuterpeChartDownloadService
{
    Task DownloadToMdmAsync(
        long cid,
        string outputPath,
        IProgress<EuterpeChartDownloadProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class EuterpeChartDownloadService : IEuterpeChartDownloadService, IDisposable
{
    public const string DownloadScheme = "euterpe-chart";
    private const string ApiBaseUrl = "https://euterpe-org.com/api/";
    private const string DownloadBaseUrl = "https://dl.euterpe-org.com/files/charts/";
    private const string DownloadRootUrl = "https://dl.euterpe-org.com/files/";
    private const string ManifestFileName = "manifest.epk";
    private readonly HttpClient _httpClient;
    private readonly HttpClient _zipHttpClient;
    private readonly IAuthService _authService;

    public EuterpeChartDownloadService(EuterpeTokenQueryHandler tokenQueryHandler, IAuthService authService)
    {
        _authService = authService;
        _httpClient = new HttpClient(tokenQueryHandler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(EuterpeClientIdentity.UserAgent);

        _zipHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _zipHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(EuterpeClientIdentity.UserAgent);
    }

    public static string CreateTaskUrl(long cid) => $"{DownloadScheme}://charts/{cid}";

    public static bool TryGetCid(string? taskUrl, out long cid)
    {
        cid = 0;
        return Uri.TryCreate(taskUrl, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(DownloadScheme, StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Equals("charts", StringComparison.OrdinalIgnoreCase) &&
               long.TryParse(uri.AbsolutePath.Trim('/'), out cid);
    }

    public async Task DownloadToMdmAsync(
        long cid,
        string outputPath,
        IProgress<EuterpeChartDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            await DownloadManifestToMdmAsync(cid, outputPath, progress, ct).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception manifestException)
        {
            // 文件服务异常时仅回退一次到网站的 ZIP 构建接口，不在此处重试。
            RuntimeLog.Write("EuterpeDownload", $"Manifest download failed for chart {cid}; trying ZIP build once: {manifestException.Message}");
            TryDeleteFile(outputPath);

            try
            {
                await DownloadZipToMdmAsync(cid, outputPath, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception zipException)
            {
                TryDeleteFile(outputPath);
                throw new InvalidOperationException(
                    $"Euterpe 文件服务下载失败（{manifestException.Message}）；ZIP 构建下载也失败（{zipException.Message}）",
                    zipException);
            }
        }
    }

    private async Task DownloadManifestToMdmAsync(
        long cid,
        string outputPath,
        IProgress<EuterpeChartDownloadProgress>? progress,
        CancellationToken ct)
    {
        var workFolder = Path.Combine(Path.GetTempPath(), "MuseDashTOOL", "Euterpe", $"{cid}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workFolder);

        try
        {
            var manifestPath = Path.Combine(workFolder, ManifestFileName);
            await DownloadFileAsync(cid, ManifestFileName, manifestPath, ct).ConfigureAwait(false);

            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
            var manifest = MsgPackDecoder.Decode(manifestBytes) as JsonObject
                ?? throw new InvalidDataException("manifest.epk 格式无效");
            var files = manifest["files"] as JsonObject
                ?? throw new InvalidDataException("manifest.epk 缺少 files 清单");

            var entries = files
                .Where(entry => !entry.Key.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new ManifestFile(entry.Key, ReadFileSize(entry.Value)))
                .ToArray();
            var totalBytes = entries.Sum(entry => Math.Max(0, entry.Size)) + new FileInfo(manifestPath).Length;
            var downloadedBytes = new FileInfo(manifestPath).Length;
            progress?.Report(new EuterpeChartDownloadProgress(0, entries.Length, downloadedBytes, totalBytes));

            for (var index = 0; index < entries.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[index];
                var filePath = ResolveSafeFilePath(workFolder, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await DownloadFileAsync(cid, entry.Name, filePath, ct).ConfigureAwait(false);
                downloadedBytes += new FileInfo(filePath).Length;
                progress?.Report(new EuterpeChartDownloadProgress(index + 1, entries.Length, downloadedBytes, totalBytes));
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            ZipFile.CreateFromDirectory(workFolder, outputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            ChartService.ConvertEpkToInfoJsonInPlace(outputPath);
            ValidateConvertedPackage(outputPath);
        }
        finally
        {
            TryDeleteDirectory(workFolder);
        }
    }

    private async Task DownloadZipToMdmAsync(
        long cid,
        string outputPath,
        IProgress<EuterpeChartDownloadProgress>? progress,
        CancellationToken ct)
    {
        EuterpeRateLimitGate.ThrowIfBlocked();
        var token = await _authService.GetAccessTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Euterpe 登录已失效，请退出账号后重新登录");

        using var buildRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}workspace/charts/{cid}/build-zip");
        buildRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        buildRequest.Headers.Add("X-Request-Id", Guid.CreateVersion7().ToString());
        using var buildResponse = await _zipHttpClient.SendAsync(buildRequest, ct).ConfigureAwait(false);
        await EuterpeHttpError.EnsureSuccessAsync(buildResponse, "构建 Euterpe ZIP", ct).ConfigureAwait(false);

        await using var buildStream = await buildResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buildDocument = await JsonDocument.ParseAsync(buildStream, cancellationToken: ct).ConfigureAwait(false);
        if (!buildDocument.RootElement.TryGetProperty("path", out var pathElement) ||
            string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            throw new InvalidDataException("Euterpe ZIP 构建响应缺少下载路径");
        }

        var downloadUri = ResolveZipDownloadUri(pathElement.GetString()!);
        var authorizedDownloadUri = AppendToken(downloadUri, token);
        EuterpeRateLimitGate.ThrowIfBlocked();
        using var downloadResponse = await _zipHttpClient.GetAsync(authorizedDownloadUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EuterpeHttpError.EnsureSuccessAsync(downloadResponse, "下载 Euterpe ZIP", ct).ConfigureAwait(false);

        var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
        progress?.Report(new EuterpeChartDownloadProgress(0, 1, 0, totalBytes));
        await using (var source = await downloadResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long downloadedBytes = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                downloadedBytes += read;
                progress?.Report(new EuterpeChartDownloadProgress(0, 1, downloadedBytes, totalBytes));
            }
        }

        ChartService.ConvertEpkToInfoJsonInPlace(outputPath);
        ValidateConvertedPackage(outputPath);
        progress?.Report(new EuterpeChartDownloadProgress(1, 1, totalBytes, totalBytes));
    }

    private async Task DownloadFileAsync(long cid, string fileName, string destinationPath, CancellationToken ct)
    {
        var encodedName = string.Join('/', fileName.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        var url = $"{DownloadBaseUrl}{cid}/{encodedName}";
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EuterpeHttpError.EnsureSuccessAsync(response, $"下载 {fileName}", ct).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private static Uri ResolveZipDownloadUri(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Scheme != Uri.UriSchemeHttps || !IsEuterpeHost(absoluteUri.Host))
                throw new InvalidDataException("Euterpe ZIP 构建返回了不受信任的下载地址");
            return absoluteUri;
        }

        if (path.StartsWith("/", StringComparison.Ordinal))
            return new Uri(new Uri("https://euterpe-org.com"), path);

        if (path.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Euterpe ZIP 构建返回了无效的下载路径");

        return new Uri(new Uri(DownloadRootUrl), path);
    }

    private static bool IsEuterpeHost(string host) =>
        host.Equals("euterpe-org.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".euterpe-org.com", StringComparison.OrdinalIgnoreCase);

    private static Uri AppendToken(Uri uri, string token)
    {
        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return new Uri($"{uri}{separator}t={Uri.EscapeDataString(token)}");
    }

    private static string ResolveSafeFilePath(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName))
            throw new InvalidDataException("manifest.epk 包含无效文件名");

        var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, fileName.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("manifest.epk 包含越界文件路径");
        return fullPath;
    }

    private static long ReadFileSize(JsonNode? node)
    {
        if (node is JsonObject entry && entry["size"] is JsonValue size && size.TryGetValue<long>(out var result))
            return result;
        return 0;
    }

    private static void ValidateConvertedPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (!archive.Entries.Any(entry => entry.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Euterpe 谱面未能生成 info.json");
        if (!archive.Entries.Any(entry => Path.GetExtension(entry.Name).Equals(".bms", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Euterpe 谱面没有可用的 BMS 文件");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _zipHttpClient.Dispose();
    }

    private sealed record ManifestFile(string Name, long Size);
}
