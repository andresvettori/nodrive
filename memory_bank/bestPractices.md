# NoDrive best practices (coding rules for this repo)

Each rule has a one-line rationale. Rules marked **community-reported** rest on Microsoft Q&A
moderator answers or user reports, not Microsoft documentation; "to verify in spike" rules are
confirmed or amended in P0. Architecture context: `systemPatterns.md`. Decisions: `decisions/`.
Responsiveness and correctness outrank features: when a rule here conflicts with a feature, the
feature waits.

---

## 0. Runtime configuration

- Never use `runtimeconfig.template.json` for the engine or AOT spikes: Native AOT drops it with a warning (CI 2026-11-13). Put every runtime knob in `eng/LowFootprint.props` as `RuntimeHostConfigurationOption` items; confirm with `GC.GetConfigurationVariables()` at start-up in debug builds.

## 1. cfapi callback rules

1. **Never block inside a callback.** Validate, copy the needed fields, enqueue to a bounded
   channel, return. No network, disk, SQLite, locks that can wait, or `await`.
   Rationale: callbacks run on CldApi's threadpool threads with a fixed 60 s deadline; slow
   callbacks are exactly what freezes Explorer (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_callback_type).
2. **Callbacks are `[UnmanagedCallersOnly]` static methods that never throw.** Wrap the body in
   `try/catch`, log, and fail the request from a worker. Rationale: an exception escaping an
   unmanaged callback terminates the process; a dead provider makes every online-only file fail
   with 0x8007016A (**community-reported**, https://github.com/B3nedikt/Tenshu/pull/6; the one
   connection per sync root rule is documented at https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot).
3. **Full channel => fail the request now with a status.** Never wait for room. Rationale: a
   waiting callback is a hung Explorer thread; a failed request is a visible error the user can
   retry.
4. **Ack `NOTIFY_DELETE`/`NOTIFY_RENAME` immediately from local state** (`ACK_DELETE`/`ACK_RENAME`
   via `CfExecute`), journal the remote operation, and let the sync worker catch up. Rationale:
   these callbacks block the user's operation until acked; the network must never be in that path
   (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_callback_type).
5. **Make delete/rename handling idempotent, keyed on `RequestKey`/`FileId`.** Rationale: one user
   delete can raise several `NOTIFY_DELETE`s (Recycle Bin move then delete, per-child callbacks,
   delete-on-close); **community-reported** FAQ (https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq).
6. **Call `CfReportProviderProgress` at least every 15 s while a hydration is alive**, even with
   zero new bytes, and call it with the real `TotalBytes`/`CompletedBytes`. Rationale: it resets
   the request timer and drives Explorer's progress UI (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfreportproviderprogress).
7. **Every request carries a `RequestBudget` far below 60 s** (enumeration 30 s, hydration 45 s
   without progress, ack 2 s) and the supervisor completes expired ones with a failing
   `CompletionStatus`. Rationale: a request that hits the platform timeout shows 0x800701AA and may
   leave Explorer in a bad state for that folder (**community-reported**, same FAQ).
8. **Fail a request with a `CompletionStatus` in `CF_OPERATION_PARAMETERS`; never leave it pending
   and never use `CF_OPERATION_ACK_DATA_FLAG_CANCELED`.** Rationale: that flag does not exist
   (only `CF_OPERATION_ACK_DATA_FLAG_NONE` is documented); the failing status is the only abort
   mechanism (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ns-cfapi-cf_operation_parameters).
9. **Honour `CANCEL_FETCH_DATA` / `CANCEL_FETCH_PLACEHOLDERS` through a per-request
   `CancellationTokenSource`**, and keep our own tokens for bulk pins. Rationale: cancelling a pin
   operation does not raise a cancel callback (**community-reported** FAQ).
10. **`FETCH_PLACEHOLDERS` reads only from `IItemIndex`, paged, with
    `TRANSFER_PLACEHOLDERS` per page.** Rationale: a folder with tens of thousands of children must
    answer from disk in well under the deadline; Graph latency on that path is a hang.
