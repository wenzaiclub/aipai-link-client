# 艾派互联 · 飞牛 NAS（fnOS）安装说明

飞牛是 Debian 内核的系统，所以有两种装法，**任选一种**：

| 方式 | 适合 | 特点 |
|---|---|---|
| 方式一：原生安装 | 大多数人 | 最简单，一条命令，开机自动接入 |
| 方式二：Docker | 想用飞牛 Docker 面板管理 | 在飞牛"Docker → 新增项目"里粘贴 compose |

两种方式都会让**飞牛主机本身**加入组网（拿到一个固定虚拟 IP），
之后飞牛上的共享、服务都能被组网内的电脑访问。

---

## 方式一：原生安装（推荐）

1. 把 `艾派互联-linux-x64.tar.gz` 上传到飞牛（比如放到 `/vol1/1000/`）
2. SSH 登录飞牛（需要 root 权限）
3. 执行：

```bash
cd /vol1/1000
tar -xzf 艾派互联-linux-x64.tar.gz
cd 艾派互联-linux
sudo ./install.sh --service
```

回车后会依次问你**账号、密码、适配码**，填完就装好了：
程序在 `/opt/aipai`，服务名 `aipai`，**开机自动接入、断线自动重连**。

**想要网页管理界面**（浏览器里看状态、点按钮连/断组网），把最后一条换成：

```bash
sudo ./install.sh --service --web
```

装完浏览器打开 `http://飞牛IP:8787/`，用你的艾派账号登录即可。
端口想改就加 `--web=9000`。这个网页面板是**这台飞牛自己**的管理界面，
通过它连/断的是飞牛主机的组网，不是打开网页的那台电脑。

常用命令：

```bash
systemctl status aipai        # 看运行状态
journalctl -u aipai -f        # 看实时日志
systemctl restart aipai       # 重启
aipai list                    # 看本机固定IP和组网
aipai status                  # 看对端是直连还是中继
```

一条命令装完（含网页面板）也可以这样：

```bash
curl -fsSL https://net.appiie.cn/download/install-linux.sh | sudo bash -s -- \
     --user=你的账号 --password=你的密码 --network=适配码 --web
```

> 账号密码会写进 `/etc/systemd/system/aipai.service`（root 才能读），
> 如果不想留在文件里，可以先 `sudo aipai login` 登录一次再装服务。

---

## 方式二：Docker / Compose

1. 在飞牛的某个目录建一个文件夹，例如 `/vol1/1000/aipai`，结构如下：

```
aipai/
├── docker-compose.yml
├── app/
│   ├── aipai                       ← 从 tar.gz 里复制
│   └── engine/
│       ├── easytier-core
│       └── easytier-cli
└── data/                           ← 空目录，程序会自己写配置
```

2. 编辑 `docker-compose.yml`，把 `AIPAI_USER`、`AIPAI_PASSWORD`、`AIPAI_NETWORK` 改成你自己的
3. 飞牛面板 → **Docker → 新增项目**，选择这个目录的 compose 文件启动
   （或者 SSH 里 `cd /vol1/1000/aipai && sudo docker compose up -d`）

**三个关键点，改动会导致组网不生效：**

* `network_mode: host` —— 必须保留，否则组网网卡会建在容器里，飞牛主机用不上
* `devices: /dev/net/tun` —— 创建虚拟网卡需要
* `./data` 目录 —— 保存设备标识，**删掉的话固定 IP 会变**

常用命令：

```bash
sudo docker logs -f aipai       # 看日志
sudo docker restart aipai       # 重启
sudo docker exec aipai /app/aipai status   # 看链路状态
```

---

## 装好之后怎么用

1. 到客户端（Windows）"组网"页看成员列表，会多出一台叫 `飞牛NAS` 的设备，记下它的虚拟 IP
2. 在电脑上直接访问飞牛的服务，例如：
   * 文件共享：资源管理器输入 `\\10.x.x.x`（飞牛虚拟IP）
   * 网页管理：浏览器打开 `http://10.x.x.x:8000`（按飞牛实际端口）
3. 反过来，飞牛也能访问组网内其他机器（比如游戏开服机的 7010 端口）

## 常见问题

**Q：日志里一直显示"正在接入组网…"？**
A：确认适配码是 8 位、账号能正常登录；`aipai list` 能看到组网列表说明账号没问题。

**Q：接入成功但电脑访问不了飞牛？**
A：到客户端"组网"页点一次【网络诊断】看是不是走了中继；中继也能用，只是延迟高些。
另外确认飞牛本身的防火墙（fnOS 防火墙）放行了对应端口。

**Q：重装/重建容器后 IP 变了？**
A：`data` 目录（或原生的 `~/.config/aipai`）里存着设备标识，删掉就会当成新设备分配新 IP。

**Q：想换账号或适配码？**
A：原生：`sudo systemctl stop aipai` → `sudo aipai login 新账号 密码` → 改 service 里的 `AIPAI_NETWORK` → 重启服务
Docker：改 compose 里的环境变量 → `docker compose up -d`
