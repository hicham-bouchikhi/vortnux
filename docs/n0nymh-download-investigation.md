# CET+Essentials collection download investigation

Checked `https://www.nexusmods.com/games/cyberpunk2077/collections/n0nymh` with curl on 2026-10-11. No game files were changed.

Nexus returned published revision 49 with 11 mods and resolver `/v2/collections/86397/revisions/698560/download_link`. Authenticated curl received HTTP 200 from the resolver and downloaded its 1,512-byte 7z archive with HTTP 200. Its host was `premium-files.nexus-cdn.com`. Account validation returned HTTP 200 and Premium enabled. The pinned No Intro Videos file resolver also returned HTTP 200, including both Nexus CDN and nexusmods.com mirrors. API keys were passed through private pipes, never saved in files or sent to the archive CDN. Signed download URLs and account identity are omitted from the evidence.

The screenshot error comes from `NexusApi.ValidateDownloadUri`, which previously accepted only `nexusmods.com` and its subdomains. It now also accepts `nexus-cdn.com` and its subdomains, retaining HTTPS, port and user-info restrictions. Regression tests cover collection resolution and fetching through premium/supporter CDN hosts, absence of API-key headers on archive requests, and rejection of impersonated/unsafe addresses.

The CDN fix alone does not make this collection installable. Its downloaded manifest has three features rejected by `Collections.Parse`:

- `collectionConfig: { "recommendNewProfile": false }`: the top-level allowlist rejects this configuration object. Support requires recognizing its schema and rejecting unknown meaningful settings.
- `info.gameVersions: ["2.3.1.0"]`: the parser rejects version metadata. Support requires presenting/validating the declared version against the selected game installation.
- All mods have `source.updatePolicy: "latest"`: the parser accepts only absent or `exact` policies. Support requires either latest-file selection and identity verification, or explicitly offering a pinned revision installation using the existing file IDs, sizes and MD5 values.

All manifest MD5 values have valid lengths. The public revision has no extra installation instructions or external resources. Archive extraction works with installed 7zip. Installation, all 11 mod downloads, and game launch remain unverified. The running packaged app has not been rebuilt/replaced.

For another check outside the restricted agent sandbox, run:

```bash
python3 tools/check_nexus_collection.py n0nymh
```

It asks for the API key with hidden input, uses curl to validate the account and resolve/download the archive, then reports manifest features. It deletes its temporary archive and does not install mods.

Sanitized evidence: `artifacts/test-results/n0nymh-download-check.json`. Build: zero warnings/errors. Collection resolver tests: 30 passed. Full suite: 147 passed; one unrelated launch/IPC test failed because the sandbox prohibits socket binding.

Official resolver implementation: https://github.com/Nexus-Mods/node-nexus-api/blob/master/src/Nexus.ts (`getCollectionDownloadLink`). Collection CDN examples: https://github.com/nexus-mods/vortex/issues/21435 and https://github.com/nexus-mods/vortex/issues/20857.
