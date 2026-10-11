# Vortnux desktop, API, testing and release plan

Prepared 2026-10-11. Stack decision: C# + Avalonia, with **Native AOT as the preferred release target from the first milestone**. This extends [implementation-plan.md](implementation-plan.md), including its Cyberpunk deployment, removal, collection and game-adapter requirements.

## Outcome

Deliver a Linux gaming desktop application that downloads and manages mods and collections, preserves game files during uninstall, reports background work clearly, and navigates from a notification to the exact relevant item. Ship a portable AppImage and an installable Arch package through tested GitHub releases. Support Windows later using the same core.

The first complete integration target is Cyberpunk 2077 through Steam/Proton. A successful release must demonstrate individual mod installation/removal and one pinned real collection in the game. Broader game compatibility remains an adapter-by-adapter commitment.

This document originally described the planning task. Implementation has since started; see [implementation-status.md](implementation-status.md) and the repository README for the delivered scope, verification and remaining milestones.

## Chosen architecture

Use a pinned supported .NET LTS SDK, C#, Avalonia, compiled XAML bindings, MVVM, and SQLite. Resolve and lock exact dependency/tool versions in the first implementation milestone. Design for Native AOT immediately; milestone 0 must publish and run the real GUI and generated-client fixture path as native binaries. AppImage and Arch release jobs package that AOT output. Regular managed builds remain useful for development and unit tests, but do not substitute for the native release gate.

Proposed projects:

- `Vortnux.Core`: typed identities, install plans, dependency/conflict rules, operation state machines, stable notification destinations.
- `Vortnux.Application`: download/install/remove/profile/collection operations, progress, policy and cancellation.
- `Vortnux.Infrastructure`: SQLite, credentials, filesystem journal, archive extraction, Nexus provider, release verification and platform adapters.
- `Vortnux.Nexus.Generated`: repeatably generated OpenAPI client and transport DTOs, isolated from business models.
- `Vortnux.Games.Cyberpunk2077`: discovery, Proton context, archive layouts, frameworks and REDmod deployment.
- `Vortnux.Presentation`: typed view models, navigation and notification history.
- `Vortnux.Desktop`: Avalonia views, platform notifications, tray/lifecycle, composition.
- `Vortnux.Cli`: diagnostics and fixture installation commands using the same application operations.
- `Vortnux.UpdateHost`: small separately AOT-published helper for AppImage replacement, health acknowledgement and recovery.
- Separate core, infrastructure, presentation, headless UI, packaging and update integration test projects.

Keep state under the user's XDG config/data/cache locations. The installed executable is immutable. Game/staging locations are configurable. Credential-store integration is separate from persisted account identity; release logs and diagnostic exports redact secrets. [ADR 0001](adr/0001-nexus-api-key-storage.md) selects Linux Secret Service via `secret-tool`, verified legacy-file migration and no plaintext fallback. Persistent connections require `libsecret` and an available, unlocked keyring; package metadata and CI install the CLI dependency. See the [credential guide](credentials.md) for account controls and recovery.

## Native AOT design and acceptance

Avalonia documents Native AOT support. It compiles the executable ahead of time and avoids requiring an installed .NET runtime; it does not remove all Linux native-library or desktop dependencies. Choose the exact stable Avalonia version only after proving its GUI, accessibility and native integration on the target machine.

Release settings include `PublishAot=true` in Desktop/Cli/UpdateHost, `IsAotCompatible=true` in production libraries, compiled bindings and source-generated serialization. Enable `TreatWarningsAsErrors`, `ILLinkTreatWarningsAsErrors` and `IlcTreatWarningsAsErrors`; keep trimming/AOT analysis warnings visible. Validate those settings against the pinned SDK. Do not turn off analyzers or broadly root every assembly to silence failures.

Architectural constraints:

