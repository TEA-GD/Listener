#!/usr/bin/env bash
# Build a package for the local Debian 12/13 amd64 release. Requires .NET 10 SDK and dpkg-deb.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Runtime library package names changed between Debian releases (including time64 OpenSSL).
# Build on the same Debian release as the target; never pull ICU from another release.
. /etc/os-release
if [ "${ID:-}" != debian ]; then
  echo "Unsupported build host: ${PRETTY_NAME:-unknown}. This script targets Debian 12/13 amd64." >&2
  exit 1
fi
if [ "$(dpkg --print-architecture)" != amd64 ]; then
  echo 'This package targets amd64 only.' >&2
  exit 1
fi
case "${VERSION_ID:-}" in
  12) RUNTIME_DEPENDS='libc6 (>= 2.36), libgcc-s1, libstdc++6, libssl3, libicu72, zlib1g' ;;
  13) RUNTIME_DEPENDS='libc6 (>= 2.41), libgcc-s1, libstdc++6, libssl3t64, libicu76, zlib1g' ;;
  *) echo "Unsupported Debian release: ${VERSION_ID:-unknown}. Supported: 12, 13." >&2; exit 1 ;;
esac
PACKAGE_VERSION=1.0.1
echo "Building Watcher Agent $PACKAGE_VERSION for Debian $VERSION_ID amd64"
OUT="$ROOT/output/linux-agent"
BUILD="$ROOT/output/deb-stage"
DEB="$ROOT/output/watcher-agent_${PACKAGE_VERSION}_amd64.deb"
dotnet publish "$ROOT/src/WatcherAgent/WatcherAgent.csproj" -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:DebugType=none \
  -p:IncludeNativeLibrariesForSelfExtract=true -o "$OUT"
rm -rf "$BUILD"
install -d -m 755 "$BUILD/DEBIAN" "$BUILD/usr/bin" "$BUILD/usr/lib/watcher-agent" \
         "$BUILD/lib/systemd/system"
install -m 755 "$OUT/WatcherAgent" "$BUILD/usr/bin/watcher-agent"
install -m 644 "$ROOT/linux/watcher-agent.service" "$BUILD/lib/systemd/system/watcher-agent.service"
cat > "$BUILD/DEBIAN/control" <<EOF_CONTROL
Package: watcher-agent
Version: $PACKAGE_VERSION
Section: admin
Priority: optional
Architecture: amd64
Maintainer: Watcher IT <it@example.invalid>
Depends: $RUNTIME_DEPENDS, iproute2, systemd
Description: Watcher SIEM connection-monitoring endpoint agent
 Local TCP connection snapshots and durable plaintext TCP event forwarding.
EOF_CONTROL
cat > "$BUILD/DEBIAN/postinst" <<'EOF_POSTINST'
#!/bin/sh
set -e
if [ "$1" = configure ]; then
  install -d -m 700 /etc/watcher-agent /var/lib/watcher-agent /var/lib/watcher-agent/spool
  # Automatic configuration: no enrollment, no token. Preserve config on upgrades.
  if [ ! -e /etc/watcher-agent/agent.json ]; then
    HOST_ID="$(hostname -f 2>/dev/null || hostname)"
    # Hostnames from the OS are expected to be DNS-compatible.
    case "$HOST_ID" in *[!A-Za-z0-9_.-]*|''|.|..) HOST_ID="$(hostname)" ;; esac
    case "$HOST_ID" in *[!A-Za-z0-9_.-]*|''|.|..) echo 'Invalid hostname: cannot configure Watcher agent' >&2; exit 1;; esac
    printf '{"agent_id":"%s","spool_dir":"/var/lib/watcher-agent/spool"}\n' "$HOST_ID" > /etc/watcher-agent/agent.json
    chmod 600 /etc/watcher-agent/agent.json
  fi
  if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload || true
    systemctl enable watcher-agent.service >/dev/null 2>&1 || true
    if [ -d /run/systemd/system ]; then
      systemctl restart watcher-agent.service || true
    fi
  fi
  echo 'Watcher Agent installed (no token). Confirm matching PLAIN TCP collector and IP allowlist.'
fi
EOF_POSTINST
cat > "$BUILD/DEBIAN/prerm" <<'EOF_PRERM'
#!/bin/sh
set -e
if [ "$1" = remove ] || [ "$1" = deconfigure ]; then
  systemctl disable --now watcher-agent.service >/dev/null 2>&1 || true
fi
EOF_PRERM
cat > "$BUILD/DEBIAN/postrm" <<'EOF_POSTRM'
#!/bin/sh
set -e
if command -v systemctl >/dev/null 2>&1; then systemctl daemon-reload || true; fi
if [ "$1" = purge ]; then
  rm -f /etc/watcher-agent/agent.json
  rmdir /etc/watcher-agent 2>/dev/null || true
fi
EOF_POSTRM
chmod 644 "$BUILD/DEBIAN/control"
chmod 755 "$BUILD/DEBIAN/postinst" "$BUILD/DEBIAN/prerm" "$BUILD/DEBIAN/postrm"
dpkg-deb --build --root-owner-group "$BUILD" "$DEB"
echo "Ready: $DEB"
