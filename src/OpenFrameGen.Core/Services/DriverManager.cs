using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenFrameGen.Core.Services;

public sealed record DriverReleaseInfo(
    string TagName,
    string AssetName,
    string DownloadUrl,
    long SizeBytes);

public sealed record DriverStatus(
    bool IsInstalled,
    bool IsVulkanLayerRegistered,
    string? InstalledVersion,
    string InstallDirectory);

public sealed class DriverManager
{
    public const string DefaultRepoOwner = "openxit";
    public const string DefaultRepoName = "FrGen";
    public const string Win64AssetName = "frgen-driver-win64.zip";
    public const string VulkanLayerManifestName = "FrGen_layer.json";
    public const string VulkanImplicitLayersKey = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";

    private readonly HttpClient _http;
    private readonly string _installDir;
    private readonly string _repoOwner;
    private readonly string _repoName;

    public DriverManager(
        HttpClient? httpClient = null,
        string? installDirectory = null,
        string repoOwner = DefaultRepoOwner,
        string repoName = DefaultRepoName)
    {
        _http = httpClient ?? CreateDefaultHttpClient();
        _installDir = installDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrGen",
            "driver");
        _repoOwner = repoOwner;
        _repoName = repoName;
    }

    public string InstallDirectory => _installDir;
    public string ManifestPath => Path.Combine(_installDir, VulkanLayerManifestName);
    public string VersionFilePath => Path.Combine(_installDir, "driver-version.txt");

    public DriverStatus GetStatus()
    {
        bool manifestExists = File.Exists(ManifestPath);
        string? version = File.Exists(VersionFilePath)
            ? File.ReadAllText(VersionFilePath).Trim()
            : null;
        bool registered = IsVulkanLayerRegistered();

        return new DriverStatus(
            IsInstalled: manifestExists && !string.IsNullOrWhiteSpace(version),
            IsVulkanLayerRegistered: registered,
            InstalledVersion: version,
            InstallDirectory: _installDir);
    }

    public async Task<DriverReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        string url = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases/latest";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseReleaseJson(doc.RootElement);
    }

    public static DriverReleaseInfo? ParseReleaseJson(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tagProp))
            return null;

        string tagName = tagProp.GetString() ?? "unknown";
        if (!root.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assetsProp.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (!string.Equals(name, Win64AssetName, StringComparison.OrdinalIgnoreCase))
                continue;

            string? downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp)
                ? urlProp.GetString()
                : null;
            long size = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;

            if (!string.IsNullOrWhiteSpace(downloadUrl))
                return new DriverReleaseInfo(tagName, name!, downloadUrl, size);
        }

        return null;
    }

    public async Task<DriverStatus> InstallOrUpdateAsync(
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var release = await GetLatestReleaseAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No matching driver package ({Win64AssetName}) found in the latest GitHub Release of {_repoOwner}/{_repoName}.");

        Directory.CreateDirectory(_installDir);
        string tempZip = Path.Combine(Path.GetTempPath(), $"openframegen-driver-{Guid.NewGuid():N}.zip");

        try
        {
            using (var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long totalBytes = response.Content.Headers.ContentLength ?? release.SizeBytes;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = File.Create(tempZip);

                var buffer = new byte[65536];
                long readTotal = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    if (totalBytes > 0)
                        progress?.Report(Math.Clamp((double)readTotal / totalBytes, 0.0, 1.0));
                }
            }

            ZipFile.ExtractToDirectory(tempZip, _installDir, overwriteFiles: true);
            await File.WriteAllTextAsync(VersionFilePath, release.TagName, ct).ConfigureAwait(false);
            EnsureManifestLibraryPath();
            RegisterVulkanLayer();
            progress?.Report(1.0);

            return GetStatus();
        }
        finally
        {
            if (File.Exists(tempZip))
                File.Delete(tempZip);
        }
    }

    public void Uninstall()
    {
        UnregisterVulkanLayer();
        if (Directory.Exists(_installDir))
            Directory.Delete(_installDir, recursive: true);
    }

    public void RegisterVulkanLayer()
    {
        if (!File.Exists(ManifestPath))
            return;

        using var key = Registry.CurrentUser.CreateSubKey(VulkanImplicitLayersKey);
        key?.SetValue(ManifestPath, 0, RegistryValueKind.DWord);
    }

    public void UnregisterVulkanLayer()
    {
        using var key = Registry.CurrentUser.OpenSubKey(VulkanImplicitLayersKey, writable: true);
        key?.DeleteValue(ManifestPath, throwOnMissingValue: false);
    }

    public bool IsVulkanLayerRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(VulkanImplicitLayersKey, writable: false);
        return key?.GetValue(ManifestPath) is not null;
    }

    private void EnsureManifestLibraryPath()
    {
        string dllPath = Path.Combine(_installDir, "VK_LAYER_FRGEN.dll");
        if (!File.Exists(ManifestPath) || !File.Exists(dllPath))
            return;

        string json = File.ReadAllText(ManifestPath);
        string normalizedDll = dllPath.Replace("\\", "\\\\");
        json = json.Replace("./VK_LAYER_FRGEN.dll", normalizedDll)
                   .Replace(".\\VK_LAYER_FRGEN.dll", normalizedDll);
        File.WriteAllText(ManifestPath, json);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FrGen", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
