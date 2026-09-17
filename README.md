# 艾派互联 客户端

把你自己名下的设备（电脑、NAS、服务器、手机、平板、Mac）连成一张虚拟局域网：组内设备互相能 ping 通、能联机、能共享文件和打印机，效果和坐在同一个局域网里差不多。

**客户端免费**。需要更多设备数、更多组网或优先调度时，按月订阅平台的**技术服务费**（账号与设备管理、打洞调度、优先调度、控制台与售后；不含线路，也不卖中继流量）。

官网：<https://net.appiie.cn>

## 这个仓库是什么

只放 **Windows 客户端** 的源码。服务端（调度信令、账号与计费、风控、后台管理）不在本仓库，也不开源。

客户端做的事：

- 登录 / 注册，拿本账号的设备与组网列表
- 用组网适配码创建或加入组网
- 拉起本机的组网引擎（EasyTier），把虚拟网卡、路由配好
- 创建组网时并发探测各调度节点的网络延迟，自动选最近的一个
- 文件与打印机共享：扫对方的共享、一键映射成本地盘、共享账号一键设置
- 软件内热更新：查版本、下载、校验、替换、重启

## 依赖

- .NET 10 SDK（`net10.0-windows`，WPF + WinForms）
- Windows 10/11 x64
- 组网引擎 [EasyTier](https://github.com/EasyTier/EasyTier) 的 `easytier-core.exe` / `easytier-cli.exe`（见下方「引擎从哪来」）

## 编译

```powershell
dotnet build src\AiPaiLink.Client\AiPaiLink.Client.csproj -c Release
```

打单文件发布包（就是官网下载的那个 exe）：

```powershell
dotnet publish src\AiPaiLink.Client\AiPaiLink.Client.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 引擎从哪来

客户端不内置组网引擎，运行时按顺序找 `easytier-core.exe`：

1. 程序目录下的 `engine\`
2. 环境变量 `APPIENET_ENGINE_DIR` 指向的目录

自己编译后想跑起来，把官方发布包里的 `engine\` 目录放到 exe 旁边即可。引擎以独立进程启动，参数形如：

```
easytier-core -i <本机虚拟IP> --network-name <组网名> --network-secret <密钥> \
  -p tcp://<节点>:<端口> -p udp://<节点>:<端口> [-r 127.0.0.1:<rpc端口>]
```

## 配置

默认连我们的服务端（`https://net.appiie.cn/api.php`），配置和日志在：

- 配置：`%APPDATA%\艾派组网\settings.json`（密码用 Windows DPAPI 加密，只有当前用户能解）
- 日志：`%APPDATA%\艾派组网\logs\`

想接自己的服务端，把 `%APPDATA%\艾派组网\settings.json` 里的 `ApiBaseUrl` 换成你的地址即可（服务端接口约定见 `Shared/ApiClient.cs`）。

## 隐私

- 不上传、不分析你在组网内传输的文件内容和通信内容
- 收集的信息只有：账号信息、设备标识、连接记录（登录/连接/断开）、中继流量大小
- 详细说明见官网的[隐私政策](https://net.appiie.cn/?p=privacy)

## 开源组件

本客户端以「独立进程 / 动态库」方式调用若干开源组件，未修改其源码，清单见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与官网「开源组件」页。

## 许可

[Apache License 2.0](LICENSE)

## 反馈

提交 Issue 时请带上：Windows 版本、客户端版本（软件「我的」页底部）、以及「查看日志」里的相关片段（记得抹掉账号和 IP）。
