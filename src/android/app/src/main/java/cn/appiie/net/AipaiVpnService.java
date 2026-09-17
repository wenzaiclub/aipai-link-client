package cn.appiie.net;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Intent;
import android.net.VpnService;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import com.easytier.jni.EasyTierJNI;

import org.json.JSONObject;

/**
 * 组网通道。
 *
 * VpnService 负责建 tun，EasyTier 负责跑这张网。顺序不能反：
 * 先把实例起起来，再把 tun 的文件描述符交给引擎，之后引擎才接管收发。
 */
public class AipaiVpnService extends VpnService {

    private static final String TAG = "AipaiVpn";

    public static final String ACTION_START = "cn.appiie.net.START";
    public static final String ACTION_STOP = "cn.appiie.net.STOP";
    public static final String ACTION_STATE = "cn.appiie.net.STATE";

    public static final String EXTRA_CONFIG = "config";
    public static final String EXTRA_IPV4 = "ipv4";
    public static final String EXTRA_SUBNET = "subnet";
    public static final String EXTRA_IPV6 = "ipv6";
    public static final String EXTRA_V6SUBNET = "v6subnet";

    public static final String EXTRA_TEXT = "text";
    public static final String EXTRA_RUNNING = "running";
    public static final String EXTRA_TRACE = "trace";

    private static final String CHANNEL_ID = "aipai";
    private static final int NOTIFY_ID = 0x8811;

    private ParcelFileDescriptor tun;
    private volatile boolean running;
    private volatile boolean stopping;
    private Thread worker;
    private Thread monitor;
    private String lastText = "";

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? null : intent.getAction();
        if (ACTION_STOP.equals(action)) {
            shutdown("已断开");
            return START_NOT_STICKY;
        }
        if (intent == null || !ACTION_START.equals(action)) {
            return START_STICKY;
        }

        String toml = intent.getStringExtra(EXTRA_CONFIG);
        if (toml == null || toml.isEmpty()) {
            shutdown("配置为空");
            return START_NOT_STICKY;
        }
        if (running) {
            report("已经连接着，先断开再重连。", true);
            return START_STICKY;
        }

        startForegroundCompat("正在连接组网…");
        stopping = false;

        final String ipv4 = stringExtra(intent, EXTRA_IPV4);
        final String subnet = stringExtra(intent, EXTRA_SUBNET);
        final String ipv6 = stringExtra(intent, EXTRA_IPV6);
        final String v6subnet = stringExtra(intent, EXTRA_V6SUBNET);

