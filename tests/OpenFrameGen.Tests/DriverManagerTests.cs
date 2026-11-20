using System.Text.Json;
using OpenFrameGen.Core.Services;
using Xunit;

namespace OpenFrameGen.Tests;

public sealed class DriverManagerTests
{
    [Fact]
    public void ParseReleaseJson_FindsWin64DriverAsset()
    {
        const string json = """
        {
          "tag_name": "v0.1.0",
          "assets": [
            {
              "name": "OpenFrameGen.exe",
              "browser_download_url": "https://example.com/OpenFrameGen.exe",
              "size": 12345
            },
            {
              "name": "open-framegen-driver-win64.zip",
              "browser_download_url": "https://example.com/open-framegen-driver-win64.zip",
              "size": 54321
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var info = DriverManager.ParseReleaseJson(doc.RootElement);

        Assert.NotNull(info);
        Assert.Equal("v0.1.0", info!.TagName);
        Assert.Equal("open-framegen-driver-win64.zip", info.AssetName);
        Assert.Equal("https://example.com/open-framegen-driver-win64.zip", info.DownloadUrl);
        Assert.Equal(54321, info.SizeBytes);
    }

    [Fact]
    public void GetStatus_ReturnsNotInstalled_ForEmptyDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"frgen-test-{Guid.NewGuid():N}");
        var mgr = new DriverManager(installDirectory: tempDir);
        var status = mgr.GetStatus();

        Assert.False(status.IsInstalled);
        Assert.Equal(tempDir, status.InstallDirectory);
    }
}
