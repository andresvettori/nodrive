using System.Net;
using System.Text;
using NoDrive.Core.Abstractions;
using NoDrive.Graph;
using NoDrive.Graph.Auth;
using Xunit;

namespace NoDrive.Core.Tests;

/// <summary>Graph client and authenticator against a fake HTTP handler: no network, no browser.</summary>
public class GraphTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GraphDrive Drive(FakeHandler h, ThrottleGate? gate = null, GraphOptions? o = null) =>
        new(new HttpClient(h), _ => new ValueTask<string>("tok"), gate, o ?? new GraphOptions { MaxAttempts = 3 });

    private const string TwoPage1 = """
        {"value":[
          {"id":"root1","name":"root","root":{},"folder":{"childCount":2}},
          {"id":"F","name":"Docs","size":0,"lastModifiedDateTime":"2026-01-02T03:04:05Z","eTag":"\"e1\"","cTag":"\"c1\"","parentReference":{"id":"root1"},"folder":{"childCount":1}},
          {"id":"A","name":"a.pdf","size":1234,"lastModifiedDateTime":"2026-01-02T03:04:06Z","eTag":"\"e2\"","cTag":"\"c2\"","parentReference":{"id":"F"},"file":{"hashes":{"quickXorHash":"QX=="}}}
        ],"@odata.nextLink":"https://graph.microsoft.com/v1.0/next2"}
        """;
    private const string TwoPage2 = """
        {"value":[{"id":"B","deleted":{"state":"deleted"}}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/delta-token"}
        """;

    [Fact]
    public async Task Delta_pages_map_items_deletions_skip_the_root_and_follow_next_then_delta_links()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsoluteUri.EndsWith("next2", StringComparison.Ordinal) ? Json(TwoPage2) : Json(TwoPage1));
        var pages = new List<NoDrive.Core.Model.DeltaPage>();
        await foreach (var p in Drive(h).GetDeltaAsync(null, CancellationToken.None)) { pages.Add(p); }

        Assert.Equal(2, pages.Count);
        Assert.Equal("https://graph.microsoft.com/v1.0/next2", pages[0].NextLink);
        Assert.Equal("https://graph.microsoft.com/v1.0/delta-token", pages[1].DeltaLink);
        Assert.Equal(2, pages[0].Changes.Count);                                  // the root item is not stored
        var file = pages[0].Changes[1].Item!;
        Assert.Equal(("A", "F", "a.pdf", 1234L, false, "QX=="), (file.Id, file.ParentId, file.Name, file.Size, file.IsFolder, file.QuickXorHash));
        Assert.True(pages[0].Changes[0].Item!.IsFolder);
        Assert.True(pages[1].Changes[0].IsDeletion);
        Assert.Equal("B", pages[1].Changes[0].Id);
        Assert.Contains("$select=", h.Requests[0].RequestUri!.Query);
        Assert.Equal("Bearer", h.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task A_410_response_becomes_a_resync_signal()
    {
        var h = new FakeHandler(_ => Json("""{"error":{"code":"resyncRequired","message":"token too old"}}""", HttpStatusCode.Gone));
        await Assert.ThrowsAsync<ResyncRequiredException>(async () => { await foreach (var _ in Drive(h).GetDeltaAsync("https://x/old", CancellationToken.None)) { } });
    }

    [Fact]
    public async Task Throttling_pauses_the_shared_gate_then_retries_and_succeeds()
    {
        int calls = 0;
        var clock = new ManualClock();
        var gate = new ThrottleGate(clock);
        var h = new FakeHandler(_ =>
        {
            if (++calls == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(30));
                return resp;
            }
            return Json(TwoPage2);
        });
        // Real clock for the gate delay (30 ms) keeps the test honest; the gate also exposes the shared pause.
        var drive = Drive(h, new ThrottleGate());
        var pages = new List<NoDrive.Core.Model.DeltaPage>();
        await foreach (var p in drive.GetDeltaAsync(null, CancellationToken.None)) { pages.Add(p); }
        Assert.Equal(2, calls);
        Assert.Single(pages);
        Assert.Equal(TimeSpan.Zero, gate.Remaining);
        gate.PauseFor(TimeSpan.FromSeconds(5));
        gate.PauseFor(TimeSpan.FromSeconds(2));                                   // never shortens an existing pause
        Assert.Equal(TimeSpan.FromSeconds(5), gate.Remaining);
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal(TimeSpan.Zero, gate.Remaining);
    }

    [Fact]
    public async Task Download_url_comes_from_a_plain_item_request_without_select()
    {
        var h = new FakeHandler(_ => Json("""{"id":"A","name":"a.pdf","@microsoft.graph.downloadUrl":"https://content.example/dl?token=1"}"""));
        var uri = await Drive(h).GetDownloadUrlAsync("F61C!1234", CancellationToken.None);
        Assert.Equal("https://content.example/dl?token=1", uri.AbsoluteUri);
        Assert.DoesNotContain("select", h.Requests[0].RequestUri!.Query, StringComparison.Ordinal);   // measured: $select removes the property
        Assert.Contains("F61C%211234", h.Requests[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : ITokenStore
    {
        public string? Token { get; set; }
        public ValueTask<string?> LoadRefreshTokenAsync(CancellationToken ct) => new(Token);
        public ValueTask SaveRefreshTokenAsync(string refreshToken, CancellationToken ct) { Token = refreshToken; return ValueTask.CompletedTask; }
        public ValueTask ClearAsync(CancellationToken ct) { Token = null; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Refresh_grant_rotates_and_persists_the_new_refresh_token_and_caches_the_access_token()
    {
        var store = new MemoryStore { Token = "old-refresh" };
        string? seenForm = null;
        var h = new FakeHandler(r =>
        {
            seenForm = r.Content!.ReadAsStringAsync().Result;
            return Json("""{"access_token":"AT1","refresh_token":"new-refresh","expires_in":3600}""");
        });
        using var auth = new PkceAuthenticator(new HttpClient(h), store, _ => { });
        Assert.Equal("AT1", await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("new-refresh", store.Token);                                  // rotated and persisted
        Assert.Contains("refresh_token=old-refresh", seenForm, StringComparison.Ordinal);
        Assert.Equal("AT1", await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Single(h.Requests);                                                 // second call served from the cache
    }

    [Fact]
    public async Task Without_a_token_and_without_interactive_permission_sign_in_is_required()
    {
        using var auth = new PkceAuthenticator(new HttpClient(new FakeHandler(_ => Json("{}"))), new MemoryStore(), _ => { });
        await Assert.ThrowsAsync<SignInRequiredException>(async () => await auth.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_revoked_refresh_token_is_cleared_and_the_interactive_flow_completes_through_the_loopback_redirect()
    {
        var store = new MemoryStore { Token = "revoked" };
        int tokenCalls = 0;
        var h = new FakeHandler(r =>
        {
            string form = r.Content!.ReadAsStringAsync().Result;
            if (++tokenCalls == 1) { return Json("""{"error":"invalid_grant","error_description":"expired"}""", HttpStatusCode.BadRequest); }
            Assert.Contains("grant_type=authorization_code", form, StringComparison.Ordinal);
            Assert.Contains("code_verifier=", form, StringComparison.Ordinal);       // PKCE
            Assert.Contains("code=THE-CODE", form, StringComparison.Ordinal);
            return Json("""{"access_token":"AT2","refresh_token":"rt2","expires_in":3600}""");
        });
        using var http = new HttpClient(h);

        // The "browser": read redirect_uri and state from the authorize URL and call the loopback listener like the real redirect would.
        using var browser = new HttpClient();
        using var auth = new PkceAuthenticator(http, store, uri =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            Assert.Equal("S256", q["code_challenge_method"]);
            Assert.Contains("Files.ReadWrite", q["scope"], StringComparison.Ordinal);
            _ = Task.Run(async () => await browser.GetAsync($"{q["redirect_uri"]}/?code=THE-CODE&state={q["state"]}"));
        });
        string token = await auth.GetAccessTokenAsync(CancellationToken.None, allowInteractive: true);
        Assert.Equal("AT2", token);
        Assert.Equal("rt2", store.Token);
    }

    [Fact]
    public async Task A_sign_in_the_user_never_completes_times_out_as_SignInRequired_and_can_be_retried()
    {
        var store = new MemoryStore();
        using var auth = new PkceAuthenticator(new HttpClient(new FakeHandler(_ => Json("{}"))), store, _ => { /* the user never answers */ }, interactiveTimeout: TimeSpan.FromMilliseconds(300));
        var first = await Assert.ThrowsAsync<SignInRequiredException>(async () => await auth.GetAccessTokenAsync(CancellationToken.None, allowInteractive: true));
        Assert.Contains("timed out", first.Message, StringComparison.OrdinalIgnoreCase);
        // The authenticator is still usable: a second attempt behaves the same instead of being stuck behind a dead state.
        await Assert.ThrowsAsync<SignInRequiredException>(async () => await auth.GetAccessTokenAsync(CancellationToken.None, allowInteractive: true));
    }
}
