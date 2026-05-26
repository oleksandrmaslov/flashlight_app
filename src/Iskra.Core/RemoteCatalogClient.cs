using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Iskra.Core;

public sealed class RemoteCatalogException : Exception
{
    public RemoteCatalogException(string msg, Exception? inner = null) : base(msg, inner) { }
}

/// <summary>
/// Outcome of <see cref="RemoteCatalogClient.FetchAsync"/>. Either we got a
/// fresh verified catalog and saved it on disk, or we have a reason why not.
/// </summary>
public sealed record RemoteCatalogResult(
    Catalog? Catalog,
    string? LocalCatalogPath,
    string? LocalSignaturePath,
    string TagName,
    bool ChangedFromCached,
    RemoteCatalogStatus Status,
    string? Message);

public enum RemoteCatalogStatus
{
    Updated,              // downloaded + verified + replaced cache
    AlreadyUpToDate,      // same tag as we have cached; cache returned
    NoRelease,            // /releases/latest returned 404
    NetworkError,         // any HTTP failure short of 404
    BadSignature,         // download succeeded but signature didn't verify
    AssetsMissing,        // release doesn't have catalog.json + catalog.json.sig
    ParseError,           // catalog.json downloaded but failed to parse
}

/// <summary>
/// Fetches signed catalog releases from the iskra-catalog repo on GitHub.
/// <para>The repo is public so the GET is anonymous. The signature is verified
/// against <see cref="CatalogTrust.EmbeddedPublicKey"/>; if it doesn't match,
/// the download is discarded and the cache is left untouched.</para>
/// <para>Cache layout: <c>%LOCALAPPDATA%\Iskra\catalog\latest.json</c> +
/// <c>latest.json.sig</c> + <c>latest.tag</c> (just the tag name, so a future
/// poll can short-circuit when nothing has changed).</para>
/// </summary>
public sealed class RemoteCatalogClient
{
    public const string DefaultDirectoryName = "Iskra";
    public const string DefaultSubdirectoryName = "catalog";
    public const string CatalogFileName        = "latest.json";
    public const string SignatureFileName      = "latest.json.sig";
    public const string TagFileName            = "latest.tag";

    public const string ApiBaseUrl  = "https://api.github.com";
    public const string ApiAccept   = "application/vnd.github+json";
    public const string ApiVersion  = "2022-11-28";

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _cacheDir;

