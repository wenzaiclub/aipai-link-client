#!/bin/sh
# 下载官方 EasyTier 引擎（LGPL-3.0）到指定目录，供客户端运行时调用。
#
#   ./tools/fetch-engine.sh <输出目录> [架构]
#
# 架构默认取本机：x86_64 / aarch64 / armv7 …
# 版本刻意写死：服务端的锚点节点要和客户端引擎版本对得上，
# 跟着 latest 跑容易在升级当天连不上。
set -e

EASYTIER_VERSION="${EASYTIER_VERSION:-v2.6.4}"
OUT_DIR="${1:-engine}"

if [ -n "$2" ]; then
    ARCH="$2"
else
    case "$(uname -m)" in
        x86_64|amd64) ARCH=x86_64 ;;
        aarch64|arm64) ARCH=aarch64 ;;
        armv7l) ARCH=armv7 ;;
        *) ARCH=x86_64 ;;
    esac
fi

PKG="easytier-linux-${ARCH}-${EASYTIER_VERSION}"
URL="https://github.com/EasyTier/EasyTier/releases/download/${EASYTIER_VERSION}/${PKG}.zip"

echo "==> 下载 $URL"
mkdir -p "$OUT_DIR"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if command -v curl >/dev/null 2>&1; then
    curl -fL --retry 3 -o "$TMP/engine.zip" "$URL"
elif command -v wget >/dev/null 2>&1; then
    wget -O "$TMP/engine.zip" "$URL"
else
    echo "需要 curl 或 wget" >&2
    exit 1
fi

if command -v unzip >/dev/null 2>&1; then
    unzip -q "$TMP/engine.zip" -d "$TMP/x"
else
    # 有些精简系统没有 unzip，用 python 兜底
    python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" \
        "$TMP/engine.zip" "$TMP/x"
fi

find "$TMP/x" -name 'easytier-core*' -exec cp -f {} "$OUT_DIR/easytier-core" \;
find "$TMP/x" -name 'easytier-cli*' -exec cp -f {} "$OUT_DIR/easytier-cli" \;
find "$TMP/x" -name 'LICENSE*' -exec cp -f {} "$OUT_DIR/" \; 2>/dev/null || true
chmod +x "$OUT_DIR/easytier-core" "$OUT_DIR/easytier-cli"

echo "==> 引擎已放到 $OUT_DIR"
"$OUT_DIR/easytier-core" --version 2>/dev/null || true