- Compile views/XAML at build time. Bindings use declared data types. Use explicit view factories and service registration rather than reflection-based view locators, assembly scans or runtime code generation.
- Use `System.Text.Json` source-generated contexts and metadata-based serialization/deserialization overloads for API DTOs, collection manifests, database JSON fields, notifications and signed update metadata. Disable default reflection serialization even in managed development builds so missing metadata fails early.
- The OpenAPI generator runs at build/development time and does not itself need to be AOT-compatible. Its **generated runtime code does**. Selecting System.Text.Json alone does not prove this; generic reflection-based overloads may still produce IL2026/IL3050 failures.
- Treat NSwag as the initial generator candidate. Prove it can emit a usable client with source-generated metadata, potentially with small versioned generator templates. If its stock output fails, fix configuration/templates or select a demonstrated compatible generator. Never hand-edit generated code, suppress the warning broadly, or silently switch the shipped app to JIT. Record this decision in milestone 0.
- Validate the SQLite/native interop provider, archive library, D-Bus notification backend, credential-store adapter and signature verifier under AOT. Prefer explicit SQL mappings over a reflection-heavy ORM, explicit D-Bus proxies over runtime proxy generation, and pinned native interop bindings. A compatibility declaration alone is insufficient.
- Compile game adapters into releases using an explicit registry. New adapter code arrives through a signed application release. Safe declarative game recipes can evolve later; arbitrary downloaded .NET extension assemblies cannot be assumed loadable in a Native AOT app. Preserve the documented limit on Vortex extension compatibility.
- Keep game images and other downloaded assets as data; avoid runtime XAML/code loading. Dynamic themes must use supported compiled resources and tested property updates.

Publish on the target OS with native compiler prerequisites, starting with Linux x64. Measure startup time, idle memory and artifact size on the actual package instead of claiming a fixed AOT improvement. Preserve matching native debug symbols as separate diagnostic artifacts.

Required native-binary scenarios: construct every production view, exercise each route/notification target, serialize/deserialize real-shaped Nexus/collection/update fixtures, open/migrate SQLite, extract/install/remove fixture archives, call the desktop notification backend, and run update verification/recovery. Run these on the actual published AOT output; ordinary unit tests and successful compilation cannot prove trimming preserved all required runtime behavior.

## Verified Nexus API findings

Source: <https://api.nexusmods.com/openapi.yaml>. The inspected document is OpenAPI **3.0.3**, API version **3.0.0**, with server `https://api.nexusmods.com/v3`. Its downloaded size was 129,433 bytes and SHA-256 was `51218dcafc715a236fbebe884a25d3ac8e517be0aca61fb41345633c9ce628f1`.

The root security alternatives are a personal API key in the `apikey` header and a bearer JWT. The schema explicitly links personal keys to the Nexus API-key settings page. Token acquisition/refresh is a separate concern; declaring bearer security does not supply an OAuth login flow. Some public endpoints override authentication.

| Need | What this specification contains | Implementation consequence |
| --- | --- | --- |
| Mod metadata | GET by game domain and game-scoped mod ID; file/version lookups | Generate transport methods and map provider identities explicitly |
| Dependencies | Version dependencies, ranges, DLC and batch materialization | Investigate semantics and account access; useful for solving exact compatible dependencies |
| Download | POST `/mod-file-versions/{id}/download-repacked`, returning `download_url` and `expires_at` | Marked Experimental; validate eligibility and repacked archive behavior before using it |
| Collections | PATCH a collection, POST create collection, POST create revision | No collection read/revision read/download operation is present in this snapshot |
| Game discovery | GET `/vortex/extensions` and POST `/vortex/detected-games` | Experimental metadata helpers; not proof that Vortnux can execute those extensions |
| Account and ordinary download flow | Not described by this snapshot | Verify complementary APIs rather than inventing missing operations |

Many consumer-oriented endpoints are Experimental. The spec defines that badge as potentially changing or disappearing. It is useful and supports code generation, but it is not a complete replacement for the collection and download contracts assumed by the Python scaffold.

Gate 0 must establish personal-key account access, a real downloadable mod and a readable pinned collection revision. Use the existing REST v1/GraphQL v2 behavior only after validating it against current primary documentation and actual responses. Keep each protocol behind `INexusProvider`. Preserve free-account browser assistance and Premium-eligible automation; generation does not bypass account permissions.

The runtime application needs consumer operations; collection publishing, upload and editing methods are outside this PoC even if the generator emits them.

