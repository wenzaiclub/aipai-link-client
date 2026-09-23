#!/bin/sh
# 艾派互联 Linux / 飞牛 NAS 一键安装脚本
#
# 只装客户端：
#   curl -fsSL https://net.appiie.cn/download/install-linux.sh | sudo bash
#
# 装客户端 + 开机自动接入（推荐）：
#   curl -fsSL https://net.appiie.cn/download/install-linux.sh | sudo bash -s -- \
#        --user=你的账号 --password=你的密码 --network=适配码
#
# 装客户端 + 网页面板（NAS 推荐，浏览器里管理组网）：
#   curl -fsSL https://net.appiie.cn/download/install-linux.sh | sudo bash -s -- \
#        --user=你的账号 --password=你的密码 --network=适配码 --web
#
# 交互式（会提示输入账号密码）：
#   curl -fsSL https://net.appiie.cn/download/install-linux.sh | sudo bash -s -- --service
set -e

BASE="https://net.appiie.cn"
PKG_URL="$BASE/download/%E8%89%BE%E6%B4%BE%E4%BA%92%E8%81%94-linux-x64.tar.gz"
PREFIX=/opt/aipai
UNIT=/etc/systemd/system/aipai.service

USER_ARG="${AIPAI_USER:-}"
PASS_ARG="${AIPAI_PASSWORD:-}"
NET_ARG="${AIPAI_NETWORK:-}"
NAME_ARG="${AIPAI_DEVICE_NAME:-}"
WANT_SERVICE=0
WANT_WEB=0
WEB_PORT=8787

while [ $# -gt 0 ]; do
    case "$1" in
        --user=*)     USER_ARG="${1#*=}" ;;
        --password=*) PASS_ARG="${1#*=}" ;;
        --network=*)  NET_ARG="${1#*=}" ;;
        --name=*)     NAME_ARG="${1#*=}" ;;
        --user)       USER_ARG="$2"; shift ;;
        --password)   PASS_ARG="$2"; shift ;;
        --network)    NET_ARG="$2"; shift ;;
        --name)       NAME_ARG="$2"; shift ;;
        --service)    WANT_SERVICE=1 ;;
        --web)        WANT_WEB=1 ;;
        --web=*)      WANT_WEB=1; WEB_PORT="${1#*=}" ;;
        --user*)      USER_ARG="${1#--user }" ;;
        -h|--help)
            sed -n '2,19p' "$0" 2>/dev/null || echo "见 https://net.appiie.cn/?p=xiazai"
            exit 0 ;;
        *) echo "忽略未知参数: $1" ;;
    esac
    shift
done

die() { echo "✗ $1" >&2; exit 1; }

[ "$(id -u)" = "0" ] || die "请用 root 运行：在命令前面加 sudo"

ARCH="$(uname -m)"
[ "$ARCH" = "x86_64" ] || [ "$ARCH" = "amd64" ] \
    || die "目前只提供 x86_64 版本，当前架构是 $ARCH"

# 系统版本检查：客户端是 .NET 自包含程序，最低要 glibc 2.27 + GLIBCXX_3.4.22。
# 老系统（CentOS 7 / Ubuntu 16.04）跑不起来，如果不在这一步拦住，
# 用户会得到「装完了但服务起不来、aipai 命令报 GLIBC not found」这种莫名其妙的现场。
ver_ge() {  # $1 >= $2 ？
    awk -v a="$1" -v b="$2" 'BEGIN{
        n=split(a,x,"."); m=split(b,y,".");
        k=(n>m?n:m);
        for(i=1;i<=k;i++){ xa=(i<=n?x[i]+0:0); yb=(i<=m?y[i]+0:0);
            if(xa>yb) exit 0; if(xa<yb) exit 1 }
        exit 0 }'
}

GLIBC_VER="$(ldd --version 2>/dev/null | head -n1 | awk '{print $NF}')"
case "$GLIBC_VER" in ''|*[!0-9.]*) GLIBC_VER="0" ;; esac

LIBCXX_VER="$(strings /usr/lib64/libstdc++.so.6 2>/dev/null \
    | grep -o 'GLIBCXX_3\.4\.[0-9]\+' | sort -V | tail -n1 | sed 's/^GLIBCXX_//')"
[ -n "$LIBCXX_VER" ] || LIBCXX_VER="$(strings /usr/lib/x86_64-linux-gnu/libstdc++.so.6 2>/dev/null \
    | grep -o 'GLIBCXX_3\.4\.[0-9]\+' | sort -V | tail -n1 | sed 's/^GLIBCXX_//')"
[ -n "$LIBCXX_VER" ] || LIBCXX_VER="0"

if ! ver_ge "$GLIBC_VER" "2.27" || ! ver_ge "$LIBCXX_VER" "3.4.22"; then
    echo "✗ 这台机器的运行库太旧，跑不了客户端" >&2
    echo "    当前：glibc $GLIBC_VER，libstdc++ GLIBCXX_$LIBCXX_VER" >&2
    echo "    需要：glibc ≥ 2.27 且 GLIBCXX ≥ 3.4.22" >&2
    echo "    也就是 CentOS 8+/Rocky 8+/Alma 8+/Ubuntu 18.04+/Debian 10+/飞牛 fnOS。" >&2
    echo "    CentOS 7、Ubuntu 16.04 这类老系统不支持（客户端是 .NET 自包含程序，官方最低就这个线）。" >&2
    echo >&2
    echo "  三个办法，任选一个：" >&2
    echo "  1) 换系统（推荐）：CentOS 7 已经停止维护，装 Rocky Linux 9 或 Ubuntu 22.04 最省事" >&2
    echo "  2) 用 Docker 跑（不改系统，容器里跑，组网网卡还是建在这台机器上）：" >&2
    echo "       curl -fsSL $BASE/download/install-docker.sh | sudo bash -s -- \\" >&2
    echo "            --user=账号 --password=密码 --network=适配码" >&2
    echo "  3) 换一台系统新一点的机器当组网节点" >&2
    echo >&2
    echo "  详情见 https://net.appiie.cn/?p=xiazai" >&2
    exit 1
