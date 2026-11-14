using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoDrive.Graph.Json;

namespace NoDrive.Graph.Auth;

/// <summary>Where the refresh token lives. Production: DPAPI-encrypted file; tests: in memory. Must persist atomically (refresh tokens ROTATE on every use).</summary>
public interface ITokenStore
{
    ValueTask<string?> LoadRefreshTokenAsync(CancellationToken ct);
    ValueTask SaveRefreshTokenAsync(string refreshToken, CancellationToken ct);
    ValueTask ClearAsync(CancellationToken ct);
}

public sealed class SignInRequiredException(string message) : Exception(message);

/// <summary>
/// OAuth 2.0 authorization code + PKCE for a public client against the `consumers` authority (ADR-0003): system browser,
/// loopback redirect, refresh-token grant with rotation persisted before the new access token is used.
/// </summary>
public sealed class PkceAuthenticator(HttpClient http, ITokenStore store, Action<Uri> openBrowser, string clientId = GraphEndpoints.DefaultClientId, string authority = GraphEndpoints.Authority, TimeProvider? clock = null, Action<string>? log = null, TimeSpan? interactiveTimeout = null) : IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expires;
    private static readonly string Scope = string.Join(' ', GraphEndpoints.Scopes);

    /// <summary>A valid access token: cached, refreshed with the stored refresh token, or (only when interactive is allowed) a new sign-in.</summary>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken ct, bool allowInteractive = false)
    {
        if (_accessToken is not null && _clock.GetUtcNow() < _expires - TimeSpan.FromMinutes(2)) { return _accessToken; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && _clock.GetUtcNow() < _expires - TimeSpan.FromMinutes(2)) { return _accessToken; }

            string? refresh = await store.LoadRefreshTokenAsync(ct).ConfigureAwait(false);
            if (refresh is not null)
            {
                var r = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["client_id"] = clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = Scope,
                }, ct).ConfigureAwait(false);
                if (r.AccessToken is not null) { return await AcceptAsync(r, ct).ConfigureAwait(false); }
                if (r.Error is not "invalid_grant") { throw new HttpRequestException($"Token refresh failed: {r.Error} {r.ErrorDescription}"); }
                await store.ClearAsync(ct).ConfigureAwait(false);                       // revoked or expired (90 days idle): sign in again
            }
            if (!allowInteractive) { throw new SignInRequiredException("Sign-in is required; run the engine with --sign-in."); }
            return await InteractiveAsync(ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public void Dispose() => _lock.Dispose();

    public ValueTask SignOutAsync(CancellationToken ct)
    {
        _accessToken = null;
        return store.ClearAsync(ct);
    }

    private async ValueTask<string> AcceptAsync(TokenResponse r, CancellationToken ct)
    {
        // Persist the rotated refresh token FIRST: if we crash after this line the next start still works.
        if (r.RefreshToken is { Length: > 0 } rt) { await store.SaveRefreshTokenAsync(rt, ct).ConfigureAwait(false); }
        _accessToken = r.AccessToken!;
        _expires = _clock.GetUtcNow() + TimeSpan.FromSeconds(r.ExpiresIn > 0 ? r.ExpiresIn : 3600);
        return _accessToken;
    }

    private async ValueTask<string> InteractiveAsync(CancellationToken ct)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        int port = FreePort();
        string redirect = $"http://localhost:{port}";
        listener.Prefixes.Add(redirect + "/");
        listener.Start();
        log?.Invoke($"sign-in: listening for the browser redirect on {redirect}/");

        var authorize = new Uri($"{authority}/authorize?client_id={Uri.EscapeDataString(clientId)}&response_type=code&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&response_mode=query&scope={Uri.EscapeDataString(Scope)}&code_challenge={challenge}&code_challenge_method=S256&state={state}&prompt=select_account");
        log?.Invoke("sign-in: opening the browser (pick your personal Microsoft account and approve Files.ReadWrite)");
        openBrowser(authorize);

        // Waiting for the user is a normal state: a slow user must not leave the engine half-dead, so a timeout is reported as "sign-in required".
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(interactiveTimeout ?? TimeSpan.FromMinutes(10));
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log?.Invoke("sign-in: timed out waiting for the browser");
            throw new SignInRequiredException("Sign-in timed out; choose 'Sign in...' from the NoDrive tray menu to try again.");
        }
        var q = ctx.Request.QueryString;
        log?.Invoke($"sign-in: redirect received: {ctx.Request.Url!.AbsolutePath}, parameters [{string.Join(",", q.AllKeys.Where(k => k is not null))}]");
        bool ok = q["error"] is null && q["state"] == state && q["code"] is not null;
        byte[] page = Encoding.UTF8.GetBytes(ok
            ? "<!doctype html><meta charset=utf-8><body style='font-family:sans-serif'><h2>NoDrive: signed in. You can close this window.</h2></body>"
            : $"<!doctype html><meta charset=utf-8><body style='font-family:sans-serif'><h2>NoDrive: sign-in failed</h2><p>{WebUtility.HtmlEncode(q["error"])}: {WebUtility.HtmlEncode(q["error_description"])}</p></body>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = page.Length;
        await ctx.Response.OutputStream.WriteAsync(page, ct).ConfigureAwait(false);
        ctx.Response.Close();
        listener.Stop();
        if (!ok) { throw new SignInRequiredException($"Sign-in failed: {q["error"]} {q["error_description"]}"); }

        var r = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["grant_type"] = "authorization_code", ["code"] = q["code"]!, ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier, ["scope"] = Scope,
        }, ct).ConfigureAwait(false);
        if (r.AccessToken is null) { throw new SignInRequiredException($"Token exchange failed: {r.Error} {r.ErrorDescription}"); }
        log?.Invoke("sign-in: token exchange succeeded");
        return await AcceptAsync(r, ct).ConfigureAwait(false);
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await http.PostAsync($"{authority}/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, GraphJsonContext.Default.TokenResponse, ct).ConfigureAwait(false) ?? new TokenResponse { Error = "empty_response" };
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
