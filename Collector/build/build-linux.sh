#!/usr/bin/env bash
# Self-contained Linux amd64 Collector. Install manually using LINUX-DEPLOYMENT.md.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
dotnet publish "$ROOT/src/WatcherCollector/WatcherCollector.csproj" \
  -c Release -p:BuildForLinux=true -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o "$ROOT/output/linux-collector"
echo "Ready: $ROOT/output/linux-collector/WatcherCollector"
