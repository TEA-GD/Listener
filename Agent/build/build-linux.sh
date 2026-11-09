#!/usr/bin/env bash
# Build a Debian 12 (bookworm) amd64 package. Requires .NET 10 SDK and dpkg-deb.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/output/linux-agent"
BUILD="$ROOT/output/deb-stage"
DEB="$ROOT/output/watcher-agent_1.0.0_amd64.deb"
dotnet publish "$ROOT/src/WatcherAgent/WatcherAgent.csproj" -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:DebugType=none \
  -p:IncludeNativeLibrariesForSelfExtract=true -o "$OUT"
rm -rf "$BUILD"
mkdir -p "$BUILD/DEBIAN" "$BUILD/usr/bin" "$BUILD/usr/lib/watcher-agent" \
         "$BUILD/lib/systemd/system"
install -m 755 "$OUT/WatcherAgent" "$BUILD/usr/bin/watcher-agent"
install -m 644 "$ROOT/linux/watcher-agent.service" "$BUILD/lib/systemd/system/watcher-agent.service"
cat > "$BUILD/DEBIAN/control" <<'EOF_CONTROL'
Package: watcher-agent
Version: 1.0.0
Section: admin
Priority: optional
Architecture: amd64
Maintainer: Watcher IT <it@example.invalid>
Depends: libc6 (>= 2.36), libgcc-s1, libstdc++6, libssl3, libicu72, zlib1g, iproute2, systemd
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
chmod 755 "$BUILD/DEBIAN/postinst" "$BUILD/DEBIAN/prerm" "$BUILD/DEBIAN/postrm"
dpkg-deb --build --root-owner-group "$BUILD" "$DEB"
echo "Ready: $DEB"
