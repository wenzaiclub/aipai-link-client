# 艾派互联 第三方组件声明

## EasyTier（LGPL-3.0）

客户端不自己实现打洞和中继，组网能力来自 EasyTier：

- 项目：<https://github.com/EasyTier/EasyTier>
- 许可证：LGPL-3.0（详见 EasyTier 仓库的 LICENSE）
- 使用方式：Windows / Linux 端以独立进程调用 `easytier-core` / `easytier-cli`；
  Android 端编译成 `libeasytier_ffi.so`、`libeasytier_android_jni.so` 后由 JNI 调用

我们**没有修改 EasyTier 的业务代码**。Android 端只加了两处构建相关的东西（一个
`build.rs`、几个 bindgen 环境变量），改动内容和重新编译的步骤都放在
`src/android/patches/`，按 LGPL-3.0 第 4 条的要求一并发出来。

按 LGPL-3.0 的要求：

1. 分发本软件时随附 EasyTier 的完整许可证文本与本声明；
2. 用户可以用自己编译的 EasyTier 替换我们分发的版本——
   Windows / Linux 端替换程序目录下 `engine/` 里的可执行文件即可；
   Android 端替换 `app/src/main/jniLibs/<abi>/` 下对应架构的 `.so` 后重新打包即可，
   客户端代码不需要任何改动；
3. 我们分发的 EasyTier 可执行文件/动态库与上游同一版本（见 `tools/fetch-engine.*`
   里写死的版本号），不是 fork，也没有闭源补丁。

## QRCoder（MIT）

- 项目：<https://github.com/codebude/QRCoder>
- 许可证：MIT
- 使用范围：仅 Windows 客户端，用来生成设备接入用的二维码

## org.json（Android 平台内置）

- 使用范围：仅 Android 客户端
- 许可证：Apache-2.0（随 Android 平台提供，本项目未单独分发）

## 打包时下载的内容

`tools/fetch-engine.sh` / `tools/fetch-engine.ps1` 会从 EasyTier 的 GitHub Release
下载对应平台的引擎压缩包，并解出 `easytier-core`、`easytier-cli` 与许可证文件。
分发这些产物时请一并保留其中的 LICENSE。
