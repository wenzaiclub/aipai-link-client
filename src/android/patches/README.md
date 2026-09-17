# Android 端用到的 EasyTier 改动

Android 端不跑 `easytier-core` 可执行文件，而是把 EasyTier 编成两个 `.so`，
通过 JNI 直接调用（成品已经提交在 `app/src/main/jniLibs/` 里）。原因写在
`../README.md`：Android 10 以后不允许从可写目录执行代码。

编译这两个 `.so` 用的是 EasyTier 上游源码（v2.6.4，和服务端锚点版本一致），
只加了两处东西，都在这个目录里：

## 1. `easytier-android-jni/build.rs`

本目录下的 `easytier-android-jni/build.rs` 就是那个文件，复制到
EasyTier 源码的 `easytier-contrib/easytier-android-jni/build.rs` 即可。

上游这个 crate 没有 `build.rs`。加它的原因：

`libeasytier_android_jni.so` 里的 `set_tun_fd`、`run_network_instance`、
`collect_network_infos` 这些符号全部来自另一个库 `libeasytier_ffi.so`。
默认链接不会把这条依赖写进 `DT_NEEDED`，而 Android 的 `System.loadLibrary`
按 `RTLD_LOCAL` 加载，光靠"先 load ffi 再 load jni"并不保险，会出现
加载时符号解析失败直接崩。这段 `build.rs` 显式让 jni 库记住依赖，
运行时由链接器自动带入 ffi 库。

除此之外没有改动上游任何代码。

## 2. `.cargo/config.toml` 里的 Android 环境变量

bindgen 交叉编译时必须知道 NDK 的 sysroot 和 clang 内置头文件目录，
否则 `kcp-sys` 找不到 `stddef.h`。模板见 `cargo-config-android.toml`，
把里面的路径换成你自己的 NDK / libclang / protoc 路径，追加到
`EasyTier/.cargo/config.toml` 末尾。

## 重新编译 .so

```powershell
$env:ANDROID_NDK_HOME = '<NDK 路径>'
cd <EasyTier 源码目录>          # 路径里不要带中文，protoc 和 bindgen 会读错参数
cargo ndk -t x86_64 -t arm64-v8a `
  -o "<本仓库>\src\android\app\src\main\jniLibs" `
  build --release -p easytier-ffi -p easytier-android-jni `
  --config profile.release.lto=false --config profile.release.codegen-units=16
```

改动只有上面两处，其余全是上游代码。按 LGPL-3.0 的要求，把 EasyTier 换成
你自己编译的版本即可（`jniLibs/` 里那几个 `.so` 就是全部），App 侧代码不需要改。
