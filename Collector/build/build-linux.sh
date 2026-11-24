#!/usr/bin/env bash
# Self-contained Linux amd64 Collector + analyzers, with automatic install/remove helpers.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
dotnet publish "$ROOT/src/WatcherCollector/WatcherCollector.csproj" \
  -c Release -p:BuildForLinux=true -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o "$ROOT/output/linux-collector"
echo "Collector published; adding analyzers and deployment helpers..."
dotnet publish "$ROOT/../Listener_ConnectionAnalyzer/Listener_ConnectionAnalyzer.csproj" \
  -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$ROOT/output/linux-collector/analyzers/connection"
dotnet publish "$ROOT/../Listener_Analyzer/Listener_Analyzer.csproj" \
  -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$ROOT/output/linux-collector/analyzers/behavior"
for helper in install.py install-linux.sh uninstall-linux.sh; do
  install -m 755 "$ROOT/linux/$helper" "$ROOT/output/linux-collector/$helper"
done
install -m 644 "$ROOT/INSTALLATION.md" "$ROOT/output/linux-collector/INSTALLATION.md"
echo "Automatic installation: sudo bash $ROOT/output/linux-collector/install-linux.sh"