## Generate NexusModsClient like Rider

Yes: use a pinned **NSwag** local .NET tool, rather than requiring an IDE button. Rider and CI can invoke the same repository command. The proposed name is `NexusModsClient`.

Implementation steps:

1. Store a reviewed upstream schema snapshot in `api/nexus/openapi.yaml`, with source URL, fetch date and hash. Do not fetch mutable upstream YAML during ordinary builds.
2. Pin `NSwag.ConsoleCore` in `.config/dotnet-tools.json` and configure `api/nexus/nswag.json`. Verify the selected version reads this YAML correctly; if conversion to JSON is necessary, pin that conversion tool and preserve semantics.
3. Generate async methods, typed DTOs, cancellation-token overloads, nullable reference types and an injectable `HttpClient`. Let application composition own the client's lifetime. Generate source-serialization metadata for the reachable request/response/error shapes and use AOT-safe overloads. Verify serializer and nullable behavior using both managed tests and published native fixtures.
4. Keep generated files committed for review and predictable IDE builds. A repository regeneration script calls the pinned tool; CI regenerates into temporary output and rejects unexplained diffs.
5. Add an authentication/application-header HTTP handler and a separate provider facade for retry policy, error translation and model mapping. Do not edit generated methods manually.
6. Use a separate downloader transport for CDN URLs; API credentials must not be forwarded to downloads or arbitrary redirects. Preserve signed URL expiry semantics and redact URLs where they contain authorization.
7. Validate documented and undocumented error paths, including RFC 9457 Problem Details, 401/403/404/429, malformed data, timeouts, cancellation and additive response fields. Only retry operations known to be safe; do not blindly retry POST operations.

The intended developer workflow is `dotnet tool restore` followed by `dotnet tool run nswag run api/nexus/nswag.json`. Exact settings and invocation will be verified against the pinned generator during implementation. Expose the regeneration script as a Rider run configuration if helpful.

Schedule upstream schema checks that open a reviewable update proposal. A schema change must pass generation, compilation and transport contract tests before adoption. Compiling generated code proves type consistency with the snapshot; it does not prove live endpoint behavior or eligibility.

## Warnings are errors

This is a release invariant, covering application code, tests, generated code and tooling:

- Set `TreatWarningsAsErrors=true`, nullable checking, code-style enforcement, deterministic compilation and a deliberate analyzer configuration in repository-wide build settings.
- Treat linker and native-compiler diagnostics as errors too (`ILLinkTreatWarningsAsErrors=true`, `IlcTreatWarningsAsErrors=true`). IL2026/IL3050 compatibility problems must be fixed or meet the same narrow documented exception policy.
- Invoke MSBuild with `-warnaserror` for restore/build/publish gates as well: the project property alone primarily covers compiler warnings, not every build task.
- Keep compiled XAML bindings explicitly enabled and declare data types on views/templates.
- Pin SDK/tool versions so analyzer behavior changes through reviewed upgrades.
- Do not use broad `NoWarn`, `WarningsNotAsErrors`, global analyzer disablement or blanket `#pragma warning disable` to make CI green.
- Do not treat generated code as an automatic exemption. Inspect generator-emitted pragmas and generated-code analyzer behavior. Prefer correct nullable/settings/template output; track any remaining specific exclusion explicitly.

An exception requires a named warning/advisory, narrow file/line/package scope, technical reason, evidence that fixing/upgrading is presently unsuitable, owner, review date/removal condition and a test where relevant. Store this in `docs/warning-exceptions.md` and validate the suppression inventory in CI. Reject newly introduced undocumented exclusions, including project properties, `.editorconfig` severities, attributes and generator templates. A justified exclusion is permitted by the user's instruction; none is pre-approved by this plan.

MyWorkHub's warning configuration demonstrates the desired policy, but its advisory exceptions are project-specific and must not be copied into Vortnux.

## Gaming behavior and lifecycle

The application centers on the active **game + installation + profile**. Show whether changes are downloaded, staged, deploying, ready, blocked or failed. These states must come from operation results, not from button timing.

