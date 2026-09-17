package cn.appiie.net;

import android.content.Context;
import android.content.SharedPreferences;

import java.util.UUID;

/** 本地存储：登录状态、记住的账号、设备标识。 */
public final class Prefs {

    private static final String FILE = "aipai";

    private final SharedPreferences sp;

    public Prefs(Context ctx) {
        sp = ctx.getApplicationContext().getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    /** 设备标识，第一次运行生成一次，之后固定不变。 */
    public String deviceUid() {
        String v = sp.getString("device_uid", "");
        if (v == null || v.length() < 8) {
            v = "android-" + UUID.randomUUID().toString().replace("-", "");
            sp.edit().putString("device_uid", v).apply();
        }
        return v;
    }

    public String token() {
        return sp.getString("token", "");
    }

    public void setToken(String token) {
        sp.edit().putString("token", token == null ? "" : token).apply();
    }

    public String username() {
        return sp.getString("username", "");
    }

    public void setUsername(String name) {
        sp.edit().putString("username", name == null ? "" : name).apply();
    }

    public String password() {
        return sp.getString("password", "");
    }

    public void setPassword(String pwd) {
        sp.edit().putString("password", pwd == null ? "" : pwd).apply();
    }

    public boolean remember() {
        return sp.getBoolean("remember", true);
    }

    public void setRemember(boolean v) {
        sp.edit().putBoolean("remember", v).apply();
    }

    public int networkId() {
        return sp.getInt("network_id", 0);
    }

    public void setNetworkId(int id) {
        sp.edit().putInt("network_id", id).apply();
    }

    /** 清掉登录状态，但保留设备标识和记住的密码。 */
    public void clearSession() {
        sp.edit().putString("token", "").putInt("network_id", 0).apply();
    }
}
