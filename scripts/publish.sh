#!/usr/bin/env bash
set -euo pipefail
vortnux_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$vortnux_root"
vortnux_version=${VORTNUX_VERSION:-0.1.0}
[[ "$vortnux_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid version' >&2; exit 1; }
[[ $(git rev-parse --is-shallow-repository) == false ]] || { echo 'Full Git history is required for the build number.' >&2; exit 1; }
vortnux_status_paths=()
if [[ ${CI:-} == true && ${GITHUB_REF:-} == refs/tags/v* ]]; then
  # Tagged CI deliberately embeds the separately configured publisher key.
  vortnux_status_paths=(-- . ':!release/trust.json')
fi
[[ -z $(git status --porcelain "${vortnux_status_paths[@]}") ]] || { echo 'Commit source changes before publishing an identifiable build.' >&2; exit 1; }
vortnux_build=$(git rev-list --count HEAD)
vortnux_commit=$(git rev-parse HEAD)
dotnet publish src/Vortnux.Desktop -c Release -r linux-x64 -o artifacts/linux-x64 \
  -p:RestoreLockedMode=true -p:Version="$vortnux_version" \
  -p:VortnuxBuildNumber="$vortnux_build" -p:VortnuxGitCommit="$vortnux_commit" -warnaserror