- Detect the configured game process and launcher/Proton context; document detection limits. Recheck immediately before writes and serialize mutations per installation.
- While the managed game runs, downloads may continue with configurable concurrency/bandwidth. Deployment, removal, profile switching that changes deployed files, REDmod work and updater restart wait until the game is closed. A queued action remains visibly queued.
- Add a gaming/quiet mode: mute sound, suppress nonessential popups, batch collection notifications and retain failures in notification history. Do not steal focus when work completes.
- A collection emits one final success notification after all required installation/deployment/verification steps succeed. Optional omissions are listed; blocked required steps cannot be called successful. Download-only completion is a distinct event.
- Minimize-to-tray/keep-running behavior is explicit and configurable. Closing the application offers keep working, safe cancellation or exit as appropriate. Tray support varies by desktop; retain normal-window access when unavailable. Autostart is optional and user-controlled.
- On restart, reconstruct queued/recoverable operations from the journal. Do not blindly restart an interrupted write or duplicate a completion notification.

The GUI includes a game library, mod list/detail, collection revision detail, downloads, profiles, conflict resolution, activity history and settings/update page. Provide keyboard navigation, accessible labels, clear progress, cancel/retry actions, high-DPI layouts and a usable narrow-window layout.

## Notifications: adapt the MyWorkHub design

Reference implementation inspected:

- `../MyWorkHub/docs/adr/0004-element-level-deep-linking.md`
- `../MyWorkHub/src/MyWorkHub.Core/Navigation/NavigationTarget.cs`
- `../MyWorkHub/src/MyWorkHub.UI/Notifications/ToastNotificationService.cs`
- `../MyWorkHub/src/MyWorkHub.Presentation/ViewModels/NotificationEntry.cs`
- `../MyWorkHub/src/MyWorkHub.Presentation/ViewModels/NotificationCenterViewModel.cs`
- `../MyWorkHub/src/MyWorkHub.Presentation/Navigation/NavigationService.cs`
- `../MyWorkHub/src/MyWorkHub.UI/Behaviors/ScrollIntoViewBehavior.cs`
- `../MyWorkHub/tests/MyWorkHub.UI.Tests/NotificationCenterViewModelTests.cs`

MyWorkHub records the same entry in toast and bell history, activates through one command, navigates to a structured target, then scrolls/highlights the requested row. UI updates marshal through Avalonia's dispatcher. Pending focus requests survive initial async data loading. Repeated activation can re-highlight the same row.

Adapt those mechanics locally; do not add a runtime dependency on the sibling repository. The UI-independent pattern is the reference, not an unreviewed wholesale copy. In particular, Vortnux requires persisted history and game/profile context, whereas MyWorkHub's current history is session-only.

Use a serializable, versioned destination with stable route/entity identities rather than persisted CLR view-model `Type` names or arbitrary `object` parameters. A destination contains route kind, game installation ID, profile ID, entity ID and optionally operation ID. Page-specific factories construct valid destinations. Map these to concrete view models inside presentation.

Activation sequence:

```text
toast / bell-history entry / desktop action
           ↓
shared notification activation service
           ↓
select game and display profile context
           ↓
open destination page → load data → reveal/select/highlight entity
           ↓
mark notification read and update unread count
```

Opening a different profile's context must not deploy or change the active gameplay profile as a side effect. If confirmation is needed for a mutating profile switch, notification navigation still opens the informational context first.

| Event | Click destination |
| --- | --- |
| Mod installed/updated | Exact mod version in its game/profile context |
| Collection ready | Exact installed revision, with completion summary |
| Download failed | Failed queue entry and available retry/re-authorize action |
| Dependency/conflict blocked | Specific dependency/conflict in that operation |
| Mod/collection removed | Historical operation detail; removed entity is not assumed to still exist |
| Game deployment queued | Queued operation and reason |
| Application update available | Release/version detail in the update page |

Persist bounded history, read state and operation/event IDs. Deduplicate by logical completion event. Compute badges from actual retained unread entries; distinguish read from dismissed. Handle deleted entities and unavailable profiles with an explicit historical/fallback destination, rather than navigating to the wrong similarly named item.

Three delivery surfaces share one destination:

