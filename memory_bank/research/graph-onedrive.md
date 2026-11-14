# Microsoft Graph / OneDrive personal: verified knowledge base

Research pass of 2026-11-13 (researcher + adversarial fact-checker, `graph` topic, overall reliability: high), plus the relevant gaps from the completeness critique. Facts below were checked against primary sources on that date unless marked **community-reported** or **to verify in spike**. Where the raw report and the fact-checker disagreed, the fact-checker's correction is what appears here.

Scope of NoDrive's Graph surface: ~10 endpoints, all under `/me/drive` or an `uploadUrl`/`downloadUrl`, personal (consumer) accounts only in v1, delegated permissions only (Graph for personal accounts has no application permissions; the createUploadSession table lists Application as "Not available" for personal). Source: https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0

---

## 1. App registration and auth

### 1.1 Prerequisite: you need an Entra tenant

- A personal Microsoft account signed into the Azure/Entra portal lands in the directory-less "Microsoft Services" tenant and **cannot create app registrations there**. Create a free Entra tenant first (free Azure sign-up, or be a guest in another tenant), then register the app in that tenant. Microsoft Q&A moderators, not official docs: **community-reported**. Source: https://learn.microsoft.com/en-us/answers/questions/5693812/app-registration-option-disabled-for-my-account-in
- The consent screen will show "unverified publisher"; fine for self-use.

### 1.2 Registration settings

