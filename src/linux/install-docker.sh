#!/bin/sh
# 艾派互联 Linux 客户端 · Docker 一键部署
#
# 给 CentOS 7 / Ubuntu 16.04 这类老系统用：客户端是 .NET 自包含程序，
# 需要 glibc ≥ 2.27，老系统直装跑不起来（会报 GLIBC / GLIBCXX not found）。
# 放进 Debian 容器里跑就没这个问题——容器用 host 网络 + NET_ADMIN + /dev/net/tun，
# 虚拟网卡还是建在这台机器上，效果和直装一样。
#
# 用法：
#   curl -fsSL https://net.appiie.cn/download/install-docker.sh | sudo bash -s -- \
#        --user=你的账号 --password=你的密码 --network=适配码
#
# 再加 --web 可以同时开网页面板（默认 8787 端口）：
#   ... | sudo bash -s -- --user=账号 --password=密码 --network=适配码 --web
#
# 不加账号参数会提示交互输入；只看日志：docker logs -f aipai
set -e

BASE="https://net.appiie.cn"
PKG_URL="$BASE/download/%E8%89%BE%E6%B4%BE%E4%BA%92%E8%81%94-linux-x64.tar.gz"
BASE_TGZ_URL="$BASE/download/aipai-base-jammy.tar.xz"
DIR=/opt/aipai-docker
IMAGE="debian:bookworm-slim"      # 首选：官方镜像（能拉就拉）
LOCAL_IMAGE="aipai-base:jammy"    # 兜底：官网自带的基础镜像包，完全不走 Docker Hub
RUN_IMAGE=""
CNAME=aipai

USER_ARG="${AIPAI_USER:-}"
PASS_ARG="${AIPAI_PASSWORD:-}"
NET_ARG="${AIPAI_NETWORK:-}"
NAME_ARG="${AIPAI_DEVICE_NAME:-$(hostname 2>/dev/null || echo Linux)}"
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
        --web)        WANT_WEB=1 ;;
        --web=*)      WANT_WEB=1; WEB_PORT="${1#*=}" ;;
        -h|--help)
            sed -n '2,17p' "$0" 2>/dev/null || echo "见 https://net.appiie.cn/?p=xiazai"
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

echo "==> 艾派互联 Linux 客户端（Docker 方式）"
echo "    数据目录：$DIR"

# ---------- 1. Docker ----------
if ! command -v docker >/dev/null 2>&1; then
    echo "==> 没装 Docker，尝试用系统包管理器安装…"
    if command -v apt-get >/dev/null 2>&1; then
        apt-get update -qq || true
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq docker.io \
            || die "Docker 装不上，请手动安装后重跑本脚本"
    elif command -v dnf >/dev/null 2>&1; then
        dnf install -y docker || die "Docker 装不上，请手动安装后重跑本脚本"
    elif command -v yum >/dev/null 2>&1; then
        if ! yum install -y docker; then
            # CentOS 7 已停止维护，官方 mirrorlist 大面积 404，切到 vault 归档源再试一次
            if grep -q 'release 7' /etc/redhat-release 2>/dev/null; then
                echo "==> CentOS 7 官方源已下线，切到 vault 归档源后重试…"
                sed -i 's|^mirrorlist=|#mirrorlist=|; s|^#\?baseurl=http://mirror.centos.org|baseurl=http://vault.centos.org|' /etc/yum.repos.d/CentOS-*.repo 2>/dev/null || true
                yum clean all >/dev/null 2>&1 || true
                yum install -y docker \
                    || die "Docker 装不上，请手动安装：https://docs.docker.com/engine/install/"
            else
                die "Docker 装不上，请手动安装：https://docs.docker.com/engine/install/"
            fi
        fi
    else
        die "这个系统没有 apt/yum/dnf，请手动装 Docker：https://docs.docker.com/engine/install/"
    fi
    systemctl enable --now docker >/dev/null 2>&1 || service docker start >/dev/null 2>&1 || true
fi

if ! docker info >/dev/null 2>&1; then
    systemctl start docker >/dev/null 2>&1 || service docker start >/dev/null 2>&1 || true
    sleep 2
