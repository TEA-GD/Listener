#!/usr/bin/env bash
set -euo pipefail
SETUP_DIR="$(cd "$(dirname "$0")" && pwd)"
exec python3 "$SETUP_DIR/install.py" uninstall "$@"
