# 艾派互联 · Android 客户端

跟 Windows、Linux 端是同一套后端、同一个组网。登录账号、选一个组网、点连接，
手机/模拟器就进了这张虚拟局域网，和电脑之间全端口互通。

## 它是怎么工作的

Android 不允许应用自己去开 `/dev/net/tun`，必须由系统的 `VpnService` 建好虚拟网卡，
再把文件描述符交给网络栈。所以流程是：

1. `AipaiVpnService` 用 `VpnService.Builder` 建 tun，只把组网那个网段加进路由；
2. 把 tun 的 fd 交给 EasyTier（`com.easytier.jni.EasyTierJNI#setTunFd`）；
3. EasyTier 用这个 fd 收发报文，和电脑端走同一套 P2P / 中继逻辑。

引擎不是单独的可执行文件，而是编译成 `libeasytier_android_jni.so` 直接 JNI 调用。
这一点很关键：Android 10 以后禁止从可写目录执行代码，塞个二进制再 `exec` 的路子在
手机上根本跑不起来，也很容易被安全软件判成危险应用。

## 目录

```
src/android/
├── app/src/main/
│   ├── AndroidManifest.xml
│   ├── java/cn/appiie/net/          界面、接口调用、VpnService
│   ├── java/com/easytier/jni/       EasyTier 的 JNI 声明
│   ├── jniLibs/<abi>/*.so           原生库（编译产物，见下）
│   └── res/                         图标、主题、字符串
├── patches/                         编译 .so 时对 EasyTier 做的唯一改动
├── keystore.properties.example      证书配置模板（真正的配置文件不入库）
└── keystore/                        证书放这里，同样不入库
```

## 一、打包 APK

装好 JDK 17+、Android SDK（platform 35、build-tools 36.0.0）、Gradle 8.7+，
在 `src/android` 目录下：

```bash
gradle assembleRelease
```

产物：`app/build/outputs/apk/release/app-release.apk`。

签名走 `keystore.properties`（复制 `keystore.properties.example` 改一下就行）。
没有这个文件时会跳过签名，产出 `app-release-unsigned.apk`，做编译检查够用。

证书一定要备份好，丢了就没法给已经装过的用户升级，只能让用户卸载重装。
自己生成一张：

```bash
keytool -genkeypair -v -keystore keystore/aipai.jks -alias aipai \
  -keyalg RSA -keysize 2048 -validity 10000
```

原生库（`jniLibs/`）已经提交在仓库里，正常打包不需要重新编译。

## 二、重新编译原生库

只在第一次搭环境、或者要升级 EasyTier 版本时需要做。

准备：

- Android NDK 28.x
- Rust + `rustup target add x86_64-linux-android aarch64-linux-android`
- `cargo install cargo-ndk`
- protoc 25.x、libclang 18.x（bindgen 交叉编译要用）

克隆 EasyTier 源码（版本要跟服务端锚点对齐，见根目录 `tools/fetch-engine.*`），
**不要放在带中文的路径下**，protoc 和 bindgen 会读错参数。然后按
[`patches/README.md`](patches/README.md) 加两处构建配置，再执行：

```bash
export ANDROID_NDK_HOME=<NDK 路径>
cd <EasyTier 源码目录>
cargo ndk -t x86_64 -t arm64-v8a \
  -o "<本仓库>/src/android/app/src/main/jniLibs" \
  build --release -p easytier-ffi -p easytier-android-jni \
  --config profile.release.lto=false --config profile.release.codegen-units=16
```

`easytier-android-jni/build.rs` 是必加的一处：让 jni 库通过 `DT_NEEDED` 依赖 ffi 库。
不加的话，`libeasytier_android_jni.so` 里的 `set_tun_fd` 这些符号在 Android 上
（`System.loadLibrary` 按 `RTLD_LOCAL` 加载）解析不到，加载时会直接崩。

## 三、装到模拟器 / 手机

1. 把 APK 拖进模拟器窗口，或者用模拟器自带的「安装 APK」；
2. 雷电之类的默认是 Android 9 / x86_64，装 x86_64 那份就行，真机装 arm64-v8a；
3. 首次连接会弹系统 VPN 授权，点「确定」，之后不会再问。

也可以用 adb：

```bash
adb connect 127.0.0.1:5555
adb install -r app-release.apk
```

## 四、账号与设备数

一个设备占一个名额，模拟器也算一台。名额按账号的会员等级算，在客户端「设备」页
可以解绑不用的设备。

## 五、现在还没做的

- 共享文件夹、打印机的界面还没做，Android 上暂时只能作为访问方；
- 踢人、备注、转让房主这些操作还是在电脑端做。
