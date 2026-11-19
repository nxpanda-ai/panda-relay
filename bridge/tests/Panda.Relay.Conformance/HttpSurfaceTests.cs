using System.Net;
using System.Security.Cryptography;

namespace Panda.Relay.Conformance;

/// <summary>
/// G1 (spec §78.11, plan-https-code AC4): the only page is the static help page of the connection code's link form,
/// at <c>/c/&lt;relayId&gt;</c> with its exact security headers; every other path is 404; nothing about the request
/// reaches the relay's output.
/// </summary>
public class HttpSurfaceTests(ClassRelay fixture) : IClassFixture<ClassRelay>
{
    private const string Csp = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RelayProcess Relay
    {
        get
        {
            fixture.Relay.Secret("/c/"); // nothing about a page request reaches the output, not even its path (HelpPageTests)
            return fixture.Relay;
        }
    }

    /// <summary>A well-formed relayId used nowhere else: it must never appear in the relay's output (checked at stop).</summary>
    private string FreshId()
    {
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Relay.Secret(id);
        return id;
    }

    private static HttpClient Http() => new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task A_well_formed_id_gets_the_page_with_its_exact_security_headers_and_no_echo()
    {
        var id = FreshId();
        var marker = "q" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        Relay.Secret(marker);
        using var http = Http();
        using var response = await http.GetAsync(new Uri(Relay.Http, $"/c/{id}?x={marker}"), Ct);
        AssertPageHeaders(response);
        var body = await response.Content.ReadAsStringAsync(Ct);
        foreach (var never in new[] { id, marker, "<script", "javascript:", "<form", "<iframe", "http-equiv" })
        {
            Assert.DoesNotContain(never, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Every_id_gets_the_same_bytes()
    {
        using var http = Http();
        var a = await http.GetByteArrayAsync(new Uri(Relay.Http, "/c/" + FreshId()), Ct);
        var b = await http.GetByteArrayAsync(new Uri(Relay.Http, "/c/" + FreshId()), Ct);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task HEAD_answers_200_with_the_headers_and_the_GET_length_and_no_body()
    {
        using var http = Http();
        var get = await http.GetByteArrayAsync(new Uri(Relay.Http, "/c/" + FreshId()), Ct);
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(Relay.Http, "/c/" + FreshId()));
        using var response = await http.SendAsync(request, Ct);
        AssertPageHeaders(response);
        Assert.Equal(get.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>The page's exact headers (HelpPage.cs): status, type, CSP, referrer, robots, nosniff, cache.</summary>
    private static void AssertPageHeaders(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal(Csp, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("public, max-age=3600", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/c/AAAA")] // short
    [InlineData("/c/AAAAAAAAAAAAAAAAAAAAA=")] // not base64url
    [InlineData("/c/AAAAAAAAAAAAAAAAAAAAAA/")]
    [InlineData("/c/AAAAAAAAAAAAAAAAAAAAAA/x")]
    [InlineData("/C/AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/c/")]
    [InlineData("/c")]
    [InlineData("/")]
    [InlineData("/AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/v1/other")]
    [InlineData("/mcp")]
    public async Task Any_other_path_is_404_without_the_page_headers(string path)
    {
        using var http = Http();
        using var response = await http.GetAsync(new Uri(Relay.Http, path), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task Another_method_on_the_page_is_404()
    {
        using var http = Http();
        using var response = await http.PostAsync(new Uri(Relay.Http, "/c/" + FreshId()), new StringContent("x"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/browser")]
    [InlineData("/v1/stream?dial=AAAAAAAAAAAAAAAAAAAAAA&kind=link")]
    public async Task Plain_HTTP_on_a_socket_path_is_400(string path)
    {
        using var http = Http();
        using var response = await http.GetAsync(new Uri(Relay.Http, path), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
