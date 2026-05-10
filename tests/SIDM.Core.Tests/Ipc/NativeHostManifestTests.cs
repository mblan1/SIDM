using System.Text.Json;
using SIDM.Core.Ipc;

namespace SIDM.Core.Tests.Ipc;

public class NativeHostManifestTests
{
    [Fact]
    public void BuildChromium_emits_required_NMH_fields()
    {
        var bytes = NativeHostManifest.BuildChromium(
            browserHostPath: @"C:\path\to\SIDM.BrowserHost.exe",
            allowedOrigin: "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/");

        var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;

        root.GetProperty("name").GetString().Should().Be("com.sidm.host");
        root.GetProperty("type").GetString().Should().Be("stdio");
        root.GetProperty("path").GetString().Should().Be(@"C:\path\to\SIDM.BrowserHost.exe");
        root.GetProperty("description").GetString().Should().NotBeNullOrEmpty();

        var origins = root.GetProperty("allowed_origins").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        origins.Should().ContainSingle()
            .Which.Should().Be("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/");
    }

    [Fact]
    public void BuildFirefox_uses_allowed_extensions_not_allowed_origins()
    {
        var bytes = NativeHostManifest.BuildFirefox(
            browserHostPath: @"C:\path\to\SIDM.BrowserHost.exe",
            allowedExtension: "sidm@snw.dev");

        var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;

        root.TryGetProperty("allowed_origins", out _).Should().BeFalse(
            "Firefox NMH manifests use allowed_extensions, not allowed_origins");
        root.GetProperty("allowed_extensions").EnumerateArray()
            .Single().GetString().Should().Be("sidm@snw.dev");

        root.GetProperty("type").GetString().Should().Be("stdio");
        root.GetProperty("name").GetString().Should().Be("com.sidm.host");
    }

    [Fact]
    public void Manifest_is_pretty_printed_so_humans_can_diff_it()
    {
        var bytes = NativeHostManifest.BuildChromium(@"C:\x.exe", "chrome-extension://a/");
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().Contain("\n", "manifest should be multi-line for readability");
        text.Should().Contain("  ", "manifest should be indented");
    }

    [Fact]
    public void HostId_constant_matches_published_name()
    {
        // The same string ends up in the registry and in the manifest's "name" field —
        // any drift breaks NMH discovery, so pin it.
        NativeHostManifest.HostId.Should().Be("com.sidm.host");
    }
}