fi

echo "==> 艾派互联 Linux 客户端 一键安装"
echo "    安装目录：$PREFIX"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT INT TERM

echo "==> 下载安装包…"
if command -v curl >/dev/null 2>&1; then
    curl -fL --connect-timeout 20 --retry 3 -o "$TMP/pkg.tar.gz" "$PKG_URL" \
        || die "下载失败，请检查网络后重试"
elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$TMP/pkg.tar.gz" "$PKG_URL" || die "下载失败，请检查网络后重试"
else
    die "系统里没有 curl 也没有 wget，请先安装其中一个（如 apt install curl）"
fi

echo "==> 解压安装…"
tar -xzf "$TMP/pkg.tar.gz" -C "$TMP" || die "解压失败，下载的文件可能不完整"
SRC="$(find "$TMP" -maxdepth 2 -type f -name aipai | head -1 | xargs -r dirname)"
[ -n "$SRC" ] || die "安装包里没有找到主程序"

mkdir -p "$PREFIX/engine"
cp -f "$SRC/aipai" "$PREFIX/aipai"
chmod +x "$PREFIX/aipai"
if [ -d "$SRC/engine" ]; then
    cp -f "$SRC/engine/easytier-core" "$SRC/engine/easytier-cli" "$PREFIX/engine/" 2>/dev/null || true
    chmod +x "$PREFIX/engine/"* 2>/dev/null || true
fi
ln -sf "$PREFIX/aipai" /usr/local/bin/aipai
echo -n "    版本："; "$PREFIX/aipai" version 2>/dev/null || echo "(未知)"

# 给了账号密码，或者明确要求装服务，就装开机自动接入
if [ -n "$USER_ARG" ] || [ "$WANT_SERVICE" = "1" ] || [ "$WANT_WEB" = "1" ]; then
    have_tty=0
    [ -r /dev/tty ] && have_tty=1

    if [ -z "$USER_ARG" ]; then
        [ "$have_tty" = "1" ] || die "没有账号信息。请加参数：--user=账号 --password=密码 --network=适配码"
        printf "账号: "; read -r USER_ARG < /dev/tty
    fi
    if [ -z "$PASS_ARG" ]; then
        [ "$have_tty" = "1" ] || die "缺少 --password=密码"
        printf "密码: "; read -r PASS_ARG < /dev/tty
    fi
    if [ -z "$NET_ARG" ] && [ "$have_tty" = "1" ]; then
        printf "适配码（8位，留空=上次连接过的组网）: "; read -r NET_ARG < /dev/tty
    fi

    if ! command -v systemctl >/dev/null 2>&1; then
        echo
        echo "==> 这个系统没有 systemd，装不了开机服务。"
        echo "    手动接入：sudo aipai connect ${NET_ARG}"
        echo "    或按包内《飞牛NAS安装说明.md》用 Docker 部署。"
        exit 0
    fi

    if [ "$WANT_WEB" = "1" ]; then
        EXEC_LINE="ExecStart=$PREFIX/aipai web --port $WEB_PORT"
        AUTO_ENV="Environment=AIPAI_AUTOCONNECT=1"
        echo "==> 配置开机自动接入 + 网页面板（端口 $WEB_PORT）…"
    else
        EXEC_LINE="ExecStart=$PREFIX/aipai connect"
        AUTO_ENV=""
        echo "==> 配置开机自动接入…"
    fi

    cat > "$UNIT" <<EOF
[Unit]
Description=艾派互联客户端
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
Environment=AIPAI_USER=$USER_ARG
Environment=AIPAI_PASSWORD=$PASS_ARG
Environment=AIPAI_NETWORK=$NET_ARG
Environment=AIPAI_DEVICE_NAME=$NAME_ARG
$AUTO_ENV
$EXEC_LINE
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF
    chmod 600 "$UNIT"
    systemctl daemon-reload
    systemctl enable aipai >/dev/null 2>&1 || true
    systemctl restart aipai
    sleep 3

    if systemctl is-active --quiet aipai; then
        if [ "$WANT_WEB" = "1" ]; then
            IP_SHOW="$(hostname -I 2>/dev/null | awk '{print $1}')"
            echo "✓ 安装完成：已开机自动接入组网，网页面板已启动"
            echo "  浏览器打开： http://${IP_SHOW:-服务器IP}:$WEB_PORT/"
        else
            echo "✓ 安装完成，已开机自动接入组网"
        fi
    else
        echo "! 服务已安装但没起来，看下日志：journalctl -u aipai -n 30 --no-pager"
    fi
    echo
    echo "常用命令："
    echo "  systemctl status aipai      查看运行状态"
    echo "  journalctl -u aipai -f      看实时日志"
    echo "  aipai list                  查看本机固定IP"
    echo "  aipai status                查看对端是直连还是中继"
else
    echo
    echo "✓ 客户端安装完成（未配置自动接入）"
    echo
    echo "接下来手动使用："
    echo "  sudo aipai login <账号> <密码>   登录"
    echo "  sudo aipai connect              接入组网（Ctrl+C 断开）"
    echo
    echo "想开机自动接入，重新运行一次并带上账号信息："
    echo "  curl -fsSL $BASE/download/install-linux.sh | sudo bash -s -- \\"
    echo "       --user=账号 --password=密码 --network=适配码"
fi