1. Avalonia in-window toast when appropriate.
2. Persistent in-app bell/history with running operations and cancellation.
3. Linux native notification through the freedesktop D-Bus interface when minimized/backgrounded, using daemon capabilities and action IDs mapped to local notification IDs.

Native desktop action support is optional in the protocol. Probe capabilities; unsupported action/activation handling falls back to the in-app history. Handle daemon restart, duplicate callbacks and compositor activation tokens. Do not promise that every daemon can reopen an application after full process exit. Prove supported lifecycle behavior on the user's desktop; normal background mode keeps the process available. A validated cold-start `vortnux://notification/{id}` route and single-instance forwarding support launches where desktop integration permits them. Neither route may execute arbitrary commands or accept a supplied view-model type.

## Testing every supported behavior

Every implemented requirement must have a named automated acceptance test or a documented real-system validation where automation cannot establish it. Maintain `docs/test-matrix.md`, mapping each requirement to its tests, fixtures, supported platforms and remaining manual evidence. Avoid claiming complete correctness from a coverage percentage.

| Layer | Required checks |
| --- | --- |
| Core | Install plan determinism, dependency solving/cycles, conflict precedence, shared ownership, profile and collection removal/update semantics |
| Filesystem/infrastructure | Original hash restoration; overlap removal in both orders; path/case/link traversal; corrupt/oversized archives; disk/permission errors; external edits; restart after failure at each journal/write boundary |
| Nexus transport | Generated method paths/headers/JSON; nullable/missing fields; Problem Details; 401/403/404/429; signed URL expiry; credentials not leaked to CDN; cancellation; deterministic regeneration |
| Downloads | Partial resume validators/ranges; server ignoring Range; changed object; retry limits; pause/cancel/restart; cache hash mismatch |
| Collections/Cyberpunk | Recorded manifests with choices/rules/external resources; required unsupported feature; precise revision pinning; real archive layout fixtures; frameworks and REDmod preflight |
| Presentation | Operation transitions, progress/cancellation, queued gaming behavior, notification dedup/read/badge rules, stale destinations, route validation |
| Headless GUI | Real composition: complete install → activate toast/history → correct game/profile/page/row; async loading; filtered/virtualized/offscreen row; repeat activation; keyboard focus; visible error/retry state |
| Visual | Fixed fonts/viewport/theme/DPI screenshots for primary pages and dialogs; review differences; ensure focus/highlight is actually visible |
| Published Native AOT | Every production view and navigation target, source-generated serializer roots, SQLite/archives/native interop, release verification and recovery exercised using the actual native output |
| Native desktop | D-Bus notify/actions/activation using a fixture service, then real-daemon checks; tray; file picker; `nxm://` and app routes; single-instance forwarding; X11/XWayland and target Wayland desktop |
| Packages | AppImage normal and extract/run modes; clean Arch install/upgrade/remove; desktop/icon/protocol registration; native dependency checks; executable launch without installed .NET |
| Updater | Valid update; invalid/unknown signature; tampered/wrong-arch asset; stale metadata; offline/rate limit; disk full; interrupted download/replace; health timeout; DB migration failure; rollback; pacman-owned binary never overwritten |

Use an injectable clock and deterministic fixtures. Keep recorded API tests offline and credentials out of pull-request CI. Optional live contract checks use dedicated authorized credentials in protected jobs and expose only sanitized results.

Run core/presentation tests on Linux and Windows. Headless tests exercise Avalonia controls; real Linux window tests run under a virtual display and a session D-Bus fixture. Hosted runners do not prove Hyprland/GNOME/KDE notification behavior; the target desktop smoke test remains a release acceptance item until automated coverage exists.

Require high branch coverage of deployment/recovery/update state machines and document uncovered branches; assess actual bug-catching behavior with fault injection. Visual comparisons are supplemental. Before declaring the PoC complete, launch real Cyberpunk with the selected mods/collection and record game/mod/tool versions. Redistribute mod fixtures only where allowed; otherwise use synthetic archive trees and locally supplied real test files.

## CI and continuous delivery

