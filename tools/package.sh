#!/usr/bin/env bash
# package.sh: the release archive dist/KSALiveClouds-v<version>.zip, one folder KSALiveClouds/ (what Borea and a
# manual install expect). Builds only a committed, pushed commit, so the archive matches the published source.
set -euo pipefail
root=$(readlink -f "$(dirname "$(readlink -f "$0")")/..")
cd "$root"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' KSALiveClouds/KSALiveClouds.csproj)
[ -n "$version" ] || { echo "no <Version> in the csproj"; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "working tree is dirty; commit first"; exit 1; }
git fetch -q origin
[ "$(git rev-parse HEAD)" = "$(git rev-parse '@{u}')" ] || { echo "HEAD is not what the remote has; push first"; exit 1; }
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
~/.dotnet/dotnet build -c Release KSALiveClouds/KSALiveClouds.csproj -nologo -v q \
  -p:ModOutDir="$work/KSALiveClouds/" -p:SourceRevisionId="$(git rev-parse --short HEAD)"
cp LICENSE THIRD_PARTY_NOTICES.md README.md "$work/KSALiveClouds/"
mkdir -p dist
zip="dist/KSALiveClouds-v$version.zip"
rm -f "$zip"
(cd "$work" && zip -qrX "$root/$zip" KSALiveClouds)
echo "$zip  ($(git rev-parse --short HEAD), v$version)"
unzip -l "$zip"
sha256sum "$zip"
