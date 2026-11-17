# macOS port: plan and spike (not started; decided 2026-11-16 to prepare cheaply and spike after the Windows beta settles)

## Goal
NoDrive on macOS with Finder integration comparable to OneDrive for Mac: online-only items, "Download Now / Remove Download", status badges, low idle memory, never block Finder.

## How macOS integration works
Apple's File Provider framework: a **replicated File Provider extension** (macOS 11+; [Apple docs](https://developer.apple.com/documentation/fileprovider/replicated-file-provider-extension.md)) is an app extension hosted by the system. The system keeps the local state and calls the extension to enumerate, fetch, create, modify and delete items, and the extension reports remote changes through sync anchors and the working set. It must be native (Swift/Objective-C), cannot be Mac Catalyst, needs entitlements and a signed, notarized app. This replaces the Windows Cloud Files layer; change tracking of local edits is done by macOS (no watcher, no offline reconcile needed on the Mac).

OneDrive for Mac itself uses File Provider, so the two can coexist (its folder lives under `~/Library/CloudStorage`).

## Architecture options
1. **Thin Swift extension + NoDrive .NET engine over XPC (preferred).** The extension only translates File Provider calls into requests to the engine (Core + Graph + index + web page), connected through [NSFileProviderService / XPC](https://developer.apple.com/documentation/foundation/nsfileproviderservice.md). Reuses almost all logic. Unknown: a Native AOT .NET helper acting as the XPC peer (no source found; must be tested) and sandbox/app-group rules for the helper.
2. **Everything in Swift.** Reimplement Graph client, index and sync in Swift (about the size of Core+Graph, roughly 4-6 weeks more); no XPC risk but no code sharing and a second codebase to maintain.
3. **FUSE-style mount (macFUSE/FUSE-T).** Fast to build but no Finder badges or "Download Now / Remove Download", third-party kernel/NFS component; not recommended for a OneDrive replacement.

## Portability map (2026-11-16)
- **Portable already (built and tested on macOS in CI):** `NoDrive.Core` (index, delta, population/hydration policy, bandwidth limiter, name rules), `NoDrive.Graph` (PKCE, delta, download, upload, delete, move; `ITokenStore`, `FileTokenStore`), the tests, the web page (`Web/index.html`), the guides.
- **Portable logic that still references Windows classes in the engine project (needs extraction into a shared project when the port starts):** `ActivityLog`, `EngineConfig`, `EnginePaths`, `Log`, `ChildFilter`, `Hydrator`, parts of `SyncService`, `Diagnostics`, `SupportBundle`, `SwitchAssistant`, the web server (`WebUi` uses Win32 only for locale/language and folder opening).
- **Windows-only and replaced on macOS:** `CloudProvider`, `PlaceholderWriter`, `PlaceholderInfo`, `RemoteApplier`, `UploadService`, `OfflineReconciler`, `LocalOps` callbacks, `PinWatcher`, `SyncRootRegistrar`, `NavigationPane`, `Coexistence`, `UserSettings` (registry), `Lifecycle` (global mutex/event, MSI hooks), `TrayWindow`, `DpapiTokenStore`, the WiX installer.
- **Mac equivalents to build:** Keychain token store, menu-bar item (AppKit), `~/Library/Application Support/NoDrive` paths, launch at login (SMAppService), `.pkg`/DMG with notarization.

## Spike (2 to 3 weeks) and acceptance criteria
1. Xcode project with a host app and a replicated File Provider extension that shows a root with three real items from NoDrive's index. Record exactly which entitlements, app group and signing the development run needs (free vs paid Apple developer account).
2. The extension fetches one file's content by asking a .NET helper (Native AOT, osx-arm64) over XPC; measure latency and memory of both processes.
3. One remote change (cloud rename) shows up in Finder through the working set; one local edit reaches the helper as `modifyItem`.
4. Decision note: go or no-go for option 1, with the effort re-estimated. Kill criteria: the .NET helper cannot be the XPC peer within the sandbox, or Finder behaviour needs private API.

## Estimate after a successful spike
Core features 4-6 weeks (enumerate, fetch, create/modify/delete, conflicts, change notifications, Keychain, menu bar, reuse of the settings page); packaging, notarization and QA 2-4 weeks. One developer, part time: 2-3 months to beta.

## Cheap preparation done
macOS build-and-test job in CI (`build-test-macos`), portable `FileTokenStore` (0600, atomic), this plan. Not done on purpose: moving classes out of the Windows engine project (risk to the Windows beta); do it as the first step of the port.
