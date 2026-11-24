#!/usr/bin/env bash
# Fetch the bundled netcoredbg (Linux x64 or arm64, picked from `uname -m`) into a
# destination folder — only if that version is not already there. Verifies the
# download against the pinned SHA-256 passed by the caller (which selects it for the
# same architecture, so the asset and checksum always agree). A
# netcoredbg-<version>.stamp marker records which version the folder holds, so a
# folder populated for another version is replaced rather than reused.
#
# Invoked by the FetchNetcoredbg MSBuild target. netcoredbg is MIT-licensed
# (Samsung); see THIRD-PARTY-NOTICES.md. Fetched at build time rather than
# committed to the repo.
#
# usage: fetch-netcoredbg.sh <version> <sha256> <destDir>
set -euo pipefail

VERSION="${1:?version required}"
SHA256="${2:?sha256 required}"
DESTDIR="${3:?destDir required}"

EXE="$DESTDIR/netcoredbg"
STAMP="$DESTDIR/netcoredbg-$VERSION.stamp"   # same name as NetcoredbgStampName
[ -f "$EXE" ] && [ -f "$STAMP" ] && exit 0   # this version is present — nothing to do

case "$(uname -m)" in
  x86_64|amd64)  ASSET="netcoredbg-linux-amd64.tar.gz" ;;
  aarch64|arm64) ASSET="netcoredbg-linux-arm64.tar.gz" ;;
  *) echo "fetch-netcoredbg: unsupported architecture '$(uname -m)'" >&2; exit 1 ;;
esac
URL="https://github.com/Samsung/netcoredbg/releases/download/$VERSION/$ASSET"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "fetch-netcoredbg: downloading $ASSET ($VERSION)..."
curl -fsSL -o "$TMP/$ASSET" "$URL"

GOT="$(sha256sum "$TMP/$ASSET" | cut -d' ' -f1)"
if [ "$GOT" != "$SHA256" ]; then
  echo "netcoredbg checksum mismatch for $ASSET" >&2
  echo "  expected $SHA256" >&2
  echo "  got      $GOT" >&2
  exit 1
fi

tar -xzf "$TMP/$ASSET" -C "$TMP"
SRC="$(dirname "$(find "$TMP" -name netcoredbg -type f | head -1)")"
[ -n "$SRC" ] || { echo "netcoredbg not found inside $ASSET" >&2; exit 1; }

# Replace (not merge into) an engine fetched for another version. Only a folder
# that actually holds a netcoredbg is cleared, never an arbitrary DESTDIR.
[ -f "$EXE" ] && rm -rf "$DESTDIR"
mkdir -p "$DESTDIR"
cp -r "$SRC/." "$DESTDIR/"
chmod +x "$EXE"
touch "$STAMP"
echo "fetch-netcoredbg: installed to $DESTDIR"
