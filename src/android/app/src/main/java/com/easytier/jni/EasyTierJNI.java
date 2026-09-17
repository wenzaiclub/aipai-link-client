package com.easytier.jni;

/**
 * EasyTier 的 Android JNI 接口（对应 easytier-contrib/easytier-android-jni）。
 *
 * 两个 so 是分开编译的：libeasytier_android_jni.so 里的符号要靠
 * libeasytier_ffi.so 提供，所以必须按顺序加载，先 ffi 再 jni。
 */
public final class EasyTierJNI {

    static {
        System.loadLibrary("easytier_ffi");
        System.loadLibrary("easytier_android_jni");
    }

    private EasyTierJNI() {
    }

    /** 把 VpnService 建好的 tun fd 交给引擎。返回 0 表示成功。 */
    public static native int setTunFd(String instanceName, int fd);

    /** 只是校验配置能不能解析，一般不单独用。 */
    public static native int parseConfig(String config);

    /** 启动一个网络实例，config 是 TOML。返回 0 表示成功。 */
    public static native int runNetworkInstance(String config);

    /** 只保留下这几个实例，其余全部停掉；传 null 或空数组等于全停。 */
    public static native int retainNetworkInstance(String[] instanceNames);

    /** 取运行状态，返回 JSON。 */
    public static native String collectNetworkInfos(int maxLength);

    /** 取最近一次的错误。 */
    public static native String getLastError();

    public static void stopAll() {
        retainNetworkInstance(null);
    }
}
