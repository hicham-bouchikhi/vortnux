#!/usr/bin/env bash
set -euo pipefail
vortnux_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$vortnux_root"
vortnux_version=${VORTNUX_VERSION:-0.1.0}
[[ "$vortnux_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid version' >&2; exit 1; }
vortnux_publish="$vortnux_root/artifacts/linux-x64"
test -x "$vortnux_publish/Vortnux.Desktop"
vortnux_build=$("$vortnux_publish/Vortnux.Desktop" --build-number)
vortnux_commit=$("$vortnux_publish/Vortnux.Desktop" --git-commit)
[[ "$vortnux_build" =~ ^[1-9][0-9]*$ && "$vortnux_commit" =~ ^[0-9a-f]{40}$ ]] || { echo 'Publish a stamped build with scripts/publish.sh before packaging.' >&2; exit 1; }
[[ $("$vortnux_publish/Vortnux.Desktop" --release-version) == "$vortnux_version" ]] || { echo 'Package version differs from the executable.' >&2; exit 1; }
"$vortnux_publish/Vortnux.Desktop" --smoke-test
vortnux_stage="$vortnux_root/artifacts/package-stage"
mkdir -p "$vortnux_stage/publish" "$vortnux_root/dist"
# Only ship executable and required native libraries, not debugging or static-link artifacts.
install -m755 "$vortnux_publish/Vortnux.Desktop" "$vortnux_stage/publish/"
for vortnux_library in "$vortnux_publish"/*.so; do install -m755 "$vortnux_library" "$vortnux_stage/publish/"; done
cp packaging/vortnux.{desktop,svg,metainfo.xml} "$vortnux_stage/"
install -m644 LICENSE "$vortnux_stage/LICENSE"
echo "$vortnux_build" > "$vortnux_stage/build-number"
"$vortnux_publish/Vortnux.Desktop" --version > "$vortnux_stage/build-info.txt"
tar -czf "dist/vortnux-${vortnux_version}-linux-x64.tar.gz" -C "$vortnux_stage" publish vortnux.desktop vortnux.svg vortnux.metainfo.xml LICENSE build-number build-info.txt

vortnux_appdir="$vortnux_root/artifacts/Vortnux.AppDir"
rm -rf "$vortnux_appdir"
mkdir -p "$vortnux_appdir/usr/lib/vortnux" "$vortnux_appdir/usr/share/metainfo" "$vortnux_appdir/usr/share/applications"
install -Dm644 LICENSE "$vortnux_appdir/usr/share/licenses/vortnux/LICENSE"
cp -a "$vortnux_stage/publish/." "$vortnux_appdir/usr/lib/vortnux/"
cp packaging/vortnux.svg "$vortnux_appdir/"
cp packaging/vortnux.desktop "$vortnux_appdir/io.vortnux.Vortnux.desktop"
cp packaging/vortnux.metainfo.xml "$vortnux_appdir/usr/share/metainfo/io.vortnux.Vortnux.appdata.xml"
if [[ -n "${GITHUB_REPOSITORY:-}" ]]; then
  python3 - "$vortnux_appdir/usr/share/metainfo/io.vortnux.Vortnux.appdata.xml" <<'PY'
import os
import sys
import xml.etree.ElementTree as ET
path = sys.argv[1]
tree = ET.parse(path)
ET.SubElement(tree.getroot(), 'url', {'type': 'homepage'}).text = 'https://github.com/' + os.environ['GITHUB_REPOSITORY']
tree.write(path, encoding='utf-8', xml_declaration=True)
PY
fi
install -m755 packaging/AppRun "$vortnux_appdir/AppRun"
# AppImage desktop launcher is relative to AppDir, while the Arch launcher is in PATH.
sed -i 's|Exec=vortnux %u|Exec=AppRun %u|' "$vortnux_appdir/io.vortnux.Vortnux.desktop"
cp "$vortnux_appdir/io.vortnux.Vortnux.desktop" "$vortnux_appdir/usr/share/applications/"
vortnux_tools="$vortnux_root/artifacts/tools"
mkdir -p "$vortnux_tools"
vortnux_tool="$vortnux_tools/appimagetool.AppImage"
vortnux_runtime="$vortnux_tools/runtime-x86_64"
if [[ ! -f "$vortnux_tool" ]]; then
  curl --fail --location --silent --show-error 'https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage' -o "$vortnux_tool"
fi
if [[ ! -f "$vortnux_runtime" ]]; then
  curl --fail --location --silent --show-error 'https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64' -o "$vortnux_runtime"
fi
echo 'ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0  '"$vortnux_tool" | sha256sum --check --status
echo '156f4bdbde9c52d01814600013e0a273f0118dc2de98975f3c8c63427ec79074  '"$vortnux_runtime" | sha256sum --check --status
chmod +x "$vortnux_tool"
(cd "$vortnux_tools" && ./appimagetool.AppImage --appimage-extract)
vortnux_metadata_options=()
if [[ -z "${GITHUB_REPOSITORY:-}" ]]; then
  # Justified development-only exception: no repository homepage exists to validate.
  # Published CI builds have a real homepage and run the complete AppStream check.
  echo 'Local unpublished build: AppStream catalogue validation deferred until a repository exists.'
  vortnux_metadata_options+=(--no-appstream)
fi
ARCH=x86_64 "$vortnux_tools/squashfs-root/AppRun" "${vortnux_metadata_options[@]}" --runtime-file "$vortnux_runtime" --mksquashfs-opt -processors --mksquashfs-opt 2 "$vortnux_appdir" "dist/Vortnux-${vortnux_version}-x86_64.AppImage"

vortnux_arch="$vortnux_root/artifacts/arch"
mkdir -p "$vortnux_arch"
cp "dist/vortnux-${vortnux_version}-linux-x64.tar.gz" "$vortnux_arch/"
vortnux_hash=$(sha256sum "$vortnux_arch/vortnux-${vortnux_version}-linux-x64.tar.gz")
vortnux_hash=${vortnux_hash%% *}
sed -e "s/pkgver=0.1.0/pkgver=${vortnux_version}/" -e "s/pkgrel=1/pkgrel=${vortnux_build}/" -e "s/REPLACED_BY_PACKAGING_SCRIPT/${vortnux_hash}/" packaging/arch/PKGBUILD > "$vortnux_arch/PKGBUILD"
if command -v makepkg >/dev/null; then
  (cd "$vortnux_arch" && makepkg --force --cleanbuild)
  cp "$vortnux_arch/vortnux-${vortnux_version}-${vortnux_build}-x86_64.pkg.tar.zst" dist/
fi
(cd dist && sha256sum ./*.AppImage ./*.tar.gz > SHA256SUMS)
if compgen -G 'dist/*.pkg.tar.zst' >/dev/null; then (cd dist && sha256sum ./*.pkg.tar.zst >> SHA256SUMS); fi
echo "Packages are in $vortnux_root/dist"
