using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NoDrive.Core.Abstractions;
using NoDrive.Graph;
using Xunit;

namespace NoDrive.Core.Tests;

/// <summary>Large-file uploads against a fake Graph: sessions are continued, not restarted, after cuts, 409 fragments and restarts.</summary>
public sealed class UploadSessionTests : IDisposable
{
    private const string Url = "https://upload.example/session1";
    private readonly string _file = Path.Combine(Path.GetTempPath(), "nd-upl-" + Guid.NewGuid().ToString("N") + ".bin");
    private const long Size = 12 * 1024 * 1024;            // two fragments (10 + 2 MiB)

    public UploadSessionTests() { var b = new byte[Size]; new Random(7).NextBytes(b); File.WriteAllBytes(_file, b); }
    public void Dispose() { try { File.Delete(_file); } catch (IOException) { } }

    private sealed class Store : IUploadSessionStore
    {
        public readonly Dictionary<string, SavedUploadSession> Items = [];
        public SavedUploadSession? Find(string key) => Items.TryGetValue(key, out var s) ? s : null;
        public void Remember(string key, SavedUploadSession session) => Items[key] = session;
        public void Forget(string key) => Items.Remove(key);
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, ContentRangeHeaderValue? Range)> Log { get; } = [];
        public List<string> SessionBodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Log.Add((r.Method, r.RequestUri!.AbsoluteUri, r.Content?.Headers.ContentRange));
            if (r.Method == HttpMethod.Post && r.Content is not null) { SessionBodies.Add(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); }
            return respond(r);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Done() => Json("{\"id\":\"I1\",\"name\":\"big.bin\",\"size\":12582912,\"parentReference\":{\"id\":\"P\"},\"file\":{}}", HttpStatusCode.Created);
    private static HttpResponseMessage Accepted(string next) => Json("{\"nextExpectedRanges\":[\"" + next + "\"]}", HttpStatusCode.Accepted);

    private static GraphDrive Drive(Fake h, Store store) => new(new HttpClient(h), _ => new ValueTask<string>("tok"), null, new GraphOptions { MaxAttempts = 2 }) { SessionStore = store };

    [Fact]
    public async Task A_saved_session_is_continued_from_where_the_server_says_without_creating_another()
    {
        var store = new Store();
        store.Items["new:P/big.bin"] = new SavedUploadSession(Url, Size, File.GetLastWriteTimeUtc(_file).Ticks);
        var h = new Fake(r => r.Method == HttpMethod.Get ? Json("{\"nextExpectedRanges\":[\"5242880-\"]}")
            : r.Content!.Headers.ContentRange!.To == Size - 1 ? Done() : Accepted("10485760-"));
        var item = await Drive(h, store).UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken);