11. **`TRANSFER_DATA` chunks are 4 KiB-aligned and come from fixed-size pooled buffers.** Rationale:
    alignment is required by `CF_OPERATION_PARAMETERS` (to verify exact rule in spike; Nextcloud and
    ownCloud follow it) and fixed buffers keep memory flat.
12. **Serve random offsets, not just sequential from zero.** Rationale: PROGRESSIVE hydration asks
    for whatever range the app touched (https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine).
13. **Open placeholders for updates with `CfOpenFileWithOplock` and release on break.** Rationale:
    the engine must never fight a user's open handle (plan rule 7).
14. **Prefer `CfSetInSyncState`/`CfSetPinState` over `CfUpdatePlaceholder`.** Rationale: heap
    corruption on 24H2/25H2 is **community-reported** and unresolved
    (https://learn.microsoft.com/en-us/answers/questions/2120335/heap-corruption-after-using-cfupdateplaceholder); P0 step 4 decides.
15. **Connect with `REQUIRE_PROCESS_INFO | BLOCK_SELF_IMPLICIT_HYDRATION`** and log the hydrating
    image path. Rationale: antivirus and memory-mapping apps hydrate whole files; we need to see who
    (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot).
16. **Handle `CF_CALLBACK_FETCH_DATA_FLAG_RECOVER` as a normal request from the required offset.**
    Rationale: it marks a hydration interrupted by crash or power loss; no in-memory state survives
    (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot).
17. **Watch `FILE_ATTRIBUTE_UNPINNED` yourself and dehydrate.** Rationale: `NOTIFY_DEHYDRATE` does
    not fire for Explorer "Free up space" (**community-reported**, https://learn.microsoft.com/en-gb/answers/questions/1694157/when-will-cf-callback-type-notify-dehydrate-be-inv).
18. **`CfDisconnectSyncRoot` on shutdown; never `CfUnregisterSyncRoot` outside the uninstall flow.**
    Rationale: unregistering permanently deletes unhydrated placeholders
    (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot).
19. **Measure callback-to-return latency in-process** (histogram; p99 < 5 ms, max < 50 ms). Rationale:
    it is the acceptance test for "never hang Explorer".

## 2. AOT and trim hygiene

1. **CsWin32 with `CsWin32RunAsBuildTask=true`, `DisableRuntimeMarshalling=true`,
   `NativeMethods.json { "allowMarshaling": false }`, `AllowUnsafeBlocks=true`.** Rationale: this is
   the documented Native AOT path; the runtime marshaller is unavailable and `AllowUnsafeBlocks=false`
   breaks generated code (https://microsoft.github.io/CsWin32/docs/getting-started.html).
2. **List only the functions we call in `NativeMethods.txt`.** Rationale: smaller binary, fewer
   trim surprises; all 36 Cf* functions exist in Win32Metadata if needed
   (https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/Storage/CloudFilters/index.html).
3. **Native callbacks are `[UnmanagedCallersOnly]` static methods passed as function pointers**
   (`delegate* unmanaged[Stdcall]`; exact `CallConv` to verify in spike). Rationale: `CF_CALLBACK` is
   a C function pointer and delegates cannot be marshalled with runtime marshalling disabled.
4. **No reflection-based JSON.** One `JsonSerializerContext` per assembly, `[JsonSerializable]` per
   root type, metadata mode where streaming is needed, `JsonStringEnumConverter<T>`; keep
   `JsonSerializerIsReflectionEnabledByDefault=false`. Rationale: reflection serialization breaks
   under AOT (https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation).
5. **Source generators only: `LibraryImport`, `GeneratedComInterface`/`ComWrappers`, STJ, CsWin32,
   CsWinRT.** No `DllImport` with marshalling, no built-in COM, no `Reflection.Emit`, no dynamic
   assembly loading. Rationale: Native AOT limitations (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).
6. **`IsAotCompatible=true` on every library project (Core, Graph); `PublishAot=true` only on the
   exes.** Rationale: turns on the trim/AOT analyzers where the code lives.
7. **Zero IL2xxx/IL3xxx warnings from our code; treat them as errors in CI.** Rationale: a warning
   is a runtime failure waiting for AOT publish.
8. **`VerifyReferenceAotCompatibility=true` is an inventory, not a gate.** Rationale: the
   `IsAotCompatible` assembly metadata only exists for libraries built for .NET 10+, so older
   packages warn regardless of real behaviour (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).
9. **Trim switches on the engine:** `OptimizationPreference=Size`, `InvariantGlobalization`,
   `UseSystemResourceKeys`, `EventSourceSupport=false`, `MetricsSupport=false`,
   `HttpActivityPropagationSupport=false`, `Http3Support=false`,
   `XmlResolverIsNetworkingEnabledByDefault=false`. Rationale: documented size/footprint levers
   (https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options).
10. **Keep `DebuggerSupport=true` and `StackTraceSupport=true`.** Rationale: `DebuggerSupport=false`
    removes symbols we need for crash dumps, and in .NET 11 `StackTraceSupport=false` makes
    stack-trace APIs throw `NotSupportedException`
    (https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime).
11. **Leave Runtime Async off in our code (`UseRuntimeAsync=false`) for v1.** Rationale: it is a
    preview feature; the BCL is compiled with it regardless, so we gain nothing by opting in
    (https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime).
12. **WinRT only through the `net11.0-windows10.0.26100.0` TFM projection; watch for NETSDK1229 and
    never let a TFM revision pull CsWinRT 3.0 preview silently.** Rationale: CsWinRT 2.3.1 documents
    .NET 9/10 only; AOT of the projection on .NET 11 is unverified until P0 step 3
    (https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md, https://github.com/dotnet/sdk/discussions/54975).
13. **No WinForms, WPF, or any UI framework in the engine.** Rationale: neither is trim/AOT
    compatible (https://api.github.com/repos/dotnet/winforms/issues/4649, https://api.github.com/repos/dotnet/wpf/issues/3811)
    and nothing with a UI framework may be resident.
14. **Publish on Windows only (`dotnet publish -r win-x64 -c Release`).** Rationale: Native AOT
    does not cross-compile across operating systems
    (https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile).

## 3. Memory hygiene

1. **Transfer buffers come from a dedicated `ArrayPool<byte>` with one fixed size (1-4 MiB) and are
   returned in `finally`.** Rationale: fixed sizes keep the pool flat; a leaked rental is a leak of
   the whole buffer.
2. **Streaming only.** `HttpCompletionOption.ResponseHeadersRead`, `CopyToAsync`/manual read loop
   into `CfExecute` or a `FileStream` (`FileOptions.Asynchronous | SequentialScan`),
   `DeserializeAsyncEnumerable` for delta pages, `ReadOnlyMemoryContent` over a rented chunk for
   uploads. Never `ReadAsByteArrayAsync`, never a `MemoryStream` of a file, never a `List<DriveItem>`
   of a whole page set.
3. **After a burst: dispose, return, drop caches, then `GC.Collect(GC.MaxGeneration,
   GCCollectionMode.Aggressive)`; re-check `GC.GetGCMemoryInfo().TotalCommittedBytes` a few seconds
   later and collect once more if commit did not drop.** Rationale: Aggressive requests maximal
   decommit and already implies compacting (so `LargeObjectHeapCompactionMode = CompactOnce` is
   redundant); .NET 10+ ages freed UOH regions over two gen2 GCs
   (https://learn.microsoft.com/en-us/dotnet/api/system.gc.collect?view=net-11.0, https://devblogs.microsoft.com/dotnet/preparing-for-dotnet-10-gc/).
4. **Use the 2-argument `GC.Collect` overload for Aggressive.** Rationale: the 4-argument overload
   with `compacting: false` throws `ArgumentException` for Aggressive
   (https://raw.githubusercontent.com/dotnet/runtime/main/src/coreclr/System.Private.CoreLib/src/System/GC.CoreCLR.cs).
5. **No periodic `GC.Collect`, no `EmptyWorkingSet`/`SetProcessWorkingSetSize` loops.** Rationale:
   working-set trimming only moves pages to standby and can cause failures; commit is the metric
   (https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset).
6. **Runtime config is fixed in `runtimeconfig.template.json`:** `System.GC.Server=false`,
   `System.GC.Concurrent=false`, `System.GC.ConserveMemory` 5-7, `System.GC.RegionSize` 1 MB,
   `System.GC.RetainVM=false`, optional `System.GC.HeapHardLimit`. Rationale: documented knobs for
   small resident heaps; RegionSize is a runtimeconfig setting since .NET 10
   (https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector).
7. **One static `HttpClient` over one `SocketsHttpHandler`:** `PooledConnectionIdleTimeout` 20-30 s,
   `PooledConnectionLifetime` ~5 min, `MaxConnectionsPerServer` 4-8. Do not use
   `ShouldEvictConnection`. Rationale: short idle timeout releases TLS buffers after a sync;
   `ShouldEvictConnection` is `[Experimental("SYSLIB5008")]` in .NET 11
   (https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.pooledconnectionidletimeout?view=net-10.0, https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.shouldevictconnection?view=net-11.0).
8. **No timers with a period under one minute while idle.** Rationale: frequent timers keep pool
   threads alive and the process never goes quiet
   (https://learn.microsoft.com/en-us/dotnet/api/system.threading.timer?view=net-11.0).
9. **No `Microsoft.Extensions.Hosting`, `Logging`, `DependencyInjection` or `Configuration` in the
   engine.** Hand-rolled main loop, a tiny structured logger writing to a rolling file, JSON config
   via STJ source generation. Rationale: the host pulls about 16 packages and its AOT status is
   unverified; the engine is a single-purpose process.
10. **Bounded channels everywhere; no unbounded `Channel`, `ConcurrentQueue` growth or
    `Task.Run` fan-out.** Rationale: memory is independent of item count only if in-flight state is
    bounded by construction.
11. **The item tree never lives in RAM.** No dictionary of all items, no path cache beyond an LRU
    of a few hundred folders. Rationale: 500k items is the design target; OneDrive.exe's 4 GB is the
    anti-pattern.
12. **Measure Private Bytes / commit, not Task Manager "Memory".** Record idle (2 and 10 min) and
    post-burst figures in `progress.md`. Rationale: working set is misleading and no published
    baseline exists for this kind of process.

## 4. Graph rules

1. **Set `@microsoft.graph.conflictBehavior` explicitly on every write.** Rationale: simple PUT
   defaults to `replace` while `createUploadSession` defaults to `fail`
   (https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0).
2. **`If-Match: <eTag>` on every content replace or metadata update of an existing item; 412 =>
   conflict copy.** Rationale: prevents silent overwrites of a remote change
   (https://learn.microsoft.com/en-us/graph/api/driveitem-put-content?view=graph-rest-1.0).
3. **One global `ThrottleGate` honours `Retry-After` on 429 and 503 for all workers; `RateLimit-*`
   headers are optional extras.** Rationale: throttled calls still count and ignoring Retry-After
   escalates to blocks; SharePoint/OneDrive do not return IETF RateLimit headers
   (https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online).
4. **`User-Agent: ISV|<owner>|NoDrive/<version>` on every request.** Rationale: documented
   decoration for prioritised handling (same source).
5. **No `Authorization` header on `@microsoft.graph.downloadUrl` or `uploadUrl` requests.**
   Rationale: they are pre-authenticated; a bearer on `uploadUrl` can yield 401
   (https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0).
6. **Fetch `downloadUrl` per hydration request, never cache it, re-fetch on 401/403/404 mid-stream,
   and never log it.** Rationale: URLs expire within about an hour or less and embed a token
   (https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0).
7. **Send `Range` to the downloadUrl, not to `/content`, and handle a 200 full body as "stream from
   offset 0".** Rationale: documented fallback when the range cannot be generated (same source).
8. **Upload fragments are multiples of 320 KiB (327,680 bytes), under 60 MiB, 5-10 MiB in
   practice, sequential, with `Content-Range`.** Rationale: hard requirements of upload sessions;
   non-aligned fragments break the final commit
   (https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0).
9. **Simple PUT only up to 4 MiB; sessions above that; journal `uploadUrl` and resume with `GET
   uploadUrl` -> `nextExpectedRanges`; 404 => restart; 416 => continue.** Rationale: keeps buffers
   small and uploads resumable across crashes (same source).
10. **Send `fileSystemInfo.lastModifiedDateTime` in the upload request, not in a follow-up
    PATCH.** Rationale: a later timestamp PATCH creates an extra version counting against quota
    (https://rclone.org/onedrive/).
11. **Delta: `$select` the fields we store, track by `id` and `parentReference.id`, deletes first,
    last occurrence wins, prune folders only when empty, persist `nextLink` after every page, handle
    410 Gone with both resync codes.** Rationale: documented delta contract
    (https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0).
12. **Tolerate missing `ctag`/`size` on consumer deletes and missing `quickXorHash` on some items;
    fall back to size + second-precision timestamp.** Rationale: documented omissions and
    **community-reported** gaps (same source; https://rclone.org/onedrive/).
13. **quickXorHash is the only hash we compute or compare.** Rationale: the only hash guaranteed on
    both personal and business; sha1 deprecated on personal, sha256 unsupported
    (https://learn.microsoft.com/en-us/graph/api/resources/hashes?view=graph-rest-1.0).
14. **Scopes are exactly `Files.ReadWrite offline_access` (`openid`/`User.Read` only by an explicit
    decision, see ADR-0003); authority `/consumers`; public client,
    no secret; rotate refresh tokens (discard the old one); tokens at rest only via DPAPI.**
    Rationale: least privilege and the documented public-client flow
    (https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow).
15. **Never call `sharedWithMe`, `recent` or `permanentDelete` from the engine; `permanentDelete`
    only behind an explicit user action in Settings.** Rationale: the first two stop returning data
    after Nov 2026; permanent deletion bypasses the user's safety net
    (https://learn.microsoft.com/en-us/graph/api/drive-sharedwithme?view=graph-rest-1.0, https://learn.microsoft.com/en-us/graph/api/driveitem-permanentdelete?view=graph-rest-1.0).
16. **All Graph access goes through `IRemoteDrive`/`IContentTransfer`; no `HttpClient` outside
    `NoDrive.Graph`.** Rationale: the tempauth URL retirement (2027-04-01, consumer scope unclear)
    and testability both need one seam (https://app.cloudscout.one/evergreen-item/mc1492958/).
17. **Sanitise names against OneDrive restricted characters and case-insensitive collisions before
    upload and surface a conflict, never a silent rename.** Rationale: rclone/abraunegg practice
    (https://rclone.org/onedrive/).

## 5. Data-safety rules

1. **Journal before mutate.** The journal row and the local state change commit in one SQLite
   transaction before any Graph call. Rationale: crash recovery replays the journal; nothing is
   lost between "acked locally" and "sent".
2. **Every handler is idempotent.** Replaying a journal entry, a delta page, or a duplicate
   callback must be a no-op. Rationale: duplicates are normal (double deletes, resumed pages).
3. **Mass-delete threshold.** More than N items or X% of the index deleted in one batch parks the
   remote deletes and pauses sync until the user confirms. Rationale: a bad local operation must not
   empty the cloud (https://rclone.org/bisync/ documents the same guard).
4. **Never dehydrate or remotely delete a `NotInSync`/`Dirty`/`LocalNew` item.** Rationale:
   unsynced bytes exist only locally.
5. **Remote deletes use `DELETE /items/{id}` (recycle bin) only.** Rationale: recoverable by the
   user; see Graph rule 15.
6. **Never `CfUnregisterSyncRoot` except in the uninstall flow, after disconnect and a hydrate-all
   prompt or explicit warning.** Rationale: it permanently deletes unhydrated placeholders
   (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot).
7. **Refuse to start while OneDrive.exe is linked to the same account.** Rationale: two engines on
   one account duplicate every file and fight over changes.
8. **Sync root under `%USERPROFILE%\NoDrive`, never at a drive root, NTFS only, no overlapping
   roots (`CfGetSyncRootInfoByPath` before registering).** Rationale: platform constraints
   (https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine).
9. **Exclude unsupported items (OneNote `package` facet, Personal Vault) from placeholder creation
   and list them as unsupported.** Rationale: their content cannot be downloaded via `/content`.
10. **Echo suppression: store our own `eTag`/`cTag` after every write.** Rationale: the next delta
    page must not re-download our own upload.
11. **Secrets (tokens, downloadUrl, uploadUrl) never appear in logs or crash dumps' string tables
    where avoidable.** Rationale: they grant access to the user's files.

## 6. Testing rules

1. **`NoDrive.Core` and `NoDrive.Graph` build and test on any OS.** Platform access goes through
   `IRemoteDrive`, `IContentTransfer`, `IPlaceholderStore`, `IItemIndex`, `IJournal`, `IClock`;
   Windows-only code lives in `NoDrive.Engine`. Rationale: planning and most development happen on
   macOS; the reconciler must be unit-testable there.
2. **Windows-only integration tests are marked (`[SupportedOSPlatform("windows")]` + a trait)
   and run only on the Windows PC / `windows-latest`.** Rationale: cfapi and Explorer cannot be
   exercised elsewhere.
3. **The fault-injecting fake Graph server is a first-class test asset** (429 with long
   Retry-After, 503, timeouts, expired downloadUrl 401/403, 200-on-Range, 410 Gone with both codes,
   duplicate ids, deletes-before-parents, missing ctag/size/hash, 507, 404 on uploadUrl, 500k-item
   synthetic delta). Rationale: every "never hang" acceptance test depends on it.
4. **Callback latency histogram runs in every integration test** and fails the test on p99 > 5 ms
   or max > 50 ms. Rationale: acceptance criterion from the plan.
5. **Property/fuzz tests for the reconciler**: random interleavings of delta pages, local changes
   and callbacks must preserve the invariants in `systemPatterns.md` section 4. Rationale: sync
   engines fail on orderings nobody wrote a unit test for.
6. **Crash tests**: kill the engine during hydration and during delta ingestion, restart, assert
   no stuck placeholders and no lost journal entries. Rationale: plan acceptance test.
7. **Memory tests record Private Bytes/commit at idle (2 and 10 min) and after a 1 GB and 10 GB
   burst** against the synthetic 500k-item index. Rationale: idle memory independent of item count
   is a measured criterion, not an assumption.
8. **A manual Explorer checklist runs before each release** (Notepad/Photos open of online-only
   file, Office save, rename storm, Free up space, AV scan, provider killed mid-hydration, reboot,
   move 10k placeholders in and out). Rationale: Explorer behaviour cannot be fully automated.
9. **No test may call the real Graph or a real account unless tagged `[Trait("Live")]`.**
   Rationale: throttling and consent surprises must not break CI.
10. **The release gate is `projectPlan.md` section 6, run on the Windows PC before every release:**
    callback latency (p99 < 5 ms, max < 50 ms over a synthetic 500k-item drive); Explorer
    open/copy/move/rename/delete with the fake server returning 429 Retry-After 120 s or the network
    pulled mid-hydration completes or fails visibly within 10 s; a 10k-placeholder folder move in and
    out of the root finishes in local-move time with no duplicates; kill mid-hydration and
    mid-ingestion leaves no stuck placeholders or lost journal entries; idle Private Bytes < 30 MB
    with 500k items and back within 10 MB of idle 60 s after a 10 GB burst; a 24 h soak with injected
    faults shows zero pending requests older than 60 s, zero crashes, zero Explorer restarts.
    Rationale: these are the plan's acceptance tests for "never hang Explorer, never balloon memory".

## 7. Documentation rules

1. **Read every `memory_bank/*.md` at the start of a session; update `activeContext.md` and
   `progress.md` at the end of every session.** Rationale: the memory bank is the only durable
   context across sessions.
2. **Every decision change gets an ADR in `memory_bank/decisions/ADR-000X.md`** (context, decision,
   consequences, status: proposed/accepted/superseded, link to the ADR it supersedes), and the
   table in `systemPatterns.md` section 8 is updated. Rationale: decisions must be re-checkable.
3. **Every factual claim about an API, limit, version or bug carries a source URL, or is marked
   "community-reported" or "to verify in spike".** Rationale: the research found several stale or
   wrong "facts"; provenance is what lets us re-verify.
4. **"Microsoft confirmed" is reserved for Microsoft documentation or Microsoft engineers.** Q&A
   answers from external moderators are "community-reported". Rationale: correction from the
   research skeptics.
5. **Spike results (numbers, screenshots, publish logs) go into `progress.md` and
   `memory_bank/research/`** with the date and the Windows build. Rationale: platform behaviour
   changes per build.
6. **`techContext.md` lists every dependency with version and licence**; adding a package means
   updating it in the same commit. Rationale: MIT licence hygiene and AOT inventory.

## 8. Git and commit conventions

1. **Small, single-purpose commits** that build (on Windows for Engine; anywhere for Core/Graph).
   Rationale: bisectable history for a platform where bugs are intermittent.
2. **Conventional-commit prefixes**: `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `build`,
   `chore`, `spike`; scope in parentheses (`feat(engine): ...`, `docs(memory-bank): ...`,
   `spike(p0-3): ...`). Rationale: readable history and easy changelog generation.
3. **Subject in imperative mood, under 72 characters; body explains why, not what.** Rationale:
   the diff shows what.
4. **Commits that change behaviour reference the rule or ADR they implement or alter**
   (`Refs ADR-0007`, `Rule cfapi-7`). Rationale: ties code to the memory bank.
5. **Never commit secrets, tokens, `index.db`, or the Entra client id of a private registration
   outside the config example.** Rationale: the repo is MIT and public-ready.
6. **Branch per phase step (`p0/03-sync-root`), merge to `main` only with a green Windows build
   and zero IL2xxx/IL3xxx warnings.** Rationale: `main` must always publish AOT-clean.
7. **Assistant-made commits carry the `Co-Authored-By` trailer the session is configured with.**
   Rationale: attribution transparency.

## Lessons from the implementation (2026-11-16)

- **Never spawn `explorer.exe` yourself** to show a folder; use ShellExecute on the path (a child explorer.exe could not browse cloud folders).
- **Acks first, work later**: NOTIFY_DELETE/RENAME and FETCH_* callbacks only validate, enqueue and return; persist the operation (SQLite journal) before touching the network; skip enqueueing for our own process.
- **Per-user cross-session names**: use `Global\...<SID>` for the single-instance mutex and the quit event (installer, scheduled task and terminal run in different sessions).
- **Every cancel path completes the request** (success or a failing status); a quiet exit leaves Explorer or the app waiting.
- **Identical-content adoption beats duplicates**: before uploading a local file that already has a cloud twin (same name, size, mtime), link it; conflict copies are only for real differences.
- **Wake loops on completion**, never a fixed sleep after a batch (it capped linking at one item per second).
- **Test with the CI Native AOT exe or MSI**, not an unsigned local JIT build; and open the settings page again after an engine restart (the token changes).
- **UI rules from user feedback**: a prominent Save bar in the page header, no layout that overflows its card, real file names (never internal ids), Windows regional formats and English/Spanish text, and optional features that never run unless the user opens them.
- **Folders converted from plain folders must be told to fill in from the cloud** (`CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION` or `CfUpdatePlaceholder` with ENABLE_ON_DEMAND_POPULATION); otherwise Windows treats them as complete and cloud-only files never appear. Never drop a journal entry when a conversion fails: retry with backoff.

- **Remote install over SSH:** the MSI's LaunchEngine starts the engine in the SSH session (no desktop, no tray icon). After a silent remote install, quit it and start it in the interactive session through a one-shot scheduled task (Interactive logon, RunLevel Limited), then check `SessionId` is not 0. Put multi-line PowerShell in a .ps1 sent with `mac-put.sh` instead of quoting it inline.
