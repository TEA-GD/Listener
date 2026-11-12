#!/usr/bin/env bash
set -euo pipefail
ANALYZER_ROOT="$(cd "$(dirname "$0")" && pwd)"
dotnet publish "$ANALYZER_ROOT/Listener_Analyzer.csproj" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$ANALYZER_ROOT/output/linux"
echo "Ready: $ANALYZER_ROOT/output/linux/Listener_Analyzer"
