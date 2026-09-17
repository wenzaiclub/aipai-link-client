# 艾派互联 Linux 客户端（命令行）

支持 Ubuntu / Debian / CentOS / **飞牛 NAS（fnOS）** / 群晖 / OpenWrt(需 x86_64) 等 x86_64 Linux。
**不需要装 .NET 运行时**（程序是自包含单文件）。

> NAS 用户（飞牛 / 群晖 / unRAID）：直接看 **《飞牛NAS安装说明.md》**，
> 里面有两种装法（原生 + Docker），以及接通后怎么访问飞牛的服务。

## 一、安装

```bash
# 解压
tar -xzf 艾派互联-linux-x64.tar.gz
cd 艾派互联-linux

# 一键安装（会把程序装到 /opt/aipai，并创建 aipai 命令）
sudo ./install.sh
```

安装脚本做了三件事：把程序和引擎放到 `/opt/aipai`、创建 `/usr/local/bin/aipai` 软链、
（可选）安装开机自动接入的 systemd 服务。

## 二、使用

```bash
aipai login <账号> <密码>     # 登录（只需一次，之后会记住）
aipai list                    # 看我的组网和本机固定IP
aipai create 我的组网          # 创建组网，得到 8 位适配码
aipai join ABC12345           # 用适配码加入别人的组网
sudo aipai connect            # 接入组网（前台运行，Ctrl+C 断开）
sudo aipai connect ABC12345   # 指定适配码接入；没加入过会自动加入
aipai status                  # 另一个终端里查看对端/链路
aipai disconnect              # 断开
sudo aipai web                # 启动网页管理面板（浏览器里连/断组网）
```

### 网页管理面板

不想敲命令的话，用网页面板：

```bash
sudo aipai web                # 默认 0.0.0.0:8787
sudo aipai web --port 9000    # 换端口
```

然后浏览器打开 `http://服务器IP:8787/`，用你的艾派账号登录，就能：
看连接状态和本机固定 IP、看每个对端是直连还是中继、点按钮接入/断开、创建或加入组网。

装成服务时加上 `--web` 就会同时具备"开机自动接入 + 网页面板"：

```bash
sudo ./install.sh --service --web
```

## 三、开机自动接入（服务器 / NAS 常用）

```bash
sudo ./install.sh --service        # 安装并配置 systemd 服务（会问账号/密码/适配码）
sudo systemctl status aipai        # 查看状态
sudo journalctl -u aipai -f        # 看日志
```

也可以全自动（无需交互），适合脚本/Docker：

```bash
sudo AIPAI_USER=账号 AIPAI_PASSWORD=密码 AIPAI_NETWORK=适配码 ./install.sh --service
```

支持的环境变量：

| 变量 | 说明 |
|---|---|
| `AIPAI_USER` / `AIPAI_PASSWORD` | 账号密码，没有登录状态时自动登录 |
| `AIPAI_NETWORK` | 适配码或组网名称，留空则连上次用过的组网 |
| `AIPAI_DEVICE_NAME` | 这台设备在客户端里的名字（默认主机名） |
| `AIPAI_ENGINE_DIR` | 引擎目录（默认程序旁边的 engine/） |

## 四、注意事项

* **必须用 root 运行 `connect`**（要创建虚拟网卡 tun）
* 本机固定 IP：同一台机器重连、重启后虚拟 IP 都不变，只有删除组网才会重新分配
* 引擎日志在 `~/.config/aipai/engine.log`，登录信息在 `~/.config/aipai/config.json`
* 引擎目录默认在程序旁边的 `engine/`；也可以放到 `/opt/aipai/engine`，
  或用环境变量 `APPIENET_ENGINE_DIR` 指定
* 与 Windows 客户端可互通：同一适配码进同一个组网，互相能 ping 通、能开服联机

## 五、卸载

```bash
sudo systemctl disable --now aipai 2>/dev/null
sudo rm -rf /opt/aipai /usr/local/bin/aipai /etc/systemd/system/aipai.service
rm -rf ~/.config/aipai
```