fi
docker info >/dev/null 2>&1 || die "Docker 装了但守护进程没起来，先执行：systemctl start docker"

# ---------- 2. 账号信息 ----------
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

# ---------- 3. 下载客户端 ----------
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT INT TERM

echo "==> 下载客户端…"

fetch_file() {  # $1=url  $2=输出路径
    if command -v curl >/dev/null 2>&1; then
        curl -fL --connect-timeout 20 --retry 3 -o "$2" "$1"
    elif command -v wget >/dev/null 2>&1; then
        wget -q -O "$2" "$1"
    else
        die "系统里没有 curl 也没有 wget，请先装其中一个"
    fi
}

fetch_file "$PKG_URL" "$TMP/pkg.tar.gz" || die "下载失败，请检查网络后重试"

tar -xzf "$TMP/pkg.tar.gz" -C "$TMP" || die "解压失败，下载的文件可能不完整"
SRC="$(find "$TMP" -maxdepth 2 -type f -name aipai | head -1 | xargs -r dirname)"
[ -n "$SRC" ] || die "安装包里没有找到主程序"

mkdir -p "$DIR/app" "$DIR/data"
cp -f "$SRC/aipai" "$DIR/app/aipai"
chmod +x "$DIR/app/aipai"
if [ -d "$SRC/engine" ]; then
    mkdir -p "$DIR/app/engine"
    cp -f "$SRC/engine/easytier-core" "$SRC/engine/easytier-cli" "$DIR/app/engine/" 2>/dev/null || true
    chmod +x "$DIR/app/engine/"* 2>/dev/null || true
fi

# ---------- 4. TUN 设备 ----------
if [ ! -e /dev/net/tun ]; then
    echo "==> 没有 /dev/net/tun，尝试创建（组网需要它）…"
    modprobe tun >/dev/null 2>&1 || true
    mkdir -p /dev/net
    [ -e /dev/net/tun ] || mknod /dev/net/tun c 10 200 2>/dev/null || true
    chmod 600 /dev/net/tun 2>/dev/null || true
fi
[ -e /dev/net/tun ] || die "创建不出 /dev/net/tun。老内核可能没编 tun 模块，这种情况只能用别的机器做组网节点"

# ---------- 5. 起容器 ----------
# ---------- 5. 准备运行环境镜像 ----------
# 国内网络经常拉不动 Docker Hub，所以分三级：官方镜像 → 国内加速器 → 官网自带的基础镜像包
prepare_image() {
    if docker image inspect "$IMAGE" >/dev/null 2>&1 || docker pull "$IMAGE" >/dev/null 2>&1; then
        RUN_IMAGE="$IMAGE"
        echo "==> 使用官方镜像 $IMAGE"
        return 0
    fi

    echo "==> 官方镜像拉不动（国内网络常见），先给 Docker 配国内加速器再试…"
    if [ -s /etc/docker/daemon.json ]; then
        # 用户已有配置就别动（里面可能有 data-root、log-driver 之类的东西）
        echo "    /etc/docker/daemon.json 已存在，不动它，直接走下面的兜底方案"
    elif [ -n "$(docker ps -q 2>/dev/null)" ]; then
        # 有容器在跑时重启 Docker 会打断它们，不值得为了配镜像源这么做
        echo "    检测到正在运行的容器，不重启 Docker，直接走下面的兜底方案"
    else
        mkdir -p /etc/docker
        cat > /etc/docker/daemon.json <<'JSON'
{
  "registry-mirrors": [
    "https://docker.m.daocloud.io",
    "https://docker.1ms.run",
    "https://docker.xuanyuan.me",
    "https://docker.1panel.live"
  ]
}
JSON
        echo "    已写入 /etc/docker/daemon.json（想撤销就删掉这个文件再重启 docker）"
        systemctl restart docker >/dev/null 2>&1 || service docker restart >/dev/null 2>&1 || true
        sleep 3
        if docker pull "$IMAGE" >/dev/null 2>&1; then
            RUN_IMAGE="$IMAGE"
            echo "==> 加速器生效，使用官方镜像"
            return 0
        fi
    fi

    echo "==> 加速器也不行，改用官网自带的基础镜像包（约 50MB，不经过 Docker Hub）…"
    if docker image inspect "$LOCAL_IMAGE" >/dev/null 2>&1; then
        RUN_IMAGE="$LOCAL_IMAGE"
        return 0
    fi
    fetch_file "$BASE_TGZ_URL" "$TMP/base.tar.xz" || return 1
    docker import "$TMP/base.tar.xz" "$LOCAL_IMAGE" >/dev/null || return 1
    RUN_IMAGE="$LOCAL_IMAGE"
    echo "==> 基础镜像已导入：$LOCAL_IMAGE"
    return 0
}

