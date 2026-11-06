#!/usr/bin/env bash
# build.sh [--when-closed]: build KSALiveClouds into out/KSALiveClouds (which the Borea instance links to) and the
# CLI harness. out/ is the live install, so never build while KSA runs: refuse, or with --when-closed wait for the
# game to close first (run that in the background).
set -euo pipefail
root=$(dirname "$(readlink -f "$0")")/..
running() { pgrep -f "[S]tarMap\.dll|[k]sa_linux_v" >/dev/null; }   # the ps-based game_running.sh misses Borea launches
if running; then
  if [ "${1:-}" = "--when-closed" ]; then
    echo "KSA is running; waiting for it to close..."
    while running; do sleep 15; done
  else
    echo "KSA is running; not building (use --when-closed)"; exit 1
  fi
fi
~/.dotnet/dotnet build -c Release "$root/KSALiveClouds/KSALiveClouds.csproj" -nologo -v q
~/.dotnet/dotnet build -c Release "$root/tools/LiveCloudsCli/LiveCloudsCli.csproj" -nologo -v q
echo "built -> $root/out/KSALiveClouds"
