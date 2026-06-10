using SIDM.Core.Http;

namespace SIDM.Core.Tests.Http;

public class FileNameResolverTests
{
    [Fact]
    public void Recovers_name_from_S3_response_content_disposition_param()
    {
        // Clean Amazon-S3-style override: the path is an opaque hash, the real
        // name rides in response-content-disposition.
        var url = "https://cdn.example.com/xet/e7bb6a02fd00323697c6a87a5e5f7554"
            + "?X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + "&response-content-disposition=attachment%3B%20filename%3D%22007.First.Light.Crack.Only-voices38.7z%22";

        FileNameResolver.FromUrlQuery(url).Should().Be("007.First.Light.Crack.Only-voices38.7z");
    }

    [Fact]
    public void Recovers_name_from_RFC5987_filename_star()
    {
        var url = "https://cdn.example.com/abc123"
            + "?response-content-disposition=attachment%3B%20filename%2A%3DUTF-8%27%27my%2520file.zip";

        // filename*=UTF-8''my%20file.zip → "my file.zip"
        FileNameResolver.FromUrlQuery(url).Should().Be("my file.zip");
    }

    [Fact]
    public void Recovers_name_from_huggingface_xet_url_shape()
    {
        // The reported bug: HF Xet presigned URL whose path is a content hash
        // and whose query carries both filename* and filename forms.
        var url = "https://cas-bridge.xethub.hf.co/xet-bridge-us/69a41bfe/"
            + "e7bb6a02fd00323697c6a87a5e5f7554e2fadde7b440dc0f42f88268bf5ee266"
            + "?Expires=1781113452"
            + "&response-content-disposition=attachment%3B+filename%2A%3DUTF-8%27%27007.First.Light.Crack.Only-voices38.7z%3B+filename%3D%22007.First.Light.Crack.Only-voices38.7z%22%3B"
            + "&response-content-type=application%2Fx-7z-compressed";

        FileNameResolver.FromUrlQuery(url).Should().Be("007.First.Light.Crack.Only-voices38.7z");
    }

    [Fact]
    public void Returns_null_when_query_has_no_filename()
    {
        var url = "https://cdn.example.com/e7bb6a02fd00?X-Amz-Algorithm=AWS4-HMAC-SHA256&response-content-type=application%2Fx-7z-compressed";
        FileNameResolver.FromUrlQuery(url).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_no_query()
    {
        FileNameResolver.FromUrlQuery("https://cdn.example.com/e7bb6a02fd00").Should().BeNull();
    }

    [Fact]
    public void Ignores_a_bare_extensionless_filename_token()
    {
        // A filename token with no extension is more likely a stray match than a
        // real download name — don't trust it.
        var url = "https://cdn.example.com/abc?filename=justaname";
        FileNameResolver.FromUrlQuery(url).Should().BeNull();
    }

    [Fact]
    public void Strips_path_components_from_embedded_name()
    {
        var url = "https://cdn.example.com/abc?response-content-disposition=attachment%3B%20filename%3D%22..%2F..%2Fevil.7z%22";
        FileNameResolver.FromUrlQuery(url).Should().Be("evil.7z");
    }

    [Fact]
    public void Handles_invalid_url_gracefully()
    {
        FileNameResolver.FromUrlQuery("not a url").Should().BeNull();
        FileNameResolver.FromUrlQuery("").Should().BeNull();
        FileNameResolver.FromUrlQuery((string?)null).Should().BeNull();
    }
}