    public RemoteCatalogClient(
        HttpClient http,
        string owner = "oleksandrmaslov",
        string repo  = "iskra-catalog",
        string? cacheDirOverride = null)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))  throw new ArgumentException("repo required",  nameof(repo));
        _http  = http;
        _owner = owner;
        _repo  = repo;
        _cacheDir = cacheDirOverride ?? DefaultCacheDir();
    }

    public static string DefaultCacheDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, DefaultDirectoryName, DefaultSubdirectoryName);
    }

    public string CatalogPath   => Path.Combine(_cacheDir, CatalogFileName);
    public string SignaturePath => Path.Combine(_cacheDir, SignatureFileName);
    public string TagPath       => Path.Combine(_cacheDir, TagFileName);

    /// <summary>
    /// Returns the catalog cached on disk from a previous successful fetch,
    /// or <c>null</c> if there's nothing cached. Re-verifies the signature
    /// against the currently embedded public key (catches key rotation).
    /// </summary>
    public Catalog? LoadCached()
    {
        if (!File.Exists(CatalogPath) || !File.Exists(SignaturePath)) return null;
        var bytes = File.ReadAllBytes(CatalogPath);
        var sig   = Convert.FromBase64String(File.ReadAllText(SignaturePath).Trim());
        var pub   = CatalogTrust.EmbeddedPublicKey;
        if (pub is null || !CatalogSignature.Verify(bytes, sig, pub)) return null;
        try { return CatalogJson.Parse(System.Text.Encoding.UTF8.GetString(bytes)); }
        catch (CatalogParseException) { return null; }
    }

    public string? CachedTag()
    {
        if (!File.Exists(TagPath)) return null;
        try { return File.ReadAllText(TagPath).Trim(); }
        catch { return null; }
    }

    /// <summary>
    /// Fetches the latest release of the catalog repo; if the tag has changed
    /// since the last successful fetch (or no cache exists), downloads the
    /// new assets, verifies the signature, and commits them to the cache
    /// atomically.
    /// </summary>
    public async Task<RemoteCatalogResult> FetchAsync(CancellationToken ct = default)
    {
        // 1) GET /repos/{owner}/{repo}/releases/latest
        var url = $"{ApiBaseUrl}/repos/{_owner}/{_repo}/releases/latest";
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(NewApiRequest(HttpMethod.Get, url), ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { return Failure(RemoteCatalogStatus.NetworkError, ex.Message); }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Failure(RemoteCatalogStatus.NoRelease,
                    $"{_owner}/{_repo} has no releases yet");
            if (!resp.IsSuccessStatusCode)
                return Failure(RemoteCatalogStatus.NetworkError,
                    $"GET releases/latest → {(int)resp.StatusCode} {resp.ReasonPhrase}");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrEmpty(tagName))
                return Failure(RemoteCatalogStatus.AssetsMissing, "release has no tag_name");

            var cachedTag = CachedTag();
            var assetsSection = root.TryGetProperty("assets", out var aEl) && aEl.ValueKind == JsonValueKind.Array
                ? aEl : default;
            if (assetsSection.ValueKind != JsonValueKind.Array)
                return Failure(RemoteCatalogStatus.AssetsMissing, "release has no assets array");

            string? catalogUrl = null, sigUrl = null;
            foreach (var a in assetsSection.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var nEl) || !a.TryGetProperty("browser_download_url", out var uEl))
                    continue;
                var name = nEl.GetString();
                if (string.Equals(name, "catalog.json", StringComparison.Ordinal))         catalogUrl = uEl.GetString();
                else if (string.Equals(name, "catalog.json.sig", StringComparison.Ordinal)) sigUrl     = uEl.GetString();
            }
            if (catalogUrl is null || sigUrl is null)
                return Failure(RemoteCatalogStatus.AssetsMissing,
                    $"{tagName} is missing catalog.json or catalog.json.sig");

            if (string.Equals(cachedTag, tagName, StringComparison.Ordinal) && File.Exists(CatalogPath))
            {
                var cached = LoadCached();
                if (cached is not null)
                    return new RemoteCatalogResult(
                        Catalog: cached, LocalCatalogPath: CatalogPath, LocalSignaturePath: SignaturePath,
                        TagName: tagName, ChangedFromCached: false,
                        Status: RemoteCatalogStatus.AlreadyUpToDate, Message: null);
            }

            // 2) Download the two assets to disk (anonymous; public repo).
            byte[] catalogBytes, sigBytes;
            try
            {
                catalogBytes = await GetBytesAsync(catalogUrl, ct).ConfigureAwait(false);
                sigBytes     = await GetBytesAsync(sigUrl, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return Failure(RemoteCatalogStatus.NetworkError, ex.Message);
            }

            // 3) Verify signature with the embedded public key.
            var pub = CatalogTrust.EmbeddedPublicKey
                ?? throw new RemoteCatalogException("CatalogTrust.EmbeddedPublicKey is unset");
            byte[] sig;
            try { sig = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(sigBytes).Trim()); }
            catch (FormatException) { return Failure(RemoteCatalogStatus.BadSignature, "signature is not base64"); }
            if (!CatalogSignature.Verify(catalogBytes, sig, pub))
                return Failure(RemoteCatalogStatus.BadSignature,
                    "downloaded catalog signature did not match the embedded public key");

            // 4) Parse — refuse to commit an unparseable catalog.
            Catalog catalog;
            try { catalog = CatalogJson.Parse(System.Text.Encoding.UTF8.GetString(catalogBytes)); }
            catch (CatalogParseException ex) { return Failure(RemoteCatalogStatus.ParseError, ex.Message); }

            // 5) Atomic commit: write .tmp files then rename.
            Directory.CreateDirectory(_cacheDir);
            WriteAtomic(CatalogPath,   catalogBytes);
            WriteAtomic(SignaturePath, sigBytes);
            WriteAtomic(TagPath,       System.Text.Encoding.UTF8.GetBytes(tagName));

            return new RemoteCatalogResult(
                Catalog: catalog, LocalCatalogPath: CatalogPath, LocalSignaturePath: SignaturePath,
                TagName: tagName, ChangedFromCached: cachedTag != tagName,
                Status: RemoteCatalogStatus.Updated, Message: null);
        }
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Iskra");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {url} → {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private HttpRequestMessage NewApiRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ApiAccept));
        req.Headers.UserAgent.ParseAdd("Iskra");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        return req;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, path, overwrite: true); }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    private RemoteCatalogResult Failure(RemoteCatalogStatus status, string message)
        => new(Catalog: null, LocalCatalogPath: null, LocalSignaturePath: null,
               TagName: "", ChangedFromCached: false, Status: status, Message: message);
}