prepare_image || die "运行环境镜像怎么也准备不出来。可以手动执行看看：
      docker pull debian:bookworm-slim
    或直接下载 https://net.appiie.cn/download/aipai-base-jammy.tar.xz 后
      docker import aipai-base-jammy.tar.xz $LOCAL_IMAGE"

docker rm -f "$CNAME" >/dev/null 2>&1 || true

if [ "$WANT_WEB" = "1" ]; then
    CMD="web --port $WEB_PORT"
else
    CMD="connect"
fi

echo "==> 启动客户端（容器名 $CNAME）…"
is_running() {  # 用 inspect 判断，比 docker ps --filter name=^x$ 靠谱：
                # 老版 Docker（1.13 等）的 name 过滤不支持正则，会匹配不到而误判
    [ "$(docker inspect -f '{{.State.Running}}' "$CNAME" 2>/dev/null)" = "true" ]
}

COMPAT_ARGS=""
start_once() {
    docker rm -f "$CNAME" >/dev/null 2>&1 || true
    # shellcheck disable=SC2086
    docker run -d --name "$CNAME" --restart unless-stopped \
        --network host --cap-add NET_ADMIN --cap-add NET_RAW \
        --device /dev/net/tun $COMPAT_ARGS \
        -e AIPAI_USER="$USER_ARG" -e AIPAI_PASSWORD="$PASS_ARG" \
        -e AIPAI_NETWORK="$NET_ARG" -e AIPAI_DEVICE_NAME="$NAME_ARG" \
        -e AIPAI_AUTOCONNECT=1 \
        -v "$DIR/app:/app" -v "$DIR/data:/root/.config/aipai" \
        -w /app "$RUN_IMAGE" /app/aipai $CMD >/dev/null 2>&1 || return 1
    sleep 6
    is_running
}

if ! start_once; then
    # 老系统上常见两种坑：Docker 1.13 的 seccomp 策略拦了 .NET 需要的系统调用；
    # 或者老内核上 .NET 的 W^X 起不来（报 Failed to create CoreCLR）。
    # 这两种都用「兼容模式」再试一次（放开 seccomp、关掉 W^X、关服务器 GC）。
    if docker logs "$CNAME" 2>&1 | grep -q 'Failed to create CoreCLR'; then
        echo "==> 客户端在老环境下起不来，用兼容模式重试（放开 seccomp、关掉 W^X）…"
        COMPAT_ARGS="--security-opt seccomp=unconfined -e DOTNET_EnableWriteXorExecute=0 -e DOTNET_gcServer=0 --ulimit memlock=-1"
        start_once || true
    fi
fi

if is_running; then
    echo "✓ 已启动"
    echo
    docker logs --tail 8 "$CNAME" 2>&1 | sed 's/^/    /' || true
    echo
    if [ "$WANT_WEB" = "1" ]; then
        IP_SHOW="$(hostname -I 2>/dev/null | awk '{print $1}')"
        echo "  网页面板： http://${IP_SHOW:-这台机器的IP}:$WEB_PORT/"
    fi
    echo "  查看日志： docker logs -f $CNAME"
    echo "  重启容器： docker restart $CNAME"
    echo "  停止容器： docker stop $CNAME"
    echo "  彻底卸载： docker rm -f $CNAME && rm -rf $DIR"
else
    echo "! 容器没起来，下面是最后的日志：" >&2
    docker logs --tail 30 "$CNAME" 2>&1 | sed 's/^/    /' >&2 || true
    exit 1
fi
