# Vortnux implementation plan

Prepared 2026-10-11. Scope: a packaged desktop mod manager with a usable GUI, starting with Cyberpunk 2077 and progressing toward Vortex game and collection compatibility.

**Stack selected by the user:** C# + Avalonia, with Native AOT as the preferred release target. The detailed follow-up requirements, verified Nexus OpenAPI findings, MyWorkHub notification design, test gates, AppImage/Arch releases, and update/rollback design are in [desktop-release-plan.md](desktop-release-plan.md). That document supersedes the stack comparison as the implementation decision; this document retains the mod deployment and game-adapter requirements.

## Recommendation and assumptions

Use C# on a supported .NET LTS release, Avalonia with compiled XAML bindings, and SQLite for local state. Build Linux x64 first, including Steam/Proton, and Windows x64 from the same core. Pin the SDK and all dependencies after an initial compatibility spike. Package early and test the actual executable throughout development.

This recommendation prioritizes development speed together with useful compiler feedback. It is an engineering judgment, not a benchmark result. Rust provides stronger memory-safety guarantees; Electron offers faster access to web UI components and closer alignment with Vortex's ecosystem.

The repository currently contains a small Python/PyQt6 scaffold, a Nexus API client, and URL parsing. There is no implemented GUI entrypoint, deployment engine, or test suite. Preserve its useful behavior as requirements and fixtures; validate its assumed REST and GraphQL contracts before porting. A small scaffold does not justify choosing Python when compile-time feedback is a principal requirement.

If immediate parity with every Vortex-supported game is more important than a new architecture, investigate a Vortex fork before building a replacement. Porting game extensions individually is a substantial ongoing project. A new GUI framework does not confer Vortex extension compatibility.

## Four technology proposals

| Proposal | Advantages | Disadvantages | Deterministic feedback | Assessment |
| --- | --- | --- | --- | --- |
| C# + Avalonia + SQLite | One primary language; compiled view bindings; mature filesystem/network APIs; headless control/layout/input tests; Linux and Windows GUI; self-contained deployment | Styling takes more custom work than a web UI; native desktop integration still needs platform tests; Vortex JavaScript extensions need adaptation | C# typing, nullable analysis, analyzers, compiled XAML and typed bindings; headless UI and filesystem tests | Recommended balance for a new implementation |
| Rust + Tauri 2 + strict TypeScript/React + SQLite | Strong core type and memory safety; web styling; small desktop shell using system webviews; Linux/Windows packaging | Two language toolchains; IPC must be explicitly typed and validated; Linux WebKitGTK dependencies and platform rendering differences | Rust compiler, TypeScript checking, generated IPC contracts, Rust tests and WebDriver | Strong choice if core correctness and footprint outweigh iteration simplicity |
| Electron + strict TypeScript/React + SQLite | Fast visual iteration; bundled Chromium; large component ecosystem; close to Vortex's implementation; best candidate for a fork/reuse investigation | Larger runtime footprint; main/preload/renderer boundaries; native SQLite/archive modules may complicate packaging; types disappear at runtime | Strict TypeScript, schema validation, typed IPC, renderer and packaged-app tests | Best for fastest visual delivery or extensive Vortex reuse |
| C++ + Qt 6 Widgets/Qt Quick + SQLite | Mature desktop toolkit; strong platform integration; native executable; Qt Test; Widgets can use generated C++ forms | More boilerplate and native build complexity; memory ownership requires discipline; QML retains runtime behavior; smaller margin for rapid feature iteration | C++ compiler; typed signal connections; generated Widgets forms; QML lint/type tooling; Qt Test | Good native option, lower priority for speed |

All four have enough documentation for a proof of concept. Context7 returned high-reputation documentation sources: Avalonia docs (~36,000 snippets), Tauri website (~13,000), Electron source (~5,000), Qt 6 docs (~145,000), and .NET docs (~85,000). These are index snapshots, not comparative quality or reliability scores.

Context7 queries checked compiled bindings and headless testing for Avalonia; desktop testing, dependencies and packaging for Tauri; context isolation and typed IPC for Electron; generated forms, QML tooling, Qt Test and deployment for Qt; and .NET self-contained deployment. No compiler verifies visual appearance, external APIs, or game compatibility: those need runtime checks.

## Product and GUI

The executable opens a polished desktop application with consistent spacing, typography, icons, keyboard navigation, light/dark themes, clear empty states and accessible contrast.

