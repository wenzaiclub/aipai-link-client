use std::path::PathBuf;

fn main() {
    // libeasytier_android_jni.so 里的 set_tun_fd / run_network_instance /
    // collect_network_infos 这些符号，全部来自单独编译的 libeasytier_ffi.so。
    // 默认链接不会把这条依赖写进 DT_NEEDED，而 Android 的 System.loadLibrary
    // 是按 RTLD_LOCAL 加载的，光靠“先 loadLibrary(ffi)”并不保险。
    //
    // 这里显式链一下，让 jni 库自己记住依赖关系，运行时由链接器自动带入。
    if let Ok(out) = std::env::var("OUT_DIR") {
        let mut dir = PathBuf::from(&out);
        // out -> build/<pkg>-<hash>/out -> build -> release
        if dir.pop() && dir.pop() && dir.pop() {
            println!("cargo:rustc-link-search=native={}", dir.display());
            println!("cargo:rustc-link-arg=-Wl,--no-as-needed");
            println!("cargo:rustc-link-lib=dylib=easytier_ffi");
        }
    }
}
