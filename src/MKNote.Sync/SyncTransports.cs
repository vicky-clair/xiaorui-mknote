using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace MKNote.Sync;

/// <summary>
/// 同步传输层抽象接口：统一屏蔽本地目录、群晖 NAS SMB 共享及 WebDAV 服务器的读写差异。
/// </summary>
public interface ISyncTransport : IDisposable
{
    Task TestConnectionAsync(CancellationToken ct = default);
    Task EnsureDirectoryAsync(string relativeDir, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, CancellationToken ct = default);
    Task<byte[]?> ReadFileAsync(string relativePath, CancellationToken ct = default);
    Task WriteFileAsync(string relativePath, byte[] data, CancellationToken ct = default);
}

/// <summary>
/// 本地文件夹与群晖 NAS SMB / 局域网 UNC 共享目录传输实现（如 \\192.168.1.100\homes\MKNoteSync）。
/// </summary>
public sealed class FolderSyncTransport : ISyncTransport
{
    private readonly string _rootPath;

    public FolderSyncTransport(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("同步路径不能为空。", nameof(rootPath));
        _rootPath = rootPath.Trim();
    }

    private string Resolve(string relativePath) =>
        Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));

    public Task TestConnectionAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_rootPath);
        var probe = Path.Combine(_rootPath, ".mknote_probe_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return Task.CompletedTask;
    }

    public Task EnsureDirectoryAsync(string relativeDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Resolve(relativeDir));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, CancellationToken ct = default)
    {
        var dir = Resolve(relativeDir);
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<string>>([]);
        var names = Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public async Task<byte[]?> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct);
    }

    public async Task WriteFileAsync(string relativePath, byte[] data, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, data, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose() { }
}

/// <summary>
/// WebDAV 协议传输实现：支持群晖 NAS WebDAV Server（HTTP 5005 / HTTPS 5006，可选信任局域网自签名证书）及各类标准 WebDAV 服务器。
/// </summary>
public sealed class WebDavSyncTransport : ISyncTransport
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public WebDavSyncTransport(string endpointUrl, string remotePath, string username, string password, bool allowSelfSignedLanCert)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new ArgumentException("WebDAV 服务器地址不能为空。", nameof(endpointUrl));

        var cleanBase = endpointUrl.Trim().TrimEnd('/');
        var cleanSub = string.IsNullOrWhiteSpace(remotePath) ? "" : "/" + remotePath.Trim().Trim('/');
        var full = cleanBase + cleanSub + "/";
        _baseUri = new Uri(full, UriKind.Absolute);

        var handler = new HttpClientHandler();
        if (allowSelfSignedLanCert)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        if (!string.IsNullOrEmpty(username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MKNoteNext-Sync/1.0");
    }

    private Uri ResolveUri(string relativePath) =>
        new(_baseUri, relativePath.TrimStart('/'));

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        await EnsureDirectoryAsync("", ct);
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUri);
        req.Headers.Add("Depth", "0");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
            throw new HttpRequestException($"WebDAV 连接失败：HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");
    }

    public async Task EnsureDirectoryAsync(string relativeDir, CancellationToken ct = default)
    {
        var segments = relativeDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Ensure root folder exists first
        await TryMkcolAsync(_baseUri, ct);

        var current = "";
        foreach (var seg in segments)
        {
            current = string.IsNullOrEmpty(current) ? seg + "/" : current + seg + "/";
            await TryMkcolAsync(ResolveUri(current), ct);
        }
    }

    private async Task TryMkcolAsync(Uri uri, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), uri);
        using var resp = await _http.SendAsync(req, ct);
        // 201 Created, 405 Method Not Allowed (already exists), 301/409 ok if parent exists
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("WebDAV 认证失败：请检查用户名或密码/应用专用密码。");
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, CancellationToken ct = default)
    {
        var dirUri = ResolveUri(relativeDir.TrimEnd('/') + "/");
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), dirUri);
        req.Headers.Add("Depth", "1");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return [];
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
            throw new HttpRequestException($"WebDAV PROPFIND 失败：HTTP {(int)resp.StatusCode}");

        var xmlText = await resp.Content.ReadAsStringAsync(ct);
        var doc = XDocument.Parse(xmlText);
        XNamespace dav = "DAV:";
        var results = new List<string>();
        var dirPathNorm = Uri.UnescapeDataString(dirUri.AbsolutePath).TrimEnd('/');

        foreach (var response in doc.Descendants(dav + "response"))
        {
            var href = response.Element(dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;

            var hrefPath = Uri.TryCreate(href, UriKind.Absolute, out var abs)
                ? Uri.UnescapeDataString(abs.AbsolutePath)
                : Uri.UnescapeDataString(href);
            var norm = hrefPath.TrimEnd('/');
            if (norm.Equals(dirPathNorm, StringComparison.OrdinalIgnoreCase)) continue;
            if (hrefPath.EndsWith('/')) continue;

            var fileName = norm.Contains('/') ? norm[(norm.LastIndexOf('/') + 1)..] : norm;
            if (!string.IsNullOrWhiteSpace(fileName))
                results.Add(fileName);
        }
        return results;
    }

    public async Task<byte[]?> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(ResolveUri(relativePath), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task WriteFileAsync(string relativePath, byte[] data, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PutAsync(ResolveUri(relativePath), content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
