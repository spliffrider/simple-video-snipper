#!/bin/bash
set -euo pipefail

source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
configuration="${CONFIGURATION:-release}"
scratch_dir="${SNIPPER_BUILD_DIR:-$HOME/Library/Caches/SimpleVideoSnipper/build}"
app="${SNIPPER_APP_DIR:-$HOME/Applications}/Simple Video Snipper.app"
signing_identity="${SNIPPER_SIGN_IDENTITY:--}"

swift build --package-path "$source_dir" --scratch-path "$scratch_dir" -c "$configuration" >&2
bin_dir="$(swift build --package-path "$source_dir" --scratch-path "$scratch_dir" -c "$configuration" --show-bin-path)"
mkdir -p "$(dirname "$app")"
staging="$(mktemp -d "$(dirname "$app")/.snipper-build.XXXXXXXX")"
trap 'rm -rf "$staging"' EXIT
bundle="$staging/Simple Video Snipper.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources/Companion"
install -m 755 "$bin_dir/SimpleVideoSnipper" "$bundle/Contents/MacOS/SimpleVideoSnipper"
cp "$source_dir/Info.plist" "$bundle/Contents/Info.plist"
for asset in index.html recorder.css recorder.js; do
    cp "$source_dir/../Companion/$asset" "$bundle/Contents/Resources/Companion/$asset"
done
for resource in "$bin_dir"/*.bundle; do
    if [[ -d "$resource" ]]; then
        ditto "$resource" "$bundle/Contents/Resources/$(basename "$resource")"
    fi
done

if [[ "$signing_identity" == "-" ]]; then
    codesign --force --sign - "$bundle" >&2
else
    codesign --force --options runtime --timestamp --sign "$signing_identity" "$bundle" >&2
fi
codesign --verify --deep --strict "$bundle"
if [[ -e "$app" ]]; then
    mv "$app" "$staging/previous.app"
fi
if ! mv "$bundle" "$app"; then
    if [[ -e "$staging/previous.app" ]]; then
        mv "$staging/previous.app" "$app"
    fi
    exit 1
fi
printf '%s\n' "$app"
