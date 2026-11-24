#!/usr/bin/env bash
set -euo pipefail
ANALYSIS_ROOT="$(cd "$(dirname "$0")" && pwd)"
dotnet publish "$ANALYSIS_ROOT/Listener_ConnectionAnalyzer.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$ANALYSIS_ROOT/output/linux"