Reuse MyWorkHub's version metadata and native publishing ideas, and add explicit test/package/update gates. Do not copy its release workflow as the full Vortnux pipeline: the inspected workflow publishes binaries but does not contain the gates defined here.

Proposed workflows:

- `ci.yml`: every PR/push; pinned toolchain; locked restore; generation-drift check; formatting verification; warning-as-error compilation; offline tests; headless GUI; Linux Native AOT publish/run gate; suppression inventory; uploaded failure diagnostics.
- `package-validation.yml`: PR/main packaging builds; AppImage and Arch package smoke tests and N−1 upgrade checks. Upload candidate artifacts without publishing releases.
- `release.yml`: `vX.Y.Z` tags from an eligible commit; verify version/tag agreement; rerun required gates; build final packages; verify those final bytes; sign checksums/update metadata/packages; produce provenance; assemble a complete draft release; publish only after every required artifact/test succeeds.
- `upstream-contract.yml`: scheduled/manual schema diff and optional credentialed contract probes; propose reviewed updates without mutating production dependencies automatically.

Keep ordinary jobs read-only. Only protected release jobs get narrowly scoped release/signing/attestation permissions. Pin third-party actions and packaging binaries to immutable revisions/digests; signing credentials are never exposed to untrusted PRs. Declare concurrency rules so stale PR runs cancel, while active release publication completes consistently.

Build Native AOT `linux-x64` on an oldest-supported compatible Linux baseline chosen for the pinned .NET/native libraries. Choose and document the supported distro/glibc floor from the packaging spike, then test newer Arch and representative other Linux systems. Pin build container digests and native compiler versions. Additional architectures require their own build and package evidence; use corresponding target-OS runners rather than assuming cross-OS AOT compilation.

Separate unsigned build artifacts from signed final artifacts; inspect provenance and final hashes. A deterministic compiler does not imply an entire signed AppImage is byte-for-byte reproducible. Record exact SDK, schema, generator, native packaging tools, source commit, dependencies and test evidence.

Version in the app, file names, package metadata, release tag and updater manifest must agree. Avoid a workflow that silently releases on every main push or bumps source versions after publication. Publishing a real GitHub release/AUR package is a later implementation action; this task only defines the workflow.

## AppImage delivery

Publish the Native AOT executable and its required native libraries into an AppDir:

```text
Vortnux.AppDir/
  AppRun
  vortnux.desktop
  vortnux.png
  usr/lib/vortnux/<native executable and required native libraries>
  usr/share/icons/hicolor/...
  usr/share/metainfo/...
```

Use a pinned `appimagetool` to create a type-2 AppImage. This is .NET/Avalonia packaging; Qt-specific deployment tools are unnecessary. Validate desktop/AppStream metadata, runtime entrypoint, execute permissions, spaces in paths and extraction behavior. Do not bundle incompatible host drivers or assume that Native AOT bundles glibc/display dependencies. Include the AOT update helper and verify it remains runnable after the original AppImage mount exits, for example by staging the helper to a protected user-owned update directory.

Include desktop entries, icons and `nxm`/application URI handling, but enable user-level integration through an explicit setting. Portable AppImage moves can invalidate absolute-path launcher entries; detect and repair them with consent or explain the needed repair. Support the documented extract-and-run fallback where FUSE is unavailable.

Required release assets:

- `Vortnux-X.Y.Z-linux-x64.AppImage`
- `Vortnux-X.Y.Z-linux-x64.tar.gz` (diagnostic/portable directory distribution)
- Checksums, signed update manifest, detached signatures and release provenance/SBOM.
- Optional `.zsync` metadata if the delta-update spike is worthwhile. Full verified download is the initial update mechanism; zsync is not required for correctness.

Do not assume an embedded AppImage signature is verified automatically on execution. The update verifier must validate the chosen detached signing format and trusted signer itself.

## Arch package delivery

Provide a versioned `PKGBUILD`, generated `.SRCINFO`, and built `vortnux-bin-X.Y.Z-1-x86_64.pkg.tar.zst`. Start with a binary package assembled from the same Native AOT publish payload; this minimizes local toolchain requirements. A separate source-build recipe can follow if needed and must declare the .NET/native build toolchain.