        Assert.Equal("I1", item.Id);
        Assert.DoesNotContain(h.Log, x => x.Method == HttpMethod.Post);                         // no new session
        Assert.Equal(5242880, h.Log.First(x => x.Method == HttpMethod.Put).Range!.From);      // started at the server's offset, not at 0
        Assert.Empty(store.Items);                                                              // finished: nothing left to resume
    }

    [Fact]
    public async Task A_new_session_is_saved_before_any_byte_is_sent_and_kept_when_the_upload_fails()
    {
        var store = new Store();
        var h = new Fake(r => r.Method == HttpMethod.Post ? Json("{\"uploadUrl\":\"" + Url + "\"}") : throw new HttpRequestException("network down"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Drive(h, store).UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken));

        Assert.True(store.Items.ContainsKey("new:P/big.bin"));                                 // the retry will continue it
        Assert.DoesNotContain(h.Log, x => x.Method == HttpMethod.Delete);                      // and it was NOT cancelled
    }

    [Fact]
    public async Task A_409_on_a_fragment_resumes_from_the_session_instead_of_being_a_name_clash()
    {
        var store = new Store();
        int puts = 0;
        var h = new Fake(r =>
        {
            if (r.Method == HttpMethod.Post) { return Json("{\"uploadUrl\":\"" + Url + "\"}"); }
            if (r.Method == HttpMethod.Get) { return Json("{\"nextExpectedRanges\":[\"5242880-\"]}"); }
            puts++;
            if (puts == 2) { return new HttpResponseMessage(HttpStatusCode.Conflict); }          // the server already had that fragment
            return r.Content!.Headers.ContentRange!.To == Size - 1 ? Done() : Accepted("5242880-");
        });
        var item = await Drive(h, store).UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken);
        Assert.Equal("I1", item.Id);
        Assert.Contains(h.Log, x => x.Method == HttpMethod.Put && x.Range!.From == 5242880);
    }

    [Fact]
    public async Task A_saved_session_that_no_longer_exists_is_replaced_by_a_new_one()
    {
        var store = new Store();
        store.Items["new:P/big.bin"] = new SavedUploadSession(Url, Size, File.GetLastWriteTimeUtc(_file).Ticks);
        var h = new Fake(r =>
        {
            if (r.Method == HttpMethod.Get) { return new HttpResponseMessage(HttpStatusCode.NotFound); }
            if (r.Method == HttpMethod.Post) { return Json("{\"uploadUrl\":\"https://upload.example/session2\"}"); }
            return r.Content!.Headers.ContentRange!.To == Size - 1 ? Done() : Accepted("x");
        });
        await Drive(h, store).UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken);
        Assert.Contains(h.Log, x => x.Method == HttpMethod.Post);
        Assert.Equal(0, h.Log.First(x => x.Method == HttpMethod.Put).Range!.From);
    }

    [Fact]
    public async Task A_saved_session_of_a_file_that_changed_is_cancelled_and_restarted()
    {
        var store = new Store();
        store.Items["new:P/big.bin"] = new SavedUploadSession(Url, Size - 1, 0);                  // size/time do not match the file
        var h = new Fake(r =>
        {
            if (r.Method == HttpMethod.Delete) { return new HttpResponseMessage(HttpStatusCode.NoContent); }
            if (r.Method == HttpMethod.Post) { return Json("{\"uploadUrl\":\"https://upload.example/session2\"}"); }
            return r.Content!.Headers.ContentRange!.To == Size - 1 ? Done() : Accepted("x");
        });
        await Drive(h, store).UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken);
        Assert.Contains(h.Log, x => x.Method == HttpMethod.Delete && x.Url == Url);
    }

    [Fact]
    public async Task The_session_is_created_with_our_modified_time_and_its_expiry_is_saved()
    {
        var store = new Store();
        var h = new Fake(r => r.Method == HttpMethod.Post ? Json("{\"uploadUrl\":\"" + Url + "\",\"expirationDateTime\":\"2030-01-01T00:00:00Z\"}")
            : r.Content!.Headers.ContentRange!.To == Size - 1 ? Done() : Accepted("10485760-"));
        string expected = File.GetLastWriteTimeUtc(_file).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var drive = Drive(h, store);
        await drive.UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken);
        Assert.Contains("\"lastModifiedDateTime\":\"" + expected + "\"", h.SessionBodies[0]);
        Assert.Contains("conflictBehavior\":\"fail\"", h.SessionBodies[0]);
    }

    [Fact]
    public async Task A_saved_session_past_its_expiry_is_not_asked_about_but_replaced()
    {
        var store = new Store();
        store.Items["new:P/big.bin"] = new SavedUploadSession(Url, Size, File.GetLastWriteTimeUtc(_file).Ticks, DateTime.UtcNow.AddMinutes(-5).Ticks);
        var h = new Fake(r => r.Method == HttpMethod.Post ? Json("{\"uploadUrl\":\"https://upload.example/session2\"}")
            : r.Content!.Headers.ContentRange!.To == Size - 1 ? Done() : Accepted("10485760-"));
        await Drive(h, store).UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(h.Log, x => x.Method == HttpMethod.Get);                          // no pointless status request
        Assert.Contains(h.Log, x => x.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_403_on_a_fragment_forgets_the_session_so_the_retry_creates_a_new_one()
    {
        var store = new Store();
        var h = new Fake(r => r.Method == HttpMethod.Post ? Json("{\"uploadUrl\":\"" + Url + "\"}") : new HttpResponseMessage(HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<HttpRequestException>(() => Drive(h, store).UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken));
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task A_timeout_on_a_fragment_asks_the_session_and_resends_only_what_the_server_lacks()
    {
        var store = new Store();
        int puts = 0;
        var h = new Fake(r =>
        {
            if (r.Method == HttpMethod.Post) { return Json("{\"uploadUrl\":\"" + Url + "\"}"); }
            if (r.Method == HttpMethod.Get) { return Json("{\"nextExpectedRanges\":[\"10485760-\"]}"); }       // the server DID get the first 10 MiB although the answer was lost
            if (++puts == 1) { throw new TaskCanceledException("net_http_request_timedout"); }
            return Done();
        });
        var item = await Drive(h, store).UploadNewFileAsync("P", "big.bin", _file, TestContext.Current.CancellationToken);
        Assert.Equal("I1", item.Id);
        Assert.Equal(2, h.Log.Count(x => x.Method == HttpMethod.Put));
        Assert.Equal(10485760, h.Log.Last(x => x.Method == HttpMethod.Put).Range!.From);        // the second request did not resend the first fragment
    }
}
