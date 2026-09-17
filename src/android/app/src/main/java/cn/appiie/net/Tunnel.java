package cn.appiie.net;

import org.json.JSONObject;

import java.net.InetAddress;

/** 把后端下发的组网参数拼成 EasyTier 的 TOML 配置。 */
public final class Tunnel {

    public static final String INSTANCE = "aipai";

    private Tunnel() {
    }

    public static String toml(JSONObject cfg, boolean broadcastRelay) {
        String ipv4 = plain(cfg.optString("ipv4", ""));
        String ipv6 = plain(cfg.optString("ipv6", ""));
        // 本月中继额度用完时只走直连；P2P 不经过锚点，所以不受额度影响
        boolean p2pOnly = cfg.optBoolean("relay_blocked", false);
        String netName = cfg.optString("ez_network_name", "");
        String secret = cfg.optString("ez_secret", "");
        String host = cfg.optString("server_host", "");
        int port = cfg.optInt("anchor_port", 11010);

        StringBuilder sb = new StringBuilder();
        sb.append("instance_name = \"").append(INSTANCE).append("\"\n");
        sb.append("hostname = \"android\"\n");
        if (!ipv4.isEmpty()) {
            sb.append("ipv4 = \"").append(ipv4).append("\"\n");
        }
        if (!ipv6.isEmpty()) {
            sb.append("ipv6 = \"").append(ipv6).append("\"\n");
        }
        sb.append('\n');

        sb.append("[network_identity]\n");
        sb.append("network_name = \"").append(netName).append("\"\n");
        if (!secret.isEmpty()) {
            sb.append("network_secret = \"").append(secret).append("\"\n");
        }
        sb.append('\n');

        // 先连服务端锚点，进来之后再和别的节点打洞
        sb.append("[[peer]]\n");
        sb.append("uri = \"tcp://").append(host).append(':').append(port).append("\"\n");
        sb.append("[[peer]]\n");
        sb.append("uri = \"udp://").append(host).append(':').append(port).append("\"\n");
        sb.append('\n');

        sb.append("listeners = [\n");
        sb.append("  \"tcp://0.0.0.0:11010\",\n");
        sb.append("  \"udp://0.0.0.0:11010\",\n");
        sb.append("]\n\n");

        sb.append("[flags]\n");
        sb.append("enable_encryption = true\n");
        sb.append("enable_ipv6 = true\n");
        sb.append("mtu = 1380\n");
        sb.append("no_tun = false\n");
        sb.append("bind_device = false\n");
        sb.append("p2p_only = ").append(p2pOnly).append('\n');
        sb.append("enable_udp_broadcast_relay = ").append(broadcastRelay).append('\n');

        return sb.toString();
    }

    /** 10.0.0.5 -> 10.0.0.0 */
    public static String subnetOf(String ipv4) {
        ipv4 = plain(ipv4);
        int i = ipv4.lastIndexOf('.');
        return i > 0 ? ipv4.substring(0, i) + ".0" : "";
    }

    /**
     * 取 IPv6 的 /64 网段。
     *
     * 不能简单地按 ":" 切前 4 段：服务端下发的是压缩写法（fd62:e027:5d3a::7），
     * 切出来会变成 fd62:e027:5d3a::: 这种非法地址，addRoute 会直接抛异常。
     * 这里按字节处理，稳妥一些。
     */
    public static String v6SubnetOf(String ipv6) {
        String raw = plain(ipv6);
        if (raw.isEmpty()) {
            return "";
        }
        try {
            byte[] bytes = InetAddress.getByName(raw).getAddress();
            if (bytes.length != 16) {
                return "";
            }
            for (int i = 8; i < 16; i++) {
                bytes[i] = 0;
            }
            return InetAddress.getByAddress(bytes).getHostAddress();
        } catch (Exception e) {
            return "";
        }
    }

    /** 去掉 "10.0.0.1/24" 里的前缀长度，VpnService.Builder 不认带斜杠的写法。 */
    public static String plain(String addr) {
        if (addr == null) {
            return "";
        }
        String v = addr.trim();
        int i = v.indexOf('/');
        return i > 0 ? v.substring(0, i) : v;
    }
}
