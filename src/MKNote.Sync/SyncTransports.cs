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
    Task DeleteFileAsync(string relativePath, CancellationToken ct = default);
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
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rootPath);
        var probe = Path.Combine(_rootPath, ".mknote_probe_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return Task.CompletedTask;
    }

    public Task EnsureDirectoryAsync(string relativeDir, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Resolve(relativeDir));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = Resolve(relativeDir);
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<string>>([]);
        var names = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && !n!.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public async Task<byte[]?> ReadFileAsync(string relativePath, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(string relativePath, byte[] data, CancellationToken ct = default)
    {
        var path = Resolve(relativePath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = Resolve(relativePath);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

/// <summary>
/// WebDAV 协议传输实现：支持群晖 NAS WebDAV Server（HTTP 5005 / HTTPS 5006，可选信任局域网自签名证书）及各类标准 WebDAV 服务器。
/// 内置多级目录逐级 MKCOL 创建、HTTP 重定向保留认证头、429/503 限流退避重试与连接池并发优化。
/// </summary>
public sealed class WebDavSyncTransport : ISyncTransport
{
    private const int MaxRetries = 3;
    private const string PropfindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>";

    private readonly HttpClient _http;
    private readonly Uri _endpointUri;
    private readonly Uri _baseUri;
    private readonly string[] _remoteSubSegments;
    private readonly AuthenticationHeaderValue? _authHeader;
    private readonly SemaphoreSlim _dirInitLock = new(1, 1);
    private bool _rootEnsured;

    public WebDavSyncTransport(string endpointUrl, string remotePath, string username, string password, bool allowSelfSignedLanCert)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new ArgumentException("WebDAV 服务器地址不能为空。", nameof(endpointUrl));

        var cleanBase = endpointUrl.Trim().TrimEnd('/');
        _endpointUri = new Uri(cleanBase + "/", UriKind.Absolute);

        _remoteSubSegments = string.IsNullOrWhiteSpace(remotePath)
            ? []
            : remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var cleanSub = _remoteSubSegments.Length == 0
            ? ""
            : "/" + string.Join('/', _remoteSubSegments.Select(Uri.EscapeDataString));
        _baseUri = new Uri(cleanBase + cleanSub + "/", UriKind.Absolute);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // 手动处理重定向以保留 Basic 认证头，避免 301/307 丢凭据
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        if (allowSelfSignedLanCert)
        {
            // 仅对局域网私网地址（RFC1918 / Loopback / .local / 无点主机名）放行自签名证书；
            // 对公网域名与公网 IP 仍强制执行标准 TLS 证书链校验，防止公网中间人攻击（MITM）。
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, _, _, sslPolicyErrors) =>
            {
                if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                    return true;
                var targetHost = (sender as System.Net.Security.SslStream)?.TargetHostName;
                if (string.IsNullOrWhiteSpace(targetHost))
                    targetHost = _endpointUri.Host;
                return IsPrivateLanHost(targetHost);
            };
        }

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.ExpectContinue = false; // 避免部分 WebDAV 代理因 100-continue 挂起
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MKNoteNext-Sync/1.1");

        if (!string.IsNullOrEmpty(username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _authHeader = new AuthenticationHeaderValue("Basic", token);
            _http.DefaultRequestHeaders.Authorization = _authHeader;
        }
    }

    private Uri ResolveUri(string relativePath)
    {
        var trimmed = relativePath.TrimStart('/');
        if (string.IsNullOrEmpty(trimmed)) return _baseUri;
        var trailingSlash = trimmed.EndsWith('/');
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var escaped = string.Join('/', parts.Select(Uri.EscapeDataString));
        if (trailingSlash) escaped += "/";
        return new Uri(_baseUri, escaped);
    }

    /// <summary>
    /// 统一发送 WebDAV 请求：自动处理同源重定向（保留认证头）及 429/502/503/504 临时错误指数退避重试。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Uri, HttpRequestMessage> requestFactory,
        Uri initialUri,
        CancellationToken ct)
    {
        var currentUri = initialUri;
        Exception? lastException = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage? resp = null;
            try
            {
                // 最多跟随 3 次重定向（如补齐末尾斜杠或 http -> https 升级）
                for (int redirect = 0; redirect < 3; redirect++)
                {
                    using var req = requestFactory(currentUri);
                    if (_authHeader is not null && req.Headers.Authorization is null)
                        req.Headers.Authorization = _authHeader;

                    resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

                    if (resp.StatusCode is HttpStatusCode.MovedPermanently
                        or HttpStatusCode.Found
                        or HttpStatusCode.TemporaryRedirect
                        or HttpStatusCode.PermanentRedirect)
                    {
                        var location = resp.Headers.Location;
                        if (location is null) break;
                        var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        resp.Dispose();
                        resp = null;
                        currentUri = nextUri;
                        continue;
                    }
                    break;
                }

                if (resp is null)
                    throw new HttpRequestException("WebDAV 重定向次数过多。");

                if (resp.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.GatewayTimeout)
                {
                    if (attempt < MaxRetries)
                    {
                        var delay = GetRetryDelay(resp, attempt);
                        resp.Dispose();
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                return resp;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                resp?.Dispose();
                lastException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                // 单次请求超时重试（非用户主动取消）
                resp?.Dispose();
                lastException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(800 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
            catch
            {
                resp?.Dispose();
                throw;
            }
        }

        throw lastException ?? new HttpRequestException("WebDAV 请求多次重试后仍然失败。");
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage resp, int attempt)
    {
        if (resp.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(15))
            return delta;
        return TimeSpan.FromMilliseconds(600 * Math.Pow(2, attempt));
    }

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        await EnsureDirectoryAsync("", ct).ConfigureAwait(false);

        using var propResp = await SendWithRetryAsync(uri =>
        {
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
            req.Headers.Add("Depth", "0");
            req.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
            return req;
        }, _baseUri, ct).ConfigureAwait(false);

        if (propResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("WebDAV 认证失败或无访问权限：请检查用户名与密码/应用专用密码。");

        if (!propResp.IsSuccessStatusCode && propResp.StatusCode != HttpStatusCode.MultiStatus)
            throw new HttpRequestException($"WebDAV 连接失败：HTTP {(int)propResp.StatusCode} ({propResp.ReasonPhrase})");

        // 执行轻量级写入与删除探针，提前发现“只读权限”或“路径不可写”问题
        var probeName = $".mknote_probe_{Guid.NewGuid():N}.tmp";
        await WriteFileAsync(probeName, "ok"u8.ToArray(), ct).ConfigureAwait(false);
        await TryDeleteFileAsync(probeName, ct).ConfigureAwait(false);
    }

    public async Task EnsureDirectoryAsync(string relativeDir, CancellationToken ct = default)
    {
        // 先串行确保远端根目录多级路径存在（避免 409 Conflict）
        if (!_rootEnsured)
        {
            await _dirInitLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_rootEnsured)
                {
                    var built = "";
                    foreach (var seg in _remoteSubSegments)
                    {
                        built = string.IsNullOrEmpty(built)
                            ? Uri.EscapeDataString(seg) + "/"
                            : built + Uri.EscapeDataString(seg) + "/";
                        await TryMkcolAsync(new Uri(_endpointUri, built), ct).ConfigureAwait(false);
                    }
                    _rootEnsured = true;
                }
            }
            finally
            {
                _dirInitLock.Release();
            }
        }

        var segments = relativeDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var seg in segments)
        {
            current = string.IsNullOrEmpty(current) ? seg + "/" : current + seg + "/";
            await TryMkcolAsync(ResolveUri(current), ct).ConfigureAwait(false);
        }
    }

    private async Task TryMkcolAsync(Uri uri, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(u => new HttpRequestMessage(new HttpMethod("MKCOL"), u), uri, ct).ConfigureAwait(false);

        // 201 Created / 200 / 204 / 405 Method Not Allowed（目录已存在）/ 301 / 302 均视为正常
        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.MethodNotAllowed)
            return;

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("WebDAV 认证失败或无目录创建权限：请检查账号权限与远端路径。");

        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new HttpRequestException($"WebDAV 无法创建目录 {uri.AbsolutePath}（HTTP 409 Conflict）：请确认上级共享目录已存在且有写入权限。");
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, CancellationToken ct = default)
    {
        var dirUri = ResolveUri(relativeDir.TrimEnd('/') + "/");
        using var resp = await SendWithRetryAsync(uri =>
        {
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
            req.Headers.Add("Depth", "1");
            req.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
            return req;
        }, dirUri, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound) return [];
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("WebDAV 无权限读取目录列表，请检查认证信息。");
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
            throw new HttpRequestException($"WebDAV PROPFIND 失败：HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");

        var xmlText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(xmlText)) return [];

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlText);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"WebDAV 返回的目录列表不是有效的 XML（请检查服务器地址是否指向正确的 WebDAV 端点）：{ex.Message}", ex);
        }

        XNamespace dav = "DAV:";
        var results = new List<string>();
        var dirPathNorm = Uri.UnescapeDataString(dirUri.AbsolutePath).TrimEnd('/');

        foreach (var response in doc.Descendants(dav + "response"))
        {
            // 跳过子目录集合节点
            if (response.Descendants(dav + "collection").Any()) continue;

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
        var fileUri = ResolveUri(relativePath);
        using var resp = await SendWithRetryAsync(uri => new HttpRequestMessage(HttpMethod.Get, uri), fileUri, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"WebDAV 无权限读取文件 {relativePath}。");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"WebDAV 读取文件 {relativePath} 失败：HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(string relativePath, byte[] data, CancellationToken ct = default)
    {
        var fileUri = ResolveUri(relativePath);
        using var resp = await SendWithRetryAsync(uri =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, uri);
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Content = content;
            return req;
        }, fileUri, ct).ConfigureAwait(false);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"WebDAV 无权限写入文件 {relativePath}（HTTP {(int)resp.StatusCode}）。");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"WebDAV 上传文件 {relativePath} 失败：HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");
    }

    public async Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        var fileUri = ResolveUri(relativePath);
        using var resp = await SendWithRetryAsync(uri => new HttpRequestMessage(HttpMethod.Delete, uri), fileUri, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"WebDAV 无权限删除文件 {relativePath}（HTTP {(int)resp.StatusCode}）。");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"WebDAV 删除文件 {relativePath} 失败：HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");
    }

    private async Task TryDeleteFileAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            var fileUri = ResolveUri(relativePath);
            using var resp = await SendWithRetryAsync(uri => new HttpRequestMessage(HttpMethod.Delete, uri), fileUri, ct).ConfigureAwait(false);
        }
        catch
        {
            // 忽略清理探针文件时的非致命异常
        }
    }

    /// <summary>
    /// 判断目标主机名/IP 是否属于局域网私网范围（Loopback、RFC1918 10/8、172.16/12、192.168/16、链路本地 169.254/16、IPv6 ULA、.local/.lan 或单段内网主机名）。
    /// 用于将自签名证书信任严格限定在可信局域网内，避免公网 HTTPS 连接退化为无证书校验。
    /// </summary>
    public static bool IsPrivateLanHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var clean = host.Trim().Trim('[', ']');
        if (clean.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(clean, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
            {
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254);
            }
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && bytes.Length == 16)
            {
                // IPv6 链路本地 fe80::/10 或唯一本地地址 fc00::/7
                return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
            }
            return false;
        }

        // 无点号的单段局域网主机名（如 "synology-nas"）视为内网主机；含点公网域名一律返回 false
        return !clean.Contains('.');
    }

    public void Dispose()
    {
        _dirInitLock.Dispose();
        _http.Dispose();
    }
}
