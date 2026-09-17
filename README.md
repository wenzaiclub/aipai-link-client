# 艾派互联 客户端

把你自己名下的设备（电脑、NAS、服务器、手机、模拟器）连成一张虚拟局域网：
组内设备互相能 ping 通、能联机、能共享文件和打印机，效果和坐在同一个局域网里差不多。

同一适配码进同一个组网，每台设备分到的虚拟 IP 是固定的（只要不删组网就不会变），
组网内所有端口互通，开服不需要在路由器上映射端口。

**客户端免费**。需要更多设备数、更多组网或优先调度时，按月订阅平台的**技术服务费**
（账号与设备管理、打洞调度、优先调度、控制台与售后；不含线路，也不卖中继流量）。

官网：<https://net.appiie.cn>

## 仓库里有什么

| 平台 | 源码 | 运行时 | 说明 |
|---|---|---|---|
| Windows | [`src/windows`](src/windows) | .NET 10 / WPF | 主界面客户端，托盘、热更新、共享与打印机 |
| Linux / NAS | [`src/linux`](src/linux) | .NET 10 自包含单文件 | 命令行 + 网页面板，适合服务器和飞牛 NAS，附 fnOS 应用包 |
| Android | [`src/android`](src/android) | Java + NDK | 走系统 VpnService，引擎编成 `.so` 用 JNI 调用 |

三端连的是同一套后端、同一个组网，互相之间可以直接互通。

**服务端不在本仓库，也不开源**：调度信令、账号与计费、风控、后台管理这些都在我们这边。
客户端只做「登录 → 拿组网配置 → 拉起引擎」这几件事，接口调用都写在
`src/windows/AiPaiLink.Client/Shared/ApiClient.cs`，想接自己的服务端照这个约定实现即可。

## 客户端在做什么

- 登录 / 注册，取本账号的设备与组网列表
- 用 8 位组网适配码创建或加入组网
- 拉起本机的组网引擎（EasyTier），配好虚拟网卡和路由
- 创建组网时并发探测各调度节点的延迟，自动选最近的那个
- 文件与打印机共享：扫组网内共享、一键映射成本地盘、共享账号一键设置
- 局域网广播中继（网上邻居 / 局域网搜房能看到）
- 软件内热更新：查版本、下载、校验、替换、重启
- 共享给别的账号：跨账号输入同一个适配码就进同一张网

## 编译

### Windows

需要 .NET 10 SDK 和 Windows 10/11 x64。

```powershell
dotnet build src\windows\AiPaiLink.Client\AiPaiLink.Client.csproj -c Release
```

打单文件发布包（就是官网下载的那个 exe）：

```powershell
dotnet publish src\windows\AiPaiLink.Client\AiPaiLink.Client.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### Linux / NAS

```bash
dotnet publish src/linux/AipaiNet.Linux/AipaiNet.Linux.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o dist/aipai
./tools/fetch-engine.sh dist/aipai/engine
```

打包和安装步骤见 [`src/linux/README.md`](src/linux/README.md)，
飞牛 NAS 见 [`src/linux/飞牛NAS安装说明.md`](src/linux/飞牛NAS安装说明.md)。

### Android

```powershell
cd src\android
copy keystore.properties.example keystore.properties   # 按提示填自己的证书
gradle assembleRelease
```

没有 `keystore.properties` 也能编译，只是产出未签名 APK。
原生库已经提交在 `app/src/main/jniLibs/`，平时不用重新编；要重编看
[`src/android/README.md`](src/android/README.md) 和 [`src/android/patches`](src/android/patches)。

## 引擎从哪来

客户端不内置组网引擎，运行时按顺序找 `easytier-core`：

1. 程序目录下的 `engine\`（Linux 是 `engine/`）
2. 环境变量 `APPIENET_ENGINE_DIR` 指向的目录（Linux 还有 `/opt/aipai/engine`）

`tools/fetch-engine.sh` / `tools/fetch-engine.ps1` 会按固定版本号下载官方引擎放到这个目录。
版本刻意写死：服务端的锚点节点要和客户端引擎版本对得上。

引擎以独立进程启动，参数形如：

```
easytier-core -i <本机虚拟IP> --network-name <组网名> --network-secret <密钥> \
  -p tcp://<节点>:<端口> -p udp://<节点>:<端口> [-r 127.0.0.1:<rpc端口>]
```

Android 例外：引擎编成 `libeasytier_android_jni.so`，通过 JNI 直接调用。

## 配置与日志在哪

| 平台 | 配置 | 日志 |
|---|---|---|
| Windows | `%APPDATA%\艾派组网\settings.json` | `%APPDATA%\艾派组网\logs\` |
| Linux | `~/.config/aipai/config.json` | `~/.config/aipai/engine.log` |
| Android | 应用私有目录（SharedPreferences） | App 内「日志」页 |

Windows 上密码用 DPAPI 加密，只有当前用户能解。
默认连我们的服务端 `https://net.appiie.cn/api.php`，改 `ApiBaseUrl` 就能指向你自己的后端。

## 隐私

- 不上传、不分析你在组网内传输的文件内容和通信内容
- 收集的信息只有：账号信息、设备标识、连接日志（登录 / 连接 / 断开）、中继流量大小
- 连接日志留存 1 年，用于风控和排障
- 详细说明见官网的[隐私政策](https://net.appiie.cn/?p=privacy)与[用户协议](https://net.appiie.cn/?p=terms)

## 开源组件与许可

客户端以「独立进程 / 动态库」方式使用 [EasyTier](https://github.com/EasyTier/EasyTier)（LGPL-3.0），
清单见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

本项目自身以 [Apache License 2.0](LICENSE) 发布。

## 反馈

提 Issue 时请带上：系统版本、客户端版本（软件内可查）、以及日志里的相关片段
（记得抹掉账号、公网 IP 和适配码）。
