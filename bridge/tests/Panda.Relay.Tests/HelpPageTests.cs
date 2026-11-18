using System.Net;

namespace Panda.Relay.Tests;

/// <summary>
/// plan-https-code T2 (AC4, seam S3): GET / HEAD /c/&lt;relayId&gt; — the static page a browser shows for the connection
/// code's link form. No script can run, no referrer leaves, nothing about the request is echoed.
/// </summary>
public class HelpPageTests
{
    private const string RelayId = "AAAAAAAAAAAAAAAAAAAAAA";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri At(RelayFixture relay, string path) => new($"http://127.0.0.1:{relay.Server.Port}{path}");

    [Theory]
    [InlineData("/c/" + RelayId)]
    [InlineData("/c/abc-DEF_0123456789abcd")]
    [InlineData("/c/" + RelayId + "?x=%3Cscript%3E")] // the query is ignored, never echoed
    public async Task A_well_formed_id_gets_the_static_page_with_its_headers(string path)
    {
        await using var relay = await RelayFixture.StartAsync();
        using var http = new HttpClient();
        using var response = await http.GetAsync(At(relay, path), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal(
            "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("panda-browser connect", body, StringComparison.Ordinal);
        Assert.Contains("0.1.15", body, StringComparison.Ordinal);
        Assert.Contains("lang=\"vi\"", body, StringComparison.Ordinal);
        Assert.Contains("lang=\"en\"", body, StringComparison.Ordinal);
        foreach (var never in new[] { "<script", "javascript:", "location", "hash", "on" + "load", RelayId, "abc-DEF", "%3C", "<form", "<iframe", "http-equiv" })
        {
            Assert.DoesNotContain(never, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Every_id_gets_the_same_bytes()
    {
        await using var relay = await RelayFixture.StartAsync();
        using var http = new HttpClient();
        var a = await http.GetByteArrayAsync(At(relay, "/c/" + RelayId), Ct);
        var b = await http.GetByteArrayAsync(At(relay, "/c/BBBBBBBBBBBBBBBBBBBBBB"), Ct);
        Assert.Equal(a, b);
        Assert.True(a.Length > 200);
    }

    [Fact]
    public async Task HEAD_answers_200_with_the_headers_and_no_body()
    {
        await using var relay = await RelayFixture.StartAsync();
        using var http = new HttpClient();
        var get = await http.GetByteArrayAsync(At(relay, "/c/" + RelayId), Ct);
        using var request = new HttpRequestMessage(HttpMethod.Head, At(relay, "/c/" + RelayId));
        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // T2 diff critic #2: what HEAD can show — the GET's length and every AC4 header (the body itself HttpClient never reads)
        Assert.Equal(get.Length, response.Content.Headers.ContentLength);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        foreach (var header in new[] { "Content-Security-Policy", "Referrer-Policy", "X-Robots-Tag", "X-Content-Type-Options", "Cache-Control" })
        {
            Assert.True(response.Headers.Contains(header), header);
        }
    }

    [Theory]
    [InlineData("/c/AAAA")] // short
    [InlineData("/c/AAAAAAAAAAAAAAAAAAAAA=")] // not base64url
    [InlineData("/c/" + RelayId + "/")]
    [InlineData("/c/" + RelayId + "/x")]
    [InlineData("/C/" + RelayId)]
    [InlineData("/c/")]
    [InlineData("/c")]
    [InlineData("/" + RelayId)]
    public async Task Anything_else_is_404(string path)
    {
        await using var relay = await RelayFixture.StartAsync();
        using var http = new HttpClient();
        using var response = await http.GetAsync(At(relay, path), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task Another_method_never_gets_the_page()
    {
        await using var relay = await RelayFixture.StartAsync();
        using var http = new HttpClient();
        using var response = await http.PostAsync(At(relay, "/c/" + RelayId), new StringContent("x"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // the fallback takes any method (T2 diff critic #1)
        Assert.DoesNotContain("panda-browser", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_internal_listener_never_serves_it()
    {
        await using var relay = await RelayFixture.StartAsync(o => o.InternalPort = 0);
        using var http = new HttpClient();
        var port = relay.Server.InternalPort ?? throw new InvalidOperationException("no internal listener");
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/c/{RelayId}"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Nothing_about_the_request_is_logged()
    {
        await using var relay = await RelayFixture.StartAsync();
        using var http = new HttpClient();
        var before = relay.Logs.Lines.Count;
        using var response = await http.GetAsync(At(relay, "/c/" + RelayId + "?q=1"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(relay.Logs.Lines.Skip(before), l => l.Contains("/c/", StringComparison.Ordinal) || l.Contains(RelayId, StringComparison.Ordinal));
    }
}