Primary navigation: Games, Mods, Collections, Downloads, Profiles, Settings. The active game and profile remain visible.

- Games: discover Steam installations, show detected versus supported games, and allow manual selection. Validate the chosen root before installation.
- Mods: paste a Nexus link or import an archive; show version, enabled state, dependencies and conflicts; offer enable, disable, uninstall and update.
- Collections: paste a URL; select a revision; show required/optional mods, downloads, installer choices, unsupported features and progress. Resume interrupted work.
- Downloads: show queue, bytes, progress, pause/resume/cancel and actionable errors. A downloaded archive and an installed mod have distinct states.
- Conflicts: show which mods write each conflicting path and the selected winner. Never silently choose based on download completion order.
- Profiles: switch enabled mods, rules and collection revisions. Launch the game through its configured launcher.
- Settings: game/staging/cache paths, Nexus connection, protocol registration, diagnostics and storage usage.

An installation preview describes affected files and unresolved requirements. Errors state what happened and what the user can do, while diagnostics retain a stable error code and operation ID.

## Architecture

One core serves both the GUI and a small diagnostic CLI. The CLI exists so installation and recovery can be reproduced without desktop automation.

```text
Desktop GUI / diagnostic CLI
            |
Application operations and progress
            |
Typed install plans and dependency/conflict solver
            |
Game adapters | Nexus provider | Archive handling
            |
Journaled deployment | SQLite | archive/staging cache
```

Separate projects: Core, Application, Infrastructure, Games.Cyberpunk2077, Desktop, Cli, and Tests. Core has no GUI or live network dependency.

An adapter describes game detection, validated roots, supported archive layouts, path normalization, mod types, dependency rules, deployment paths, load order and post-deployment work. Version adapters so an install plan records which implementation produced it.

Use typed records for game installation, archive identity, mod version, collection revision, installer selections, profile, dependency, conflict rule and file ownership. Persist changes with schema migrations. Store account secrets only in the Linux Secret Service keyring, with verified migration of legacy plaintext files and no plaintext fallback, as specified by [ADR 0001](adr/0001-nexus-api-key-storage.md). Never include download authorization tokens in logs or collection lockfiles.

## Reliable installation and removal

Pipeline: resolve source → download → verify → extract into staging → classify → solve dependencies/conflicts → preview → journal → deploy → verify → commit.

- Cache archives by content hash. Record provider IDs, exact file IDs, versions and hashes. Resume transfers only when server responses and validators allow it.
- Inspect and validate archive paths before extraction. Reject traversal, absolute paths, escaping links and ambiguous case collisions. Bound expanded size and entry count. Start with ZIP; add tested 7z/RAR support before accepting collections that need them.
- Keep staged source files immutable. Start with copies and same-directory temporary files followed by rename. Add hardlink optimization later, with filesystem and mutation checks.
- Compute final destinations and winners before writing. Validate disk space, writable paths, game process state and current destination hashes. Serialize mutations per game.
- Persist a write-ahead operation journal and backups. SQLite transactions cannot make a whole directory tree atomic; recover interrupted filesystem operations explicitly.
- Track every deployed path, its winning mod, previous owner, installed hash and any backed-up unmanaged original.
- Uninstall recomputes the profile's desired files. If mod A overwrote mod B, removing A restores B. If an original existed, restore it when ownership ends. Removing a collection removes its membership and only unreferenced managed mods; shared dependencies remain.
- If the deployed file changed externally, stop that path's destructive operation and offer an explicit resolution. Do not delete whole game directories or untracked files.
- Runtime-generated caches/configuration need game-specific handling; immutable managed-file restoration does not imply every game-created byte returns to baseline.

## Nexus and collection integration

First establish actual API access using the user's own account and an appropriate application identity. Official Nexus documentation describes app registration for SSO and forbids using keys created for another app. Manual personal-key setup can be explored for the private PoC if permitted by the current API.

Validate account information, one mod's file metadata/download, and a published collection revision. The existing scaffold assumes REST v1 plus GraphQL v2, while the current API documentation also exposes v3. Isolate this in a provider adapter rather than assuming an older contract still works.

Nexus advertises one-click collection downloads as a Premium feature. Plan automatic downloads where account permissions allow them; provide a browser-assisted flow for free accounts, receiving authorized `nxm://` links or importing downloaded files. Prove the actual free-account flow in the API spike. Register the protocol handler with an explicit GUI control, including proper second-instance forwarding and expiry/account checks.

