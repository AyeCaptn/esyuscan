#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

props=patches/Avalonia.Wayland/Source.props
patch_file="$PWD/patches/Avalonia.Wayland/stale-configure.patch"
revision=$(sed -n 's/.*<AvaloniaRevision>\(.*\)<\/AvaloniaRevision>.*/\1/p' "$props")
checksum=$(sed -n 's/.*<AvaloniaArchiveSha256>\(.*\)<\/AvaloniaArchiveSha256>.*/\1/p' "$props")
[[ "$revision" =~ ^[0-9a-f]{40}$ && "$checksum" =~ ^[0-9a-f]{64}$ ]]
cache="$PWD/artifacts/avalonia-wayland"
source="$cache/Avalonia-$revision"
fingerprint=$(sha256sum "$props" "$patch_file")
if [[ -f "$source/.esyuscan-patch" && "$(cat "$source/.esyuscan-patch")" == "$fingerprint" ]]; then
    exit 0
fi

mkdir -p "$cache"
archive="$cache/$revision.tar.gz"
if [[ ! -f "$archive" ]]; then
    download=$(mktemp "$cache/download-XXXXXX")
    trap 'rm -f -- "$download"' EXIT
    curl --fail --location --silent --show-error \
        "https://codeload.github.com/AvaloniaUI/Avalonia/tar.gz/$revision" --output "$download"
    printf '%s  %s\n' "$checksum" "$download" | sha256sum --check --status
    mv -- "$download" "$archive"
fi
printf '%s  %s\n' "$checksum" "$archive" | sha256sum --check --status
tar -xzf "$archive" -C "$cache"
patch --directory="$source" --strip=1 --batch --forward --input="$patch_file"
printf '%s\n' "$fingerprint" > "$source/.esyuscan-patch"