- `signInAudience`: **AzureADandPersonalMicrosoftAccount** (keeps a future business option open; `PersonalMicrosoftAccount` also works for MSA-only). Platform: "Mobile and desktop applications". "Allow public client flows" = yes. Source: https://learn.microsoft.com/en-us/entra/identity-platform/supported-accounts-validation
- Authority: `https://login.microsoftonline.com/consumers` for MSA-only, `/common` for MSA + work accounts. Source: https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow
- Redirect URIs (source: https://learn.microsoft.com/en-us/entra/identity-platform/reply-url, updated 2026-06-15):
  - `http://localhost` for the system-browser loopback flow. The port component is ignored when matching a localhost redirect URI, so register it once without a port; "do not register multiple localhost redirect URIs where only the port differs".
  - `http://127.0.0.1` is preferred by Microsoft over `localhost` but the portal blocks http loopback; it must be added via `replyUrlsWithType` in the manifest.
  - IPv6 loopback `[::1]` is not supported.
  - `https://login.microsoftonline.com/common/oauth2/nativeclient` only for embedded browsers (not used).
  - `ms-appx-web://microsoft.aad.brokerplugin/{client_id}` only for WAM (deferred). Source: https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam
- Constraints when the audience includes MSA: max 100 redirect URIs, no query parameters or wildcards in redirect URIs, national clouds unsupported, max 30 permissions per resource API (200 total), max 2 client secrets (irrelevant for a public client). Sources: https://learn.microsoft.com/en-us/entra/identity-platform/reply-url , https://learn.microsoft.com/en-us/entra/identity-platform/supported-accounts-validation
- No 2025-2026 change to Entra app-registration policy for MSA-enabled apps was found beyond these long-standing limits.

### 1.3 Scopes

- **`Files.ReadWrite` + `offline_access`** (+ `openid`, optionally `User.Read` for the display name). Authorize-request scope string: `Files.ReadWrite offline_access openid`.
- Least-privileged per endpoint, "Delegated (personal Microsoft account)" column: delta, get content, socketIo, recent: `Files.Read`; put content, createUploadSession, permanentDelete: `Files.ReadWrite`. Sources: https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0 , https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0 , https://learn.microsoft.com/en-us/graph/api/subscriptions-socketio?view=graph-rest-1.0
- `Files.ReadWrite.AppFolder` is a personal-account-only sandbox (App Root folder), display string still "(preview)". Not what NoDrive needs. For personal accounts `Files.Read`/`Files.ReadWrite` also grant access to files shared with the signed-in user. Source: https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/permissions_reference (updated 2026-05-14)
- Full properties of shared items via `/sharedWithMe` need `Files.Read.All`, but that endpoint is being retired (section 8). Shared items reachable through the user's own drive surface in delta as `remoteItem` entries.
- Personal accounts consent via the user consent prompt; no admin consent.
- Do not use `*.All` permissions if business support is added later (community report of tenants requiring admin consent for `*.All`): **community-reported**. Source: https://community.n8n.io/t/ms-onedrive-now-requires-admin-consent-for-all-sites-and-files-graph-api-permissions/186707

### 1.4 Hand-rolled authorization-code + PKCE flow (chosen for v1)

Source for the whole flow: https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow (ms.date 2026-01-09).

- Authorize: `GET https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=...&response_type=code&redirect_uri=http://localhost:{port}&scope=Files.ReadWrite offline_access openid&state=...&code_challenge=...&code_challenge_method=S256` opened in the **system browser**; a loopback `HttpListener` on `http://localhost:{port}/` receives the code (bind to 127.0.0.1; rclone registers `http://localhost:53682/` and listens on 127.0.0.1:53682, source: https://rclone.org/onedrive/).
- Redeem: `POST /consumers/oauth2/v2.0/token` with `client_id`, `code`, `code_verifier`, `redirect_uri`, `grant_type=authorization_code`, **no `client_secret`** ("Public clients ... must not use secrets or certificates when redeeming an authorization code").
- Authorization codes expire after about 1 minute.
- Refresh: `grant_type=refresh_token`; a new refresh token is returned on every use; discard the old one ("Refresh tokens replace themselves with a fresh token upon every use"). Default lifetime 90 days for native apps (the auth-code page says native-app refresh tokens "don't have specified lifetimes"; treat 90 days as the documented default and handle revocation at any time). Source: https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens (ms.date 2025-11-05)
- Tokens for consumer (MSA) users may be encrypted/opaque and not JWTs: **never parse access or refresh tokens**.
- Validate `state` on the callback; use a per-run random port; set a CancellationToken timeout of a few minutes on the browser wait.
- Microsoft states it does "not recommend" manual protocol implementation over MSAL. Trade-off accepted: zero NuGet dependencies, provably Native-AOT clean (System.Net.Http + System.Text.Json source-gen + System.Security.Cryptography.ProtectedData), at the cost of WAM features (Windows account picker, Windows Hello/FIDO, device-bound refresh tokens, SSO with the signed-in Windows MSA).
- Known field issue: AADSTS70000 caused by browser extensions mangling the redirect URL (abraunegg known-issues): **community-reported**. Source: https://github.com/abraunegg/onedrive/blob/master/docs/known-issues.md

### 1.5 Token storage

- Store the refresh token (and cached access token) encrypted at rest with DPAPI (`System.Security.Cryptography.ProtectedData`, `DataProtectionScope.CurrentUser`), in `%LOCALAPPDATA%\NoDrive`. This is what MSAL's `Microsoft.Identity.Client.Extensions.Msal` (`MsalCacheHelper`) does on Windows. Source: https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization

### 1.6 MSAL / WAM status (deferred, documented for later)

- **Microsoft.Identity.Client 4.92.0** (2026-10-29), 3.28 MB nupkg; net8.0 dependencies only `Microsoft.IdentityModel.Abstractions >= 8.14.0` and `System.Diagnostics.DiagnosticSource`. Source: https://www.nuget.org/packages/Microsoft.Identity.Client
- MSAL core declares `<IsAotCompatible>true</IsAotCompatible>` for net8.0+ in `src/Directory.Build.props` (PR #5458, merged 2025-09-11, **first shipped in 4.77.1 on 2025-09-25**). Sources: https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/pull/5458 , https://raw.githubusercontent.com/AzureAD/microsoft-authentication-library-for-dotnet/main/src/Directory.Build.props
- WAM broker: **Microsoft.Identity.Client.Broker 4.92.0** ships netstandard2.0/net462 only (88 KB), depends on **Microsoft.Identity.Client.NativeInterop 0.20.7** (21.65 MB nupkg with the native msalruntime binaries; NativeInterop itself also has a net9.0 target). Because the Broker package has no modern TFM, the AOT analyzer never covers the broker path. Sources: https://www.nuget.org/packages/Microsoft.Identity.Client.Broker , https://www.nuget.org/packages/Microsoft.Identity.Client.NativeInterop
- NativeInterop IL3000 (`Assembly.Location`) fix: NativeInterop 0.19.4 via PR #5434, shipped in MSAL 4.76.0 (2025-08-15). Sources: https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/pull/5434 , https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/issues/5226
- Open issue **#6232** (opened October 2026, no maintainer response; the fact-checker transcribed the page date as 2025-10-23, which conflicts with the MSAL 4.91.1 / .NET 10.0.13 versions it reports, so re-check the date on the issue): under `PublishAot` on .NET 10 win-x64 (MSAL 4.91.1) the system-browser loopback `HttpListener` aborts the success page although token acquisition succeeds; the reporter also states **msalruntime.dll cannot be bundled into a single-file Native AOT exe**, so WAM + AOT means shipping native DLLs next to the exe. Source: https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/issues/6232
- WAM usage facts (source: https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam, updated 2026-11-02): MSAL 4.52.0+; `WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))`; `WithParentActivityOrWindow(hwnd)` is mandatory; Windows 10 1703+ / Server 2019+ with browser fallback; MSA supported, OS account picker appears when the authority includes personal accounts; must run in an interactive user session (not service/scheduled task/runas); WAM supports only Entra ID, no third-party IdPs; expect to reference the Broker package even on `net*-windows` TFMs (the doc contradicts itself on this).
- Verdict for NoDrive: "likely works, must be validated with a real PublishAot build"; not worth 21 MB + non-single-file deployment in v1.

### 1.7 The 403 `serviceReadOnly` report (test on day one)

- **Community-reported, no Microsoft response**: a Microsoft Q&A thread (posted 2026-08-23, comments through 2026-10-30) reports that **newly consented apps on personal accounts** requesting only `Files.ReadWrite.AppFolder + offline_access` receive `403 accessDenied` / innerError `serviceReadOnly` ("Database Is Read Only") or `503 itemDisabledDueToPendingProvisioning` on all drive endpoints, while apps consented months earlier work. Community workaround: a one-time consent with the broader `Files.ReadWrite` to trigger provisioning. Source: https://learn.microsoft.com/en-us/answers/questions/5983388/personal-onedrive-newly-consented-apps-get-403-acc
- NoDrive is exactly a brand-new registration against a personal account: P0 step 1 is a fresh app + fresh account + `GET /me/drive/root/delta` with `Files.ReadWrite`. **To verify in spike.**

---

## 2. Delta query semantics

Endpoint: `GET /me/drive/root/delta`. Primary source for everything in this section unless stated: https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0 (page updated 2026-06-06), plus https://learn.microsoft.com/en-us/graph/delta-query-overview

Rules (all confirmed verbatim by the fact-checker):

- **Delta is the only enumeration guaranteed complete.** Paging `/children` is explicitly not guaranteed complete if writes occur. Never build the index from children listings.
- Initial call without a token walks the whole hierarchy page by page via `@odata.nextLink`, ending with `@odata.deltaLink`. Persist the `nextLink` after each page so the initial walk is resumable; persist the `deltaLink` only when the walk completes.
- `delta?token=latest` returns an empty page with a fresh `deltaLink` (a cursor without enumerating). Useful after a resync decision, not for the first sync.
- The feed shows the **latest state of each item, not each change**. The same item id may appear more than once in one response or across pages: **use the last occurrence**.
- **Track items by `id` and `parentReference.id`**, never by path: `parentReference.path` is not populated, and renaming/moving a folder does **not** re-emit its descendants. A move/rename appears as the item re-appearing with a new `name` and/or new `parentReference.id`.
- Deleted items carry the **`deleted` facet**. Apply deletes first, then upsert everything else. Only remove a folder locally once it is empty after applying all changes in the page/batch.
- **Consumer omissions**: on OneDrive personal, deleted items omit `ctag` and `size`. (Business omits `ctag` on create/modify and `ctag`+`name` on delete.) Do not rely on those fields in delete handling.
- **Resync, HTTP 410 Gone**: the response carries a `Location` header with a fresh `nextLink` and one of two error codes:
  - `resyncChangesApplyDifferences`: server wins; replace local items with server versions and upload only what the server lacks.
  - `resyncChangesUploadDifferences`: upload local items the server did not return and files that differ; keep both when unsure.
  - Both paths must re-walk the whole drive against the SQLite index in streaming fashion (no RAM balloon; see plan).
- `$select`, `$expand`, `$top` are supported and are **baked into the returned tokens**: choose the `$select` once, on the first call. Recommended select: `id,name,size,eTag,cTag,file,folder,deleted,parentReference,fileSystemInfo,lastModifiedDateTime,root` (remoteItem/package if those facets are needed to exclude items).
- Header `Prefer: deltaExcludeParent` omits parent items from responses.
- Timestamp-in-place-of-token is **business/SharePoint only**; not available on personal.
- `@removed` annotation: described in the generic delta overview for directory-style resources; the driveItem page documents only the `deleted` facet. Not expected for driveItem; coding defensively costs nothing. **To verify in spike** against a personal drive, together with cross-folder moves and items moved into/out of shared folders (`remoteItem`).
- Page size: the research did not find a documented default or maximum for `$top` on consumer delta. The plan assumes roughly 200 items/page (about 1,000-2,500 pages for 200k-500k items). **To verify in spike** (P0 step 6): which `$top` values consumer delta honours and the real first-sync time.
- Cost: delta with token = 1 resource unit; delta without token = 2 (section 10).
- Delta is a cursor, not proof that local actions succeeded (abraunegg architecture note). Source: https://github.com/abraunegg/onedrive/blob/master/docs/client-architecture.md
- rclone notes that "delta listing is only supported from the drive root" applies to *some older OneDrive for Business/SharePoint drives*, not personal. Source: https://rclone.org/onedrive/

---

## 3. Change notifications

### 3.1 Socket.io channel (no public endpoint needed)

Source: https://learn.microsoft.com/en-us/graph/api/subscriptions-socketio?view=graph-rest-1.0 (ms.date 2024-04-04, updated 2025-07-23)

- `GET /me/drive/root/subscriptions/socketIo` (v1.0, delegated only, personal supported with `Files.Read`, Application "Not supported") returns `{ id, notificationUrl }`.
- `notificationUrl` is a **socket.io** endpoint (an `https://...svc.ms` URL, not a bare `wss://`). Connect with `transports: ['websocket']` only (polling transport unsupported) and listen for the `notification` event, then run delta. A real client must speak **Engine.IO v4** over the WebSocket, so a hand-rolled client is more than a `ClientWebSocket` connect; alternative is a socket.io NuGet (check AOT and size).
- Session lifetime is undocumented; expect to re-request the endpoint on disconnect.
- The Microsoft.Graph SDK has no dedicated socketIo request builder (its sample calls `Subscriptions[id].GetAsync()`), another reason for raw HttpClient.
- Reliability for personal accounts from .NET: a Q&A thread reporting WebSocket connect failures (questions/2073657) now returns 410 Gone, so that evidence is unverifiable. **To verify in spike / P2 prototype.**
- Notifications only signal "something may have changed"; they never replace delta reconciliation (abraunegg). Source: https://github.com/abraunegg/onedrive/blob/master/docs/client-architecture.md
- Decision: v1 uses adaptive delta polling (1 min active, backing off to 15-60 min idle, daily safety delta); socket.io lands in P2.

### 3.2 Webhooks (not usable without infrastructure)

Source: https://learn.microsoft.com/en-us/graph/change-notifications-overview (updated 2026-04-07)

- `POST /subscriptions` on resource `/users/{id}/drive/root` for OneDrive personal; requires a **publicly reachable HTTPS `notificationUrl`** with a validation handshake, or Azure Event Hubs / Event Grid.
- Max expiration for driveItem subscriptions: 42,300 minutes (< 30 days). Latency: average < 1 min, maximum 6 h.

### 3.3 Microsoft's recommended cadence

Source: https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/scan-guidance

- Crawl once with delta, then rely on notifications, and run a periodic safety delta **"no more than once per day"**. Repeated or high-rate polling gets throttled.

---

## 4. Downloads

Sources: https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0 , https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0

- `GET /me/drive/items/{id}/content` returns **302 Found** to a pre-authenticated URL identical to `@microsoft.graph.downloadUrl`. Get the URL directly with `GET /me/drive/items/{id}?$select=id,@microsoft.graph.downloadUrl` and skip the redirect.
- **No Authorization header** on the downloadUrl ("You don't need to include an Authorization header").
- **Expiry**: the driveItem page says about 1 hour ("invalidated after a short period of time (1 hour)"); the content page says "might expire within minutes". Do not cache it in the index; fetch per hydration and re-fetch on 401/403 mid-stream.
- **Range**: send `Range: bytes=start-end` to the **downloadUrl, not to `/content`**; yields **206 Partial Content** with `Content-Range`. The server **may ignore Range and return 200 with the full body**; PROGRESSIVE hydration code must handle both (treat 200-on-Range as "write ahead from offset 0").
- `If-None-Match` with eTag/cTag on `/content` returns 304.
- Download costs 1 resource unit.
- Junction with cfapi's fixed 60 s callback deadline (critique gap): hydration worker calls `CfReportProviderProgress` at least every ~15-30 s even with zero new bytes; if `Retry-After` exceeds the remaining budget, fail the request with a network-unavailable status so Explorer shows an error instead of hanging. Source for the cfapi side: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfreportproviderprogress

### 4.1 Tempauth retirement, MC1492958

Source: https://mc.merill.net/message/MC1492958 (Message Center mirror; secondary summaries: https://pupuweb.com/mc1492958-microsoft-graph-new-tenant-controls-for-retirement-of-non-standard-file-api-tokens/ , https://app.cloudscout.one/evergreen-item/mc1492958/)

- Published 2026-10-09. From **2027-04-01** (rollout early-to-late April 2027), selected Graph file APIs stop returning URLs with embedded tempauth tokens and stop issuing 302 redirects to them: download/content, createUploadSession, copy + long-running monitors, preview/thumbnail/version, format conversion. Apps must authenticate with Graph access tokens and handle direct content responses. SPO PowerShell tenant controls (off by default) let admins opt app IDs in early.
- Services named: SharePoint Online, OneDrive, Microsoft Graph, Entra ID; clouds Worldwide/GCC/GCC High/DoD. **Consumer OneDrive (1drv.com / up.1drv.com) is never mentioned**, and the controls are SPO tenant cmdlets, which strongly suggests (but does not prove) SPO/ODB-only scope. Open question.
- Design consequence: isolate the transfer layer behind an interface so the downloader can switch to bearer-authenticated `GET /content` with direct 200 responses, and re-verify Range support on that path when details appear.

---

## 5. Uploads

Sources: https://learn.microsoft.com/en-us/graph/api/driveitem-put-content?view=graph-rest-1.0 (updated 2026-08-11), https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0 , https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0

### 5.1 Simple PUT

- `PUT /me/drive/items/{parent-id}:/{filename}:/content` (new) or `PUT /me/drive/items/{id}/content` (replace). Documented for files **up to 250 MB** (the old 4 MB figure is obsolete).
- `@microsoft.graph.conflictBehavior=fail|replace|rename` goes in the **URL query string**, not the body ("should be included in the URL instead of the body"). **Default for PUT is `replace`.** The put-content page itself does not mention conflictBehavior; the rule comes from the driveItem resource page.
- For memory reasons NoDrive uses simple PUT only for small files (zero-byte to a few MiB) and sessions above that, even though PUT allows 250 MB.

### 5.2 Upload sessions (resumable)

- `POST /me/drive/items/{parentId}:/{fileName}:/createUploadSession` with body `{ "item": { "@microsoft.graph.conflictBehavior": ..., "name": ..., "fileSystemInfo": { "lastModifiedDateTime": ... }, "fileSize": ... }, "deferCommit": ... }`.
- **Default conflictBehavior for createUploadSession is `fail`** ("fail (default) | replace | rename"), while simple PUT defaults to `replace`. An engine relying on defaults behaves differently for small vs large files: **set `@microsoft.graph.conflictBehavior` explicitly on every write**. NoDrive: `fail` for new files (detects collisions), `replace` + `If-Match` eTag for edits (P2).
- `fileSize` and `description` in the item body are **personal only**; `507 Insufficient Storage` is returned if `fileSize` exceeds quota (lets us fail before transferring).
- Fragment PUTs to `uploadUrl` with `Content-Range: bytes start-end/total`:
  - each range **MUST be a multiple of 320 KiB (327,680 bytes)**;
  - each request **< 60 MiB**; **recommended 5-10 MiB**; Microsoft recommends sessions for anything **> 10 MiB**;
  - **sequential**, one fragment at a time;
  - **do not send an Authorization header** to `uploadUrl` ("might result in an HTTP 401").
- `202 Accepted` responses return `nextExpectedRanges` and a refreshed `expirationDateTime`; the final fragment returns 200/201 with the driveItem.
- Resume: `GET uploadUrl` returns the missing ranges (`nextExpectedRanges`); `416` means the range was already received; `DELETE uploadUrl` cancels; **404 means the session is gone: restart from createUploadSession**. Exponential backoff only for 5xx.
- `If-Match`/`If-None-Match` on createUploadSession return 412 on mismatch.
- **deferCommit completion differs by service**: OneDrive **Personal completes with a final PUT**; Business/SharePoint completes with a zero-length POST ("currently only supported on OneDrive for Business and SharePoint"). NoDrive is personal-only in v1; keep the branch behind the transfer interface.
- `409 nameAlreadyExists` on the last fragment can be recovered by `PUT` to the target path with `@microsoft.graph.sourceUrl={uploadUrl}` (sourceUrl is not supported on Business/SharePoint).
- Persist `uploadUrl` + expiration in the SQLite journal so an upload survives an engine restart.
- Memory: a 5-10 MiB chunk is an LOH allocation; rent from `ArrayPool<byte>.Shared` (or one fixed reusable buffer per upload worker) and stream from `FileStream`; never buffer the whole file.
- Third-party experience (source: https://rclone.org/onedrive/): chunks above ~250 MiB fail with "request message is too big"; rclone buffers chunks in memory (default 10 MiB); **every upload creates a version counting against quota, and setting mtime after upload creates another version** on business, so send `fileSystemInfo.lastModifiedDateTime` inside the createUploadSession body rather than a follow-up PATCH.
- Max file size **250 GiB** (personal and business) is from rclone's documentation; no Graph page checked states a maximum. **Third-party figure.**

### 5.3 Other mutations (v1 rename/delete propagation)

- Rename/move: `PATCH /me/drive/items/{id}` with `name` and/or `parentReference.id`. Create folder: `POST /me/drive/items/{parent-id}/children` with `folder: {}` and explicit conflictBehavior. Delete: section 7. Create/update/delete/upload cost 2 resource units each. Source: https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0 , https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online

---

## 6. Hashes and tags

Sources: https://learn.microsoft.com/en-us/graph/api/resources/hashes?view=graph-rest-1.0 (updated 2025-04-09), https://devblogs.microsoft.com/microsoft365dev/deprecation-of-sha1hash-on-onedrive-personal/ (2023-06-02), https://learn.microsoft.com/en-us/onedrive/developer/code-snippets/quickxorhash?view=odsp-graph-online , https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0

- **`quickXorHash` is the only hash guaranteed on both OneDrive personal and business.** `sha1Hash` deprecated on OneDrive Personal from July 2023 ("if available"); `sha256Hash` "isn't supported. Don't use."; `crc32Hash` "if available".
- QuickXorHash: 160-bit non-cryptographic hash; each input byte is rotated left by `11*i` bits and XORed into a 20-byte block, then the little-endian 64-bit length is XORed into the low bytes; base64 in the API. Microsoft publishes a C# `HashAlgorithm` implementation; port it to Span-based incremental hashing and compute while streaming uploads/downloads.
- **community-reported**: some personal items (e.g. freshly created Office docs) lack `quickXorHash`; fall back to size + second-precision `lastModifiedDateTime`. Source: https://forum.rclone.org/t/microsoft-is-switching-onedrive-personal-to-quickxorhash-from-sha1/36296
- **eTag** covers the entire item (metadata + content). **cTag** changes only when content changes; use cTag (or quickXorHash+size) to decide whether to re-download; use eTag for `If-Match` on writes.
- **Folder cTag caveat**: the driveItem property table says unconditionally that cTag "isn't returned if the item is a folder", with a separate note scoping that to Business/SharePoint; the report also claims that on folders cTag changes when any descendant changes while eTag changes only on the folder's own properties. **Do not rely on folder cTag on personal without testing (to verify in spike).**
- `fileSystemInfo.createdDateTime/lastModifiedDateTime` are read-write; set them to preserve local timestamps. OneDrive timestamps are second-precision; abraunegg compares whole-second timestamps first and hashes only on mismatch. Source: https://github.com/abraunegg/onedrive/blob/master/docs/client-architecture.md

---

## 7. Deletes

- `DELETE /me/drive/items/{id}` moves the item to the OneDrive **recycle bin** (default behaviour, used by NoDrive for all remote deletes). Source: https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0
- **`permanentDelete` IS supported on personal accounts** (corrects the raw report's rclone-derived claim): `POST /drives/{drive-id}/items/{item-id}/permanentDelete`, Delegated (personal) least-privileged `Files.ReadWrite`; there is no `/me/drive` shorthand, so the drive id is needed. rclone's `hard_delete` doc confirms it works for personal including free accounts. Sources: https://learn.microsoft.com/en-us/graph/api/driveitem-permanentdelete?view=graph-rest-1.0 (updated 2026-01-20), https://rclone.org/onedrive/
- Delete policy (critique gap, adopted in the plan): recycle bin only by default; mass-delete threshold pauses sync and asks; locally dirty files are never deleted/dehydrated until uploaded; remote deletes arriving via delta must not delete a locally modified, not-yet-uploaded file. Reference for the threshold idea: rclone bisync `--max-delete` 50% default, https://rclone.org/bisync/

---

## 8. Deprecations

- **`/me/drive/sharedWithMe` and `/me/drive/recent` are officially deprecated** (corrects the raw report's "no official announcement"). The v1.0 reference pages are titled "(deprecated)" and carry a Caution banner: deprecated and "will operate in a degraded state until November, 2026, after which it will stop returning data". Both list Delegated (personal) support, so personal accounts are affected. `sharedWithMe` needs `Files.Read.All` for full properties and shared items cannot be accessed without a `*.All` permission. Sources: https://learn.microsoft.com/en-us/graph/api/drive-sharedwithme?view=graph-rest-1.0 , https://learn.microsoft.com/en-us/graph/api/drive-recent?view=graph-rest-1.0 (both updated 2026-03-21). Community context: https://techcommunity.microsoft.com/t5/skills-hub-discussions/what-is-the-microsoft-graph-api-replacement-for-deprecated/m-p/4551294 , https://prismatic.io/docs/changelog/connectors/2026/06/26/ms-onedrive/
  - Consequence: no "Shared with me" view in NoDrive; shared items appear in delta as `remoteItem` entries only.
- Tempauth URLs / 302 redirects: retire 2027-04-01 (section 4.1).
- `sha1Hash` on personal: deprecated since July 2023 (section 6).
- MC1392563 (Jun-Jul 2026): business OneDrive web URLs move to onedrive.cloud.microsoft; Graph unaffected; consumer stays on live.com. Source: https://mc.merill.net/message/MC1392563
- The legacy direct consumer endpoint `api.onedrive.com` still exists in docs, but Graph is the preferred endpoint. Source: https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/direct-endpoint-differences
- Graph `/workbook` (Excel) APIs are officially supported only on business OneDrive/SharePoint (Microsoft Q&A, 2026; **community-reported**). Source: https://learn.microsoft.com/en-us/answers/questions/5835926/microsoft-graph-api-workbook-paths-for-excel-store
- Not exposed via the API (critique): Personal Vault (per third-party sync vendors, **community-reported**), OneNote notebooks (`package` facet) cannot be downloaded via `/content`. Exclude both from placeholder creation and show as unsupported. Sources: https://forum.2brightsparks.com/bb/viewtopic.php?p=52399 , https://helpdesk.resilio.com/hc/en-us/articles/41452653826963-Thumbnails-and-media-files-previews

---

## 9. Thumbnails (P2 reference)

- `GET /me/drive/items/{id}/thumbnails` returns small/medium/large sets; `/thumbnails/{id}/{size}/content` 302s to a cache-safe URL; `$expand=thumbnails` works on personal. Note the thumbnail URL path is also in MC1492958's affected list. Source: https://learn.microsoft.com/en-us/graph/api/driveitem-list-thumbnails

---

## 10. Throttling

Sources: https://learn.microsoft.com/en-us/graph/throttling-limits , https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online (ms.date 2025-10-02, updated 2026-08-10), https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/scan-guidance

- Signals: **429 Too Many Requests or 503 Service Unavailable with `Retry-After` (seconds)**. Honour it and **pause ALL in-flight requests** (one global gate for all workers); throttled calls still count against quota and ignoring Retry-After escalates to blocks.
- **No IETF `RateLimit-*` headers on SharePoint/OneDrive**: the dedicated section says "SharePoint Online does not return or support IETF RateLimit headers", although the same page's summary says to use them (internally inconsistent). Implement Retry-After as mandatory, RateLimit-* as optional hints if present.
- Graph global limit: **130,000 requests per 10 s per app across all tenants**. The Graph Files section defers to the SharePoint article for everything else.
- SharePoint/OneDrive for Business published numbers (business only, for reference): per-user **3,000 requests / 5 min**, 50 GB ingress/h, 100 GB egress/h, 50 delegation token requests / 5 min; per-app-per-tenant **1,250 RU/min and 1,200,000 RU/24 h** for tenants up to 1,000 licenses (scales with license count), 400 GB ingress and egress per hour; tenant-level buckets 18,750 RU / 5 min (0-1,000 licenses) up to 93,750.
- **Consumer OneDrive limits are unpublished**; the SharePoint doc says "Microsoft reserves the right to lower limits on Unpaid/Unlicensed usage" and that limits may change at any time. Treat consumer as stricter than business and measure in the spike.
- **Resource-unit costs**: delta with token = 1 RU (deliberately lowered); delta without token, list children = 2; create/update/delete/upload = 2; download = 1. `$batch` is evaluated per sub-request.
- **User-Agent decoration**: `ISV|CompanyName|AppName/Version` (NoDrive: `ISV|<you>|NoDrive/<version>`) to get traffic prioritised and attributable. Register your own app ID instead of a shared one (rclone notes this helps with throttling; source: https://rclone.org/onedrive/). Do not create multiple app IDs to evade limits; running the same account from many machines can trigger per-user throttling.
- Cadence rule: delta polling at most "once per day" as a safety check when notifications are used (section 3.3); NoDrive's v1 adaptive polling must remain modest.
- Field issue (**community-reported**): `SSL_read errno 104` disconnects against EU datacenters (abraunegg known-issues). Source: https://github.com/abraunegg/onedrive/blob/master/docs/known-issues.md

---

## 11. Name restrictions and collisions

- OneDrive-restricted characters (documented by rclone, which replaces them with full-width equivalents): `"`, `*`, `:`, `<`, `>`, `?`, `\`, `|`, plus trailing space or period and leading space or `~`. Source: https://rclone.org/onedrive/
- OneDrive names are **case-insensitive**; abraunegg rejects case-insensitive collisions rather than silently renaming. Source: https://github.com/abraunegg/onedrive/blob/master/docs/client-architecture.md
- Windows/NTFS adds its own invalid names (reserved device names, etc.); items whose remote name is invalid on Windows, or that collide case-insensitively within one folder, must be surfaced as user-visible conflicts instead of silent renames (research recommendation and critique "Exclusions" decision).
- Sanitize before upload and explain failures (`409 nameAlreadyExists`, `invalidRequest`) as conflicts in the tray/conflict list.

---

## 12. Why not the Microsoft.Graph SDK

Sources: https://www.nuget.org/packages/Microsoft.Graph , https://www.nuget.org/packages/Microsoft.Graph.Core , https://raw.githubusercontent.com/microsoftgraph/msgraph-sdk-dotnet/main/src/Microsoft.Graph/Microsoft.Graph.csproj , https://raw.githubusercontent.com/microsoft/kiota-dotnet/main/Directory.Build.props , https://learn.microsoft.com/en-us/graph/sdks/sdks-overview , https://learn.microsoft.com/en-us/graph/sdks/generate-with-kiota

- **Microsoft.Graph 6.10.0** (NuGet 2026-11-09, regenerated every ~2 weeks): **52.61 MB nupkg**, TFMs netstandard2.0/2.1, net8.0, net10.0; depends on Microsoft.Graph.Core 4.0.1 which pulls seven Microsoft.Kiota.* 2.0.0 packages (Abstractions, Authentication.Azure, Http.HttpClientLibrary, Serialization.Form/Json/Multipart/Text) plus Microsoft.IdentityModel.Protocols.OpenIdConnect and Validators 8.18.0 (and Azure.Core transitively).
- `<IsTrimmable>true</IsTrimmable>` for net5.0+ but **no `IsAotCompatible`**; kiota-dotnet sets none of IsTrimmable/IsAotCompatible/EnableTrimAnalyzer/EnableAotAnalyzer. Nobody has published a PublishAot result for Microsoft.Graph 6.x, so "not AOT-compatible" is a reasonable default rather than a proven incompatibility.
- Known issues: #1702 (DLL 30-62 MB; maintainers acknowledged size, pointed to Kiota custom clients and trimming, declined splitting packages, auto-closed), #2944 (open since 2025-07-02: `PageIterator<T,T2>.CreatePageIterator` throws `Arg_GetMethNotFnd` when trimmed, no maintainer response), #2896 (open: NRE in `ItemWithPath` in a packaged WinUI 3 app), kiota-dotnet #576 trim warnings; a dotnet/runtime issue documents ILLink taking very long on Microsoft.Graph. Sources: https://github.com/microsoftgraph/msgraph-sdk-dotnet/issues/1702 , https://github.com/microsoftgraph/msgraph-sdk-dotnet/issues/2944 , https://github.com/microsoftgraph/msgraph-sdk-dotnet/issues/2896 , https://github.com/microsoft/kiota-dotnet/issues/576
- Maintainers' own advice: the libraries are "fairly large"; a Kiota-generated client limited to the used paths (`kiota generate --openapi https://aka.ms/graph/v1.0/openapi.yaml --include-path "/me/drive/**" --language CSharp`) reduces size. Microsoft CSS supports only the HTTP API, not the SDKs.
- Decision (ADR-0004): raw `HttpClient` (`SocketsHttpHandler`, Range, streaming, HTTP/2, fully AOT-compatible) + `System.Text.Json` source-generated `[JsonSerializable]` contexts for driveItem (facets: `file.hashes`, `folder.childCount`, `deleted`, `fileSystemInfo`, `parentReference`, `remoteItem`, `package`, `@microsoft.graph.downloadUrl`), delta page, uploadSession and error payloads. This is what rclone and abraunegg do.
- Required endpoint list (~10): `GET /me/drive`; `GET /me/drive/root/delta[?token=...]`; `GET /me/drive/items/{id}?$select=...`; `GET downloadUrl` with Range; `PUT .../content`; `POST createUploadSession` + `PUT/GET/DELETE uploadUrl`; `PATCH /items/{id}` (rename/move); `DELETE /items/{id}`; `POST /items/{id}/children`; `GET /me/drive/root/subscriptions/socketIo`; later `permanentDelete`, `/thumbnails`.

---

## 13. Third-party references

- **rclone** (Go, **MIT**, v1.76.0 released 2026-11-06): `backend/onedrive` and docs are the best permissive protocol reference (restricted characters, 250 GiB limit, 320 KiB chunk rule, default 10 MiB chunks, > ~250 MiB fails, versioning/mtime quirk and `no_versions`, `hard_delete`, own-client-ID setup with `http://localhost:53682/`, personal vs business permission schema `grantedTo` vs `grantedToIdentities`). Code reuse allowed with attribution. Sources: https://rclone.org/onedrive/ , https://github.com/rclone/rclone , https://github.com/rclone/rclone/releases
- **abraunegg/onedrive** (D, **GPL-3.0**, v2.5.12 released 2026-10-24): `docs/client-architecture.md` is the best written sync-engine design: delta-driven, SQLite "reconciliation baseline" keyed by (driveId, id), parent links by id not path, moves recognised through id tracking, delta token as cursor, timestamp-first then hash comparison (`isItemSynced`), simple upload for zero-byte/small files and sessions otherwise, notifications as triggers only, rejection of case-insensitive collisions; `known-issues.md`: standalone sync cannot see moves made while not running (delete+create), EU `SSL_read errno 104`, AADSTS70000 from browser extensions. **Design reference only; copying code would make NoDrive GPL.** Sources: https://github.com/abraunegg/onedrive/blob/master/docs/client-architecture.md , https://github.com/abraunegg/onedrive/blob/master/docs/known-issues.md , https://github.com/abraunegg/onedrive/releases
- **Microsoft "Best practices for discovering files and detecting changes at scale"** (scan guidance): the closest official sync-engine guide (crawl via delta, webhooks, cTag to skip unchanged downloads, honour Retry-After). Source: https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/scan-guidance
- fast.io delta guide (third-party, mentions `@removed`): https://fast.io/resources/onedrive-delta-sync.md

---

## 14. Open questions for the spike (P0)

1. **403 `serviceReadOnly` / 503 provisioning on a freshly consented personal app** (section 1.7): create tenant + app, sign in with a fresh personal account, call `/me/drive/root/delta` with `Files.ReadWrite` on day one.
2. **Delta page size**: which `$top` values consumer delta honours, the default page size, and the real first-sync time for ~200k items streamed into SQLite (section 2).
3. **Delta edge cases on personal**: whether `@removed` ever appears, how cross-folder moves and `remoteItem` (shared folder) moves are emitted, and whether folder cTag is returned on personal (sections 2, 6).
4. **MC1492958 scope**: does the tempauth/302 retirement on 2027-04-01 apply to consumer 1drv.com / up.1drv.com URLs, and will Range be honoured on bearer-authenticated `GET /content` afterwards (section 4.1).
5. **Consumer throttling thresholds** (unpublished): measure what delta ingestion at 200-item pages and 3-4 parallel hydrations trigger; build the fake Graph server with 429 `Retry-After` injection regardless (section 10).
6. **socket.io channel** (P2, but cheap to probe): can a .NET Engine.IO v4 client connect to the returned `notificationUrl` for a personal account, what is the session lifetime, and does it fire for all sub-folders (section 3.1).
7. **MSAL + WAM under PublishAot** (only if WAM is ever revisited): does Broker + NativeInterop 0.20.7 publish cleanly on .NET 11 win-x64, and what is the on-disk/idle-memory cost of msalruntime (section 1.6).
8. **Range fallback frequency**: how often the consumer downloadUrl answers Range with a 200 full body, which determines whether PROGRESSIVE hydration is worth it for large media (section 4).


## Measured on the developer's personal account (spike `spikes/graph-smoke`, 2026-11-13, run from macOS)

Facts observed against the real drive (460.5 GB used of 1061 GB), client ID `6711e7c6-ec09-42df-a5bb-bfb449119fdc` registered the same day:

- **No 403 `serviceReadOnly` / 503 provisioning** on a freshly consented personal-account app: `/me/drive` returned 200 on the first call. The community-reported risk did not reproduce (one account, one day; keep the check in the engine's start-up diagnostics).
- **PKCE + system browser + loopback** works with the `consumers` authority and redirect `http://localhost:<random port>` against a registration that lists only `http://localhost`. Token exchange about 1 s. Granted `scope` string in the token response is `Files.ReadWrite` only (offline_access is implied by the presence of `refresh_token`).
- **Refresh tokens rotate**: every `refresh_token` grant returned a new `refresh_token` (`expires_in` 3599). The engine must persist the newest one atomically after every refresh, or the next refresh fails.
- **Delta page size**: `$top=1000` is honoured approximately; pages contained 1019-1201 items (so never assume an exact cap), 700-800 KiB of JSON each with the `$select` below, 2.5-5.5 s per page from this location. `$select=id,name,size,lastModifiedDateTime,folder,file,parentReference,deleted,cTag,eTag` works on consumer delta.
- **`@microsoft.graph.downloadUrl`** is **omitted** when the item is requested with `$select=id,name,size,@microsoft.graph.downloadUrl`; it is present on a plain `GET /me/drive/items/{id}` (no `$select`). The documented `?select=id,@microsoft.graph.downloadUrl` spelling and the `/content` 302 were not needed (attempts B and C in the spike did not run because A succeeded). Download host: `my.microsoftpersonalcontent.com`.
- **HTTP Range on the download URL**: `Range: bytes=0-1023` -> 206 with `Content-Range: bytes 0-1023/252923` and `Accept-Ranges: bytes`; a mid-file `Range` (offset ~126 KB, 4 KiB) also -> 206. Random-access partial hydration (PROGRESSIVE policy) is feasible. First range request ~1.6 s (TLS + CDN), subsequent ~0.7 s.
- **Hashes on a personal-account file**: `quickXorHash`, `sha1Hash` **and** `sha256Hash` were all present on the item, contrary to the docs' "sha1 deprecated on personal, sha256 not supported". Treat quickXorHash as the only guaranteed one, but opportunistically verify with sha256 when present.
- `cTag` format on personal: `"c:{GUID},2"`; `eTag` also returned.
- Process footprint of the JIT spike (plain console, HttpClient, JsonNode): working set 69-74 MB, GC heap 7-17 MB after three 800 KiB pages. Not representative of the AOT engine; recorded only as a baseline for the P0 step 2 measurements.
- **`parentReference.path` IS present on consumer delta items** (e.g. `/drive/root:/Documentos/Outlook Files`), contrary to the docs' "path is absent" warning; still track by id (paths can be absent for shared/remote items and the docs do not promise it), but it is a free diagnostic. Second full enumeration (`--largest 15`) took 340.7 s for the same 136,411 items.
- **Largest files on the drive**: 10.9 GB `.pst`, 8.1 GB `.iso`, several 3-5 GB `.mp4`/`.pst`; nothing near 1 GB, hence `aot-baseline --limit-mb`.
- **Full-drive initial delta (`--all`, `$top=1000`), 2026-11-13 from macOS**: **136,411 items** (17,286 folders, 119,125 files, 0 deleted) in **129 pages**, **388.7 s** (6.5 min) end to end, average 3.0 s per page (min 1.4 s, max 11.3 s), 88 MiB of JSON in total with the `$select` above, **no 429/503** at one request at a time, `@odata.deltaLink` reached. Extrapolation for the 500k-item design target: about 475 pages, 25 min, 320 MiB of JSON streamed through the parser (never held in memory). Spike process stayed at 72 MB working set / 11 MB GC heap because each page is parsed and dropped.
