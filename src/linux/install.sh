#!/bin/sh
# 艾派互联 Linux 客户端安装脚本
#   sudo ./install.sh                只安装（之后手动 aipai login / aipai connect）
#   sudo ./install.sh --service      安装并配置开机自动接入（会问账号密码适配码）
#   sudo ./install.sh --service --web 在上面基础上再开网页管理面板（默认 8787 端口）
#   也可以先把账号信息放到环境变量里，实现全自动：
#   sudo AIPAI_USER=xxx AIPAI_PASSWORD=yyy AIPAI_NETWORK=ZZZ ./install.sh --service --web
set -e

cd "$(dirname "$0")"
PREFIX=/opt/aipai

SERVICE_FLAG=0
WEB_FLAG=0
WEB_PORT=8787
for a in "$@"; do
    case "$a" in
        --service) SERVICE_FLAG=1 ;;
        --web)     WEB_FLAG=1 ;;
        --web=*)   WEB_FLAG=1; WEB_PORT="${a#*=}" ;;
    esac
done

if [ "$(id -u)" != "0" ]; then
    echo "请用 root 运行：sudo ./install.sh"
    exit 1
fi

echo "==> 安装到 $PREFIX"
mkdir -p "$PREFIX/engine"
cp -f aipai "$PREFIX/aipai"
chmod +x "$PREFIX/aipai"
if [ -d engine ]; then
    cp -f engine/easytier-core engine/easytier-cli "$PREFIX/engine/" 2>/dev/null || true
    chmod +x "$PREFIX/engine/"* 2>/dev/null || true
fi
ln -sf "$PREFIX/aipai" /usr/local/bin/aipai

echo "==> 完成，当前版本："
"$PREFIX/aipai" version

if [ "$SERVICE_FLAG" = "1" ] || [ "$WEB_FLAG" = "1" ]; then
    echo
    echo "==> 配置开机自动接入"

    if [ -z "$AIPAI_USER" ]; then
        printf "账号: "
        read AIPAI_USER
    fi
    if [ -z "$AIPAI_PASSWORD" ]; then
        printf "密码: "
        read AIPAI_PASSWORD
    fi
    if [ -z "$AIPAI_NETWORK" ]; then
        printf "适配码（8位，可留空=上次连接过的组网）: "
        read AIPAI_NETWORK
    fi

    if [ "$WEB_FLAG" = "1" ]; then
        EXEC_LINE="ExecStart=$PREFIX/aipai web --port $WEB_PORT"
        AUTO_ENV="Environment=AIPAI_AUTOCONNECT=1"
        echo "==> 同时开启网页管理面板（端口 $WEB_PORT）"
    else
        EXEC_LINE="ExecStart=$PREFIX/aipai connect"
        AUTO_ENV=""
    fi

    cat > /etc/systemd/system/aipai.service <<EOF
[Unit]
Description=艾派互联客户端
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
Environment=AIPAI_USER=$AIPAI_USER
Environment=AIPAI_PASSWORD=$AIPAI_PASSWORD
Environment=AIPAI_NETWORK=$AIPAI_NETWORK
$AUTO_ENV
$EXEC_LINE
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF
    chmod 600 /etc/systemd/system/aipai.service
    systemctl daemon-reload
    systemctl enable aipai
    systemctl restart aipai
    sleep 2
    echo "==> 服务状态："
    systemctl --no-pager -l status aipai | head -12 || true
    if [ "$WEB_FLAG" = "1" ]; then
        IP_SHOW="$(hostname -I 2>/dev/null | awk '{print $1}')"
        echo
        echo "网页面板： http://${IP_SHOW:-服务器IP}:$WEB_PORT/   （用艾派账号登录）"
    fi
    echo
    echo "查看日志：journalctl -u aipai -f"
else
    echo
    echo "用法："
    echo "  aipai login <账号> <密码>     登录"
    echo "  aipai list                    查看我的组网"
    echo "  sudo aipai connect            接入组网（Ctrl+C 断开）"
    echo "  aipai status                  查看连接状态"
    echo
    echo "想开机自动接入（NAS/服务器）：sudo ./install.sh --service"
fi