        worker = new Thread(() -> connect(toml, ipv4, subnet, ipv6, v6subnet), "aipai-connect");
        worker.start();
        return START_STICKY;
    }

    private static String stringExtra(Intent intent, String key) {
        String v = intent.getStringExtra(key);
        return v == null ? "" : v;
    }

    private void connect(String toml, String ipv4, String subnet, String ipv6, String v6subnet) {
        try {
            trace("配置：本机IP " + ipv4 + "，网段 " + subnet
                    + (ipv6.isEmpty() ? "，不开 IPv6" : "，IPv6 " + ipv6));

            // 保险起见，先清掉可能残留的实例
            try {
                EasyTierJNI.stopAll();
            } catch (Throwable ignored) {
                // 没有实例时这里本来就会失败，忽略
            }

            trace("正在启动 EasyTier 引擎…");
            report("正在启动引擎…", false);
            int started;
            try {
                started = EasyTierJNI.runNetworkInstance(toml);
            } catch (Throwable t) {
                fail("引擎启动失败：" + rootMessage(t));
                return;
            }
            if (started != 0) {
                fail("引擎启动失败：" + errText());
                return;
            }
            trace("引擎已启动");

            trace("正在建立虚拟网卡…");
            report("正在建立虚拟网卡…", false);
            Builder builder = new Builder();
            builder.setSession("艾派互联");
            builder.setMtu(1380);
            builder.setBlocking(true);
            if (!ipv4.isEmpty() && !subnet.isEmpty()) {
                builder.addAddress(ipv4, 24);
                builder.addRoute(subnet, 24);
            } else {
                fail("服务端下发的地址不完整：ipv4=" + ipv4 + " subnet=" + subnet);
                return;
            }
            // IPv6 是附加项，配不上就只跑 IPv4，不要因此把整个连接带崩
            if (!ipv6.isEmpty() && !v6subnet.isEmpty()) {
                try {
                    builder.addAddress(ipv6, 64);
                    builder.addRoute(v6subnet, 64);
                } catch (Throwable t) {
                    trace("IPv6 配置跳过：" + rootMessage(t));
                }
            }

            tun = builder.establish();
            if (tun == null) {
                fail("系统没有同意建立 VPN，请检查是不是被别的 VPN 占用了。");
                return;
            }
            trace("虚拟网卡已建立，fd=" + tun.getFd());

            // 引擎那边是异步接管的，fd 可能要给几次才收得下
            trace("正在把网卡交给引擎…");
            int rc = -1;
            for (int i = 0; i < 30 && !stopping; i++) {
                try {
                    rc = EasyTierJNI.setTunFd(Tunnel.INSTANCE, tun.getFd());
                } catch (Throwable t) {
                    rc = -1;
                }
                if (rc == 0) {
                    break;
                }
                Thread.sleep(200);
            }
            if (rc != 0) {
                fail("引擎没能接管网卡：" + errText());
                return;
            }

            running = true;
            trace("已连接");
            report("已连接，正在同步节点信息…", true);
            startMonitor();
        } catch (Throwable t) {
            Log.w(TAG, "connect failed", t);
            fail("连接失败：" + rootMessage(t));
        }
    }

    private static String rootMessage(Throwable t) {
        Throwable cur = t;
        while (cur.getCause() != null && cur.getCause() != cur) {
            cur = cur.getCause();
        }
        String msg = cur.getMessage();
        if (msg == null || msg.isEmpty()) {
            msg = cur.getClass().getSimpleName();
        }
        return msg;
    }

    private void trace(String line) {
        Intent intent = new Intent(ACTION_STATE);
        intent.setPackage(getPackageName());
        intent.putExtra(EXTRA_TRACE, line);
        sendBroadcast(intent);
    }

    private void startMonitor() {
        monitor = new Thread(() -> {
            int idle = 0;
            while (running && !stopping) {
                try {
                    String json = EasyTierJNI.collectNetworkInfos(16);
                    if (json != null) {
                        String text = describe(json);
                        if (text != null && !text.equals(lastText)) {
                            lastText = text;
                            report(text, true);
                        }
                        idle = 0;
                    } else {
                        idle++;
                    }
                } catch (Throwable t) {
                    Log.w(TAG, "collect failed", t);
                    idle++;
                }
                if (idle >= 5) {
                    idle = 0;
                }
                try {
                    Thread.sleep(3000);
                } catch (InterruptedException e) {
                    return;
                }
            }
        }, "aipai-monitor");
        monitor.start();
    }

    /** 把 collectNetworkInfos 的 JSON 翻成一行给人看的状态。 */
    private String describe(String json) {
        try {
            JSONObject root = new JSONObject(json).optJSONObject("map");
            if (root == null) {
                return null;
            }
            JSONObject info = root.optJSONObject(Tunnel.INSTANCE);
            if (info == null) {
                return null;
            }
            if (!info.optBoolean("running", false)) {
                String err = info.optString("error_msg", "");
                return err.isEmpty() ? "引擎已停止" : ("引擎报错：" + err);
            }

            String mine = "";
            JSONObject myNode = info.optJSONObject("my_node_info");
            if (myNode != null) {
                mine = ipOf(myNode.optJSONObject("virtual_ipv4"));
            }

            int direct = 0;
            int relay = 0;
            org.json.JSONArray pairs = info.optJSONArray("peer_route_pairs");
            if (pairs != null) {
                for (int i = 0; i < pairs.length(); i++) {
                    JSONObject pair = pairs.optJSONObject(i);
                    if (pair == null) {
                        continue;
                    }
                    JSONObject route = pair.optJSONObject("route");
                    JSONObject peer = pair.optJSONObject("peer");
                    if (route == null || peer == null) {
                        continue;
                    }
                    if (route.optInt("next_hop_peer_id", -1) == peer.optInt("peer_id", -2)) {
                        direct++;
                    } else {
                        relay++;
                    }
                }
            }

            StringBuilder sb = new StringBuilder("已连接");
            if (!mine.isEmpty()) {
                sb.append("　本机IP ").append(mine);
            }
            sb.append("　对端 ").append(direct + relay).append(" 个");
            if (direct + relay > 0) {
                sb.append("（直连 ").append(direct).append(" / 中继 ").append(relay).append("）");
            }
            return sb.toString();
        } catch (Exception e) {
            return null;
        }
    }

    private static String ipOf(JSONObject inet) {
        if (inet == null) {
            return "";
        }
        long addr = inet.optLong("address", 0);
        if (addr == 0) {
            return "";
        }
        return ((addr >> 24) & 0xFF) + "." + ((addr >> 16) & 0xFF) + "."
                + ((addr >> 8) & 0xFF) + "." + (addr & 0xFF);
    }

    private String errText() {
        try {
            String e = EasyTierJNI.getLastError();
            return e == null || e.isEmpty() ? "未知原因" : e;
        } catch (Throwable t) {
            return "未知原因";
        }
    }

    private void fail(String text) {
        Log.w(TAG, text);
        running = false;
        report(text, false);
        stopEngine();
        stopSelf();
    }

    public void shutdown(String text) {
        stopping = true;
        running = false;
        if (monitor != null) {
            monitor.interrupt();
            monitor = null;
        }
        stopEngine();
        if (text != null) {
            report(text, false);
        }
        stopForeground(true);
        stopSelf();
    }

    private void stopEngine() {
        try {
            EasyTierJNI.stopAll();
        } catch (Throwable ignored) {
            // 引擎没起来时这里会失败，忽略
        }
        if (tun != null) {
            try {
                tun.close();
            } catch (Exception ignored) {
                // 关不上也无所谓，进程退出时系统会回收
            }
            tun = null;
        }
    }

    @Override
    public void onRevoke() {
        // 用户或系统把 VPN 收回去了
        shutdown("VPN 权限被系统收回，组网已断开");
        super.onRevoke();
    }

    @Override
    public void onDestroy() {
        stopping = true;
        running = false;
        stopEngine();
        super.onDestroy();
    }

    private void report(String text, boolean connected) {
        Intent intent = new Intent(ACTION_STATE);
        intent.setPackage(getPackageName());
        intent.putExtra(EXTRA_TEXT, text);
        intent.putExtra(EXTRA_RUNNING, connected);
        sendBroadcast(intent);
        updateNotification(text);
    }

    private void startForegroundCompat(String text) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager nm = getSystemService(NotificationManager.class);
            if (nm != null && nm.getNotificationChannel(CHANNEL_ID) == null) {
                NotificationChannel ch = new NotificationChannel(CHANNEL_ID, "组网连接",
                        NotificationManager.IMPORTANCE_LOW);
                ch.setShowBadge(false);
                nm.createNotificationChannel(ch);
            }
        }
        startForeground(NOTIFY_ID, buildNotification(text));
    }

    private void updateNotification(String text) {
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (nm != null) {
            nm.notify(NOTIFY_ID, buildNotification(text));
        }
    }

    private Notification buildNotification(String text) {
        Intent open = new Intent(this, MainActivity.class);
        open.setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            flags |= PendingIntent.FLAG_IMMUTABLE;
        }
        PendingIntent pi = PendingIntent.getActivity(this, 0, open, flags);

        Notification.Builder b = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);
        b.setContentTitle("艾派互联");
        b.setContentText(text);
        b.setSmallIcon(android.R.drawable.ic_dialog_info);
        b.setContentIntent(pi);
        b.setOngoing(true);
        return b.build();
    }
}
