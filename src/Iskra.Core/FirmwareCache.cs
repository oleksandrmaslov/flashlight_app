namespace Iskra.Core;

public sealed class FirmwareCacheException : Exception
{
    public FirmwareCacheException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// On-disk cache for firmware ELFs downloaded from GitHub release assets.
/// Layout: <c>%LOCALAPPDATA%\Iskra\firmware-cache\&lt;owner&gt;_&lt;repo&gt;\&lt;tag&gt;\&lt;asset&gt;</c>.
/// <para>Policy: re-hash on every <see cref="GetOrDownloadAsync"/> call.
/// If the on-disk hash matches the catalog's <paramref name="expectedSha256"/>,
/// return the cached path; otherwise (mismatch or cache miss) download,
/// verify, atomically commit, and return the new path. If the downloaded
/// bytes don't match, the cache file is removed and an exception is thrown.</para>
/// </summary>
public sealed class FirmwareCache
{
    public const string DefaultDirectoryName = "Iskra";
    public const string DefaultSubdirectoryName = "firmware-cache";

    private readonly string _root;
    private readonly GitHubReleaseAssetClient _api;
    private readonly Func<CancellationToken, Task<string>> _getAccessToken;

    /// <param name="api">Pure HTTP layer.</param>
    /// <param name="getAccessToken">Caller-supplied fresh-token source (see
    /// <see cref="AccessTokenProvider.GetFreshAccessTokenAsync"/>). Called per
    /// download attempt so we always use a non-stale token.</param>
    /// <param name="rootOverride">Alternate cache root, for tests.</param>
    public FirmwareCache(
        GitHubReleaseAssetClient api,
        Func<CancellationToken, Task<string>> getAccessToken,
        string? rootOverride = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _getAccessToken = getAccessToken ?? throw new ArgumentNullException(nameof(getAccessToken));
        _root = rootOverride ?? DefaultRoot();
    }

    public static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, DefaultDirectoryName, DefaultSubdirectoryName);
    }

    public string PathFor(GitHubReleaseRef src)
    {
        var ownerRepo = src.Repo.Replace('/', '_');
        return Path.Combine(_root, ownerRepo, src.Tag, src.Asset);
    }

    /// <summary>
    /// Returns a local path containing the asset bytes, verified against
    /// <paramref name="expectedSha256"/>. Cache-hit + matching hash skips the
    /// network entirely; cache-miss or hash-mismatch triggers a download.
    /// </summary>
    public async Task<string> GetOrDownloadAsync(
        GitHubReleaseRef src,
        string expectedSha256,
        CancellationToken ct = default)
    {
        if (src is null) throw new ArgumentNullException(nameof(src));
        if (!FirmwareIntegrity.IsValidSha256Hex(expectedSha256))
            throw new ArgumentException("expectedSha256 must be 64 hex chars",
                nameof(expectedSha256));

        var dest = PathFor(src);

        if (File.Exists(dest))
        {
            var onDisk = FirmwareIntegrity.ComputeSha256Hex(dest);
            if (FirmwareIntegrity.HashesMatch(onDisk, expectedSha256))
                return dest;
            // Stale or tampered. Drop and re-download.
            File.Delete(dest);
        }

        await DownloadAndVerifyAsync(src, expectedSha256, dest, ct).ConfigureAwait(false);
        return dest;
    }

    private async Task DownloadAndVerifyAsync(
        GitHubReleaseRef src, string expectedSha256, string dest, CancellationToken ct)
    {
        var token = await _getAccessToken(ct).ConfigureAwait(false);
        string assetUrl;
        try
        {
            assetUrl = await _api.GetAssetDownloadUrlAsync(
                src.Repo, src.Tag, src.Asset, token, ct).ConfigureAwait(false);
        }
        catch (GitHubAssetNotFoundException) { throw; }
        catch (GitHubApiException ex)
        {
            throw new FirmwareCacheException(
                $"could not list assets for {src.Repo}@{src.Tag}: {ex.Message}", ex);
        }

        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = dest + ".tmp";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                await _api.DownloadAssetAsync(assetUrl, token, fs, ct).ConfigureAwait(false);

            var actual = FirmwareIntegrity.ComputeSha256Hex(tmp);
            if (!FirmwareIntegrity.HashesMatch(actual, expectedSha256))
            {
                File.Delete(tmp);
                throw new FirmwareCacheException(
                    $"{src.Repo}@{src.Tag} asset '{src.Asset}' downloaded but " +
                    $"sha256={actual} did not match catalog {expectedSha256.ToLowerInvariant()}");
            }

            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }
}