Install application files under `/opt/vortnux`, a launcher under `/usr/bin/vortnux`, and desktop/icons/AppStream/license metadata in the standard shared locations. Keep all user data and game changes outside pacman-owned paths. Derive actual runtime dependencies by inspecting the shipped native libraries and testing a clean Arch environment; do not guess them from the developer machine.

Pin release sources/checksums in the recipe. Verify upstream artifact signatures with full signing fingerprints where applicable. Build in a clean Arch environment with `devtools`/`makechrootpkg`; run `namcap` on recipe and package and investigate findings. Some native-library payloads may need `!strip`; document and verify that choice if used.

Acceptance checks: install with `pacman -U`, launch, integrate protocols, upgrade from N−1, uninstall, and verify user profiles/downloads/game files are preserved. Package removal and mod removal are distinct operations. Deliver a package signature and document trust setup without advising users to disable signature checking.

Two subsequent distribution routes:

- Publish a reviewed `vortnux-bin` recipe to AUR, enabling normal AUR-helper updates.
- Optionally provide a signed pacman repository later for native `pacman -Syu` updates. A package uploaded to GitHub alone does not create a pacman update repository.

The GUI detects package provenance from build metadata and install path, reports new releases, and links to the appropriate update instructions. For pacman-owned installations it must never replace `/opt` or `/usr` files itself. No embedded updater invokes sudo automatically.

## Safe updates from GitHub

Portable AppImage updates use an explicit **Check → Download → Verify → Install on restart** flow. Automatic checks can be enabled; default application/restart remains user-controlled. Stable and prerelease channels are separate, with stable excluding prereleases.

Initial trust design: a pinned release-signing public key bundled in the application, a detached signature over exact manifest bytes, and SHA-256 hashes in that signed manifest. The final cryptographic format/library is selected and tested in the update spike. Key changes require authorization by the existing trusted key; never trust a replacement public key supplied only alongside the new download. GitHub provenance is additional evidence, not an excuse to omit local verification.

The manifest identifies app, channel, version, release/source commit, asset file/size/hash, OS/architecture, schema compatibility, updater minimum version and expiry. Validate identity, bounded size, intended release location, version policy and signature before download/replacement. Use HTTPS and handle rate limiting/offline service without blocking startup. A hash fetched next to an unsigned download detects corruption but does not independently authenticate the release.

AppImage replacement sequence:

1. Download into a temporary file on the destination filesystem; allow safe retries/resume and verify final size/hash/signature metadata. Leave the current executable intact.
2. Verify writable user-owned install location, immutable package provenance, free space and no active deploy/remove/migration. Defer while the managed game is running or application work cannot safely quiesce.
3. The running app acquires update/state locks, flushes state and journals, and creates a consistent settings/DB backup. Checkpoint SQLite WAL or use its backup mechanism; do not copy only a live database file.
4. Run the separate update helper, exit the old process, and persist an update journal. Replace the user-owned executable with same-filesystem atomic renames, retaining the previous version and verified candidate. Resolve symlinks explicitly and verify executable permissions. Never write inside the mounted AppImage filesystem.
5. Start the new app with an update transaction ID and bounded readiness handshake. Check version, database access and essential GUI/services. Readiness must not deploy mods or launch the game.
6. Acknowledge success and retain one known-good backup; clean older temporary files only afterward. A crash/timeout/failing migration triggers rollback while neither process holds the state lock. Restore the old executable and matching DB/settings backup before reopening it.
7. If the helper itself is interrupted, its durable journal allows deterministic recovery on next launch; provide a documented manual recovery route too. Recovery must not undo unrelated game file changes.

Use additive/backward-compatible DB migrations initially. Test every supported N−1 upgrade path. For a future irreversible migration, explicitly limit rollback or build a compatible migration/restore strategy; replacing only the executable is insufficient. Before commit, keep the new process in limited update-readiness mode so it cannot write new gameplay state that would be lost during rollback.

Reuse the same versioned data backup/compatibility design for package upgrades, but let pacman manage the binaries. Application self-update and mod updates have separate operation models and notifications.

