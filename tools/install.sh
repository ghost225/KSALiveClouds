#!/usr/bin/env bash
# install.sh: link out/KSALiveClouds into a Borea instance (mods/ symlink + manifest.toml entry), for development.
# The instance is $BOREA_INSTANCE, or Borea's active one. Backs the manifest up first. Refuses while KSA runs.
set -euo pipefail
root=$(readlink -f "$(dirname "$(readlink -f "$0")")/..")
borea=${BOREA_HOME:-$HOME/.local/share/Borea}
if [ -z "${BOREA_INSTANCE:-}" ]; then
  id=$(sed -n 's/^ActiveInstanceId *= *"\(.*\)"/\1/p' "$borea/active-instance.toml" 2>/dev/null || true)
  [ -n "$id" ] || { echo "no BOREA_INSTANCE and no active Borea instance"; exit 1; }
  BOREA_INSTANCE="$borea/Instances/$id"
fi
inst=$BOREA_INSTANCE
if pgrep -f "[S]tarMap\.dll|[k]sa_linux_v" >/dev/null; then echo "KSA is running; not installing"; exit 1; fi
[ -f "$root/out/KSALiveClouds/KSALiveClouds.dll" ] || { echo "build first (tools/build.sh)"; exit 1; }
ln -sfn "$root/out/KSALiveClouds" "$inst/mods/KSALiveClouds"
if ! grep -q '^id = "KSALiveClouds"' "$inst/manifest.toml"; then
  cp "$inst/manifest.toml" "$inst/manifest.toml.bak-ksaliveclouds"
  printf '\n[[mods]]\nid = "KSALiveClouds"\nenabled = true\n' >> "$inst/manifest.toml"
  echo "manifest: added KSALiveClouds (backup manifest.toml.bak-ksaliveclouds)"
fi
echo "installed: $inst/mods/KSALiveClouds -> $root/out/KSALiveClouds"
