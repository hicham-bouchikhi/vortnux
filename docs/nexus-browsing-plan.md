# Searchable Nexus browsing

Implemented in the browsing increments: both catalog types, name search, four sorts, 24-item pagination, cancellation/stale-result protection, bounded session caching, result-to-download navigation, free-account browser actions, installed-library filters, mod thumbnails and collection cover images. Public queries for both types and image URLs were verified against the live endpoint on 2026-10-11. Expanded details, exact installed-file badges and retained scroll position remain follow-up work. Existing acceptance gates for real-account downloads and a native desktop remain pending.

## Problem and expected behavior

At the time this plan was written, connecting in Settings validated the API key only. Browse is now implemented; see [implementation status](implementation-status.md). Current account persistence follows [ADR 0001](adr/0001-nexus-api-key-storage.md) and the [credential guide](credentials.md). The original baseline listed only Vortnux-managed installations and resolved pasted Nexus URLs; it had no remote catalog or search. Files installed by another manager are still not automatically imported into Vortnux ownership.

Add a Browse route for Cyberpunk 2077. After connecting, offer Open Nexus browser and load a first page of recent mods when Browse opens. Keep Mods as the installed library, with its own local name filter. Browse offers Mods and Collections tabs, search, sorting, pagination, details, and actions that reuse existing staging and deployment.

## API basis and first implementation gate

Context7's official Nexus GraphQL documentation describes `mods` with `ModsFilter`, `ModsSort`, `offset`, `count`, and a `ModPage` containing `nodes` and `totalCount`. Mod filters include `gameDomainName`, `name`, and `nameStemmed`. `collectionsV2` supports `CollectionsSearchFilter`, sorting and the same pagination style; collection filters include `gameDomain`, `name`, and `hasPublishedRevision`.

Source: https://graphql.nexusmods.com/

Before building UI behavior, verify exact filter operator shapes, sorting enums, result fields, visibility defaults and personal-key access against the current official schema. Make a small authenticated read-only acceptance check using the connected session, without logging the key. Documentation establishes search support; live account access has not yet been checked. Report any access restriction explicitly rather than presenting an empty catalog. Do not scrape the website as a silent fallback.

## Delivery sequence

1. **Typed search adapter.** Define source-generated DTOs for requests, mod summaries, collection summaries and paginated results. Add separate mod and collection search methods to the Nexus adapter. Restrict queries to Cyberpunk server-side, validate page bounds, use GraphQL variables for user text, request only necessary fields, and exclude hidden/unpublished content as the verified schema permits. Keep existing API-key host validation and bounded responses. Retain warnings-as-errors and Native AOT compatibility.

2. **Browse UI.** Add Mods/Collections tabs, search box with Enter and Search button, Clear, supported sorting choices, 24 results per page, Previous/Next and total count. Empty search loads a useful recent listing. Show name, author/curator, summary and available metadata, plus explicit loading, no-results, disconnected and error states. Default to explicit searches to control API traffic. Optional thumbnails come after text browsing works, with bounded downloads, host validation and fallback images.

3. **Responsive request handling.** Give catalog requests their own cancellation and loading state rather than the existing operation wrapper that disables all navigation. Cancel superseded requests, use a request generation number to ignore stale responses, and cancel on disconnect or game changes. Preserve query, tab, sort, page and scroll when returning from details. Use a bounded short-lived in-memory cache keyed by account/session, game, query, sort and page; clear it on disconnect. Handle rate limits with a visible retry delay when supplied, without retry loops.

4. **Details and existing install flow.** A mod result opens details and its file list; the user selects a specific file/version before Download & stage. Premium uses the existing direct-download path. Free users get an Open on Nexus action and instructions for an authorized manager link. A collection result opens published revision details, then the existing manifest inspection and batch staging. Searchability is not a compatibility guarantee: unsupported installers and collection rules remain blocked during preflight. Installation notifications continue to target the installed mod or collection.

5. **Installed-library search and clear status.** Add an immediate local name filter and All/Enabled/Disabled choices to Mods, and name filtering to installed Collections. Persist Nexus mod/file identities with backward-compatible state loading so Browse can mark exact installed files and link to their library entries. Do not infer ownership from filenames. If external-manager import is wanted later, design a separate reviewable adoption workflow.

6. **Verification and delivery.** Test adapter query variables, game isolation, pagination, malformed/partial GraphQL responses, permission failures, rate limits, cancellation, stale results and disconnect/cache clearing. Headless UI tests cover search submission, tab/sort/page changes, navigation back, empty/error states, selected file routing, local filtering and installed badges. Test older state fixtures and Nexus identity persistence. Run the existing install/uninstall and notification regressions, locked restore and warnings-as-errors tests, Native AOT publish, native smoke checks and AppImage/Arch packaging checks in CI. Rebuild local deliverables after the feature passes.

## Acceptance checks

- Connect a real account, open Browse and see Cyberpunk results without pasting a URL.
- Search a known mod such as CET or RED4ext, open its details and choose a file.
- Search a published collection and inspect a selected revision before downloading.
- Page and sort without duplicate local entries or stale requests replacing newer results.
- Distinguish no results, missing credentials, denied access and network/rate-limit failures.
- Download, review, install and uninstall a supported mod using the existing ownership system; clicking completion opens its library entry.
- Filter the installed library and retain it after restart.
- Verify on a real desktop and with an actual Nexus account; fixture tests alone do not establish live API acceptance.

## Scope

Keep C# + Avalonia and Native AOT. Deliver text-based mod browsing first, collection browsing next, then installed badges and visual polish. This work does not expand game support, importer compatibility, free-account automatic collection downloading, or ownership of externally installed files.