## Milestones and acceptance

| Milestone | Output | Completion evidence |
| --- | --- | --- |
| 0. Contracts and toolchain | Selected SDK/Avalonia/generator versions; AOT-compatible generated client spike; API coverage/access report; native GUI shell; first AOT AppImage/Arch feasibility builds | Native publish passes with compiler/linker/AOT warnings as errors; native GUI and client/SQLite fixtures run; real account/mod/collection access verified or a precise blocker recorded; both packaging formats launch |
| 1. Reliable core | Local archive lifecycle, ownership, journal and recovery | Install/remove restoration and failure-injection tests pass; no success on partial deployment |
| 2. Gaming shell and notifications | Game/profile context, operation history, MyWorkHub-style activation, native backend, quiet mode | Automated correct-element navigation tests; target desktop action smoke; game-running mutations queue |
| 3. Cyberpunk and Nexus | Steam/Proton adapter, supported framework layouts, download queue and protocol integration | Packaged app installs/removes a real mod; recorded API failure cases pass; real game launch works |
| 4. Collections | Pinned revision install, choices/rules/dependencies, progress/resume/update/remove | Supported real collection launches; missing/unsupported required instructions block; shared mods survive removal |
| 5. Packaging and release automation | Reusable CI gates, final AppImage/Arch package and signed release metadata | Final-byte smoke tests; clean Arch N−1 install/upgrade/remove; no unjustified warning suppression; draft release completeness check |
| 6. Safe update | GitHub checks, verification, user-owned AppImage replacement, data rollback, package-aware instructions | Success/tampering/interruption/migration/health-failure tests pass; previous version recovers; pacman paths never overwritten |
| 7. PoC release acceptance | Usable GUI, documented installation/update/recovery and test matrix | End-to-end packaged collection install → actionable notification → correct item → real game → collection removal; all required release checks green |

Milestones 0–7 complete the requested Cyberpunk PoC and distribution/update foundation. Do not assign a reliable delivery date until the account/collection contract and package spikes resolve the primary unknowns.

## Documentation checked

Context7 was used for Avalonia, .NET, AppImage, the follow-up NSwag question, and the subsequent Native AOT requirement. Primary web documentation supplemented it for Linux notifications, Arch tools, GitHub provenance and Native AOT. ArchWiki returned an access challenge; Arch's official manual pages were readable instead.

- [Nexus OpenAPI source](https://api.nexusmods.com/openapi.yaml)
- [NSwag CLI/configuration](https://github.com/ricosuter/nswag/wiki/CommandLine)
- [NSwag client generation settings](https://github.com/ricosuter/nswag/wiki/CSharpClientGeneratorSettings)
- [.NET warning handling](https://github.com/dotnet/docs/blob/main/docs/csharp/language-reference/compiler-options/errors-warnings.md)
- [Avalonia headless tests](https://github.com/avaloniaui/avalonia-docs/blob/main/docs/testing/headless-xunit.md)
- [Avalonia Native AOT](https://docs.avaloniaui.net/docs/deployment/native-aot)
- [.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [System.Text.Json source generation](https://github.com/dotnet/docs/blob/main/docs/standard/serialization/system-text-json/source-generation.md)
- [.NET linker warning policy](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options)
- [.NET native compiler warning integration](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/BuildIntegration/Microsoft.NETCore.Native.targets)
- [AppImage AppDir packaging](https://docs.appimage.org/packaging-guide/manual.html)
- [AppImage signatures](https://docs.appimage.org/packaging-guide/optional/signatures.html)
- [AppImage FUSE fallback](https://docs.appimage.org/user-guide/troubleshooting/fuse.html)
- [Freedesktop notification actions and activation](https://specifications.freedesktop.org/notification/latest/protocol.html)
- [Arch PKGBUILD](https://man.archlinux.org/man/PKGBUILD.5.en)
- [Arch clean package builds](https://man.archlinux.org/man/makechrootpkg.1.en)
- [Arch package validation](https://man.archlinux.org/man/namcap.1.en)
- [Arch pacman operations](https://man.archlinux.org/man/pacman.8.en)
- [GitHub artifact provenance](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)