Collection installation must resolve and pin a revision, fetch its metadata/manifest, and preserve:

- Exact mod file IDs, required/optional membership and external resources.
- Installer choices and path mappings.
- Dependency and conflict rules, with cycles/missing files reported.
- Game-specific ordering and post-install steps.

Download completion is not collection completion. Preflight all required features; an unsupported required instruction blocks deployment and explains why. Do not run arbitrary collection-provided scripts.

Create a local lockfile with game/adapter versions, collection revision, selected files, hashes, choices and rules. Export it without secrets. Collection updates produce a diff; removing/updating a collection preserves separately installed or shared mods. Some archived, unavailable or external files require user action.

Do not promise arbitrary Nexus collection compatibility until the current schema and extension-driven installation behavior have been implemented and tested. Start with a pinned real Cyberpunk collection whose features can be fully supported.

## Cyberpunk adapter

First support Steam discovery and manual roots, including Proton prefixes. Validate `bin/x64/Cyberpunk2077.exe`. Keep launcher, prefix and game installation paths distinct.

Implement and test archive placement under `archive/pc/mod`, `r6/scripts`, `r6/tweaks`, `red4ext`, and `bin/x64/plugins` where appropriate for each supported mod type. Handle archives wrapped in an extra directory and Windows path separators without guessing when multiple interpretations exist.

Test standalone archive mods and framework-dependent mods using RED4ext, redscript, CET, ArchiveXL, TweakXL and Codeware as applicable. Dependencies must be exact compatible files where metadata/rules require them; requirements are not reliably inferable from filenames alone.

Add REDmod as a distinct installation/deployment mode with checks for DLC/tools and launcher/prefix behavior. The Nexus Mods App's own Cyberpunk documentation records a Heroic/Linux REDmod limitation; verify this explicitly rather than assuming every launcher works identically.

Reference the Cyberpunk Vortex extension and modding documentation for installation behavior. Code reuse requires checking the specific file's license and dependencies. Use deterministic local fixtures for automation and a real game launch for integration verification.

## Milestones and completion gates

| Milestone | Deliverable | Required evidence |
| --- | --- | --- |
| 0. Feasibility | Pinned stack, executable GUI shell, API and collection contract report | GUI opens on target Linux desktop; one account/mod/collection resolves or access blocker is documented; actual collection features inventoried |
| 1. Local deployment | Import/install/disable/uninstall with journal and ownership | Original hashes restored; overlap restoration works; malicious archives rejected; recovery after interrupted writes |
| 2. Cyberpunk | Steam/manual adapter plus supported mod layouts | Archive mod and framework-dependent mod work in the game; disabling/removing produces the expected managed-file state |
| 3. Nexus downloads | GUI link import, account flow, queue and protocol handler | Authorized real download succeeds; expired link/429/network failures recover; packaged protocol handler launches/forwards correctly |
| 4. Collections | Revision import, choices/rules, dependency preflight, resume/update/remove | One pinned real collection works in game; shared dependency removal works; unsupported instructions cannot appear successful |
| 5. Release polish | Responsive GUI, screenshots, diagnostics and distributables | Clean-machine executable smoke test; installation lifecycle driven through GUI; desktop integration and DPI checks |
| 6. More games | Versioned adapter SDK and support matrix | At least a second game passes its own adapter fixtures and launch validation without changing generic deployment semantics |

Milestone 0 is deliberately early: prove packaging and collection access before investing in a large interface. Milestones 1–5 define the useful Cyberpunk PoC. Schedule estimates should follow the API/schema spike and selection of a real test collection; promising an all-games completion date now would be misleading.

## Compiler, test and release feedback

Explicitly enable compiled bindings globally and declare `x:DataType` for bound views/templates. Enable nullable analysis, relevant analyzers, warnings-as-errors and a pinned SDK/dependency lock. Avoid dynamic/reflection bindings except for reviewed necessities.

The same ordered operation graph, adapter version, rules, file hashes and choices should generate the same sorted deployment plan regardless of download completion order. Store plan snapshots and stable failure codes.

Automated gates:

1. Compile the core and every view; fail on missing bound properties.
2. Test deploy/remove, overlapping ownership, original restoration, dependency cycles, collections and profile changes in temporary game trees.
3. Inject failures after journal/write/rename steps, and test restart recovery, disk/permission errors and external file changes.
4. Use recorded provider responses for repeatable tests. Keep optional credentialed live contract checks separate.
5. Exercise real views with Avalonia Headless for interaction and layout; use Skia rendering for selected screenshots. Pin fonts, scale and viewport for repeatability. Screenshots supplement behavior tests and still need review.
6. Publish and start the actual package on a clean target environment; verify file dialogs, `nxm://`, drag/drop and rendering on the target desktop. Headless success does not prove OS integration.
7. Perform a real Cyberpunk launch and collection smoke test. Record game, mod and collection versions; automated tests cannot guarantee gameplay compatibility.

Distribution: Native AOT Linux x64 executable first, then AppImage with desktop/protocol integration and an Arch package; Windows x64 `.exe` and installer follow. Build and test each platform on its corresponding CI runner. Users should not need Python, Node, or a separately installed .NET runtime. Native OS libraries remain dependencies. Use X11/XWayland initially for broad Avalonia Linux compatibility and validate native Wayland separately. Prove AOT compatibility in milestone 0 and package the tested native output, as detailed in [desktop-release-plan.md](desktop-release-plan.md).

## Expanding toward all Vortex games

Maintain a capability matrix: catalogued, detected, downloadable, installable, collection-compatible, and tested platform/launcher. A game appearing in Nexus's catalog must not automatically appear as fully supported.

Add game families incrementally. Bethesda games need plugin ordering, relevant LOOT integration, FOMOD choices and potentially generated patches; other games may need archive patching or custom deployment tools. Provide a typed adapter contract and reusable installers before inviting third-party game adapters.

Assess a Vortex extension bridge against real representative extensions before promising reuse. Extensions can depend on Vortex events, stores, installers, UI and Windows integrations. A JavaScript runtime alone does not solve this. For broad immediate compatibility, the Electron/Vortex fork route deserves preference over rewriting hundreds of adapters.

## Documentation evidence

These are the primary documentation sources used in the Context7 queries and web verification. Documentation availability is sufficient for the desktop stacks; account-specific API access and collection parity remain implementation feasibility gates.

- [Avalonia compiled bindings](https://github.com/avaloniaui/avalonia-docs/blob/main/docs/data-binding/compiled-bindings.md)
- [Avalonia headless platform](https://github.com/avaloniaui/avalonia-docs/blob/main/docs/testing/setting-up-the-headless-platform.md)
- [Avalonia Linux desktop backends](https://docs.avaloniaui.net/docs/platform-specific-guides/linux)
- [Tauri desktop tests](https://tauri.app/develop/tests)
- [Tauri WebDriver CI](https://tauri.app/develop/tests/webdriver/ci)
- [Tauri Windows installer](https://tauri.app/distribute/windows-installer)
- [Electron context isolation and TypeScript bridge](https://github.com/electron/electron/blob/main/docs/tutorial/context-isolation.md)
- [Electron security and IPC boundaries](https://github.com/electron/electron/blob/main/docs/tutorial/security.md)
- [Qt deployment](https://doc.qt.io/qt-6/cmake-deployment.html)
- [Qt GUI testing](https://doc.qt.io/qt-6/qttestlib-tutorial3-example.html)
- [.NET publishing properties](https://github.com/dotnet/docs/blob/main/docs/core/project-sdk/msbuild-props.md)
- [.NET self-contained Linux dependencies](https://github.com/dotnet/docs/blob/main/core/install/linux-ubuntu-install.md)
- [Nexus API documentation](https://api-docs.nexusmods.com/)
- [Nexus official client: authentication and collections](https://github.com/Nexus-Mods/node-nexus-api)
- [Nexus Premium collection download feature](https://www.nexusmods.com/premium)
- [Vortex game support and extension model](https://github.com/Nexus-Mods/Vortex)
- [Vortex collections event surface](https://github.com/Nexus-Mods/Vortex/blob/master/packages/vortex-api/docs/EVENTS.md)
- [Cyberpunk Vortex extension](https://github.com/E1337Kat/cyberpunk2077_ext_redux)
- [Nexus Mods App Cyberpunk/REDmod documentation](https://github.com/Nexus-Mods/NexusMods.App/blob/main/docs/users/games/Cyberpunk2077.md)

Status: researched implementation plan only. No new executable or functional GUI has been built by this planning task.
