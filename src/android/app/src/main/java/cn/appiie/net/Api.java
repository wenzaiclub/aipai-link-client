package cn.appiie.net;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/** 后端接口的薄封装，字段跟 Windows 客户端保持一致。 */
public final class Api {

    public static final String BASE = "https://net.appiie.cn/api.php";

    private Api() {
    }

    /** 后端返回 ok=false 时抛这个，message 直接给用户看。 */
    public static class ApiError extends Exception {
        public final int http;

        public ApiError(String message, int http) {
            super(message);
            this.http = http;
        }
    }

    public static JSONObject post(String route, JSONObject body, String token) throws Exception {
        HttpURLConnection conn = (HttpURLConnection) new URL(BASE + "?r=" + route).openConnection();
        conn.setConnectTimeout(15000);
        conn.setReadTimeout(25000);
        conn.setRequestMethod("POST");
        conn.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        conn.setRequestProperty("Accept", "application/json");
        if (token != null && !token.isEmpty()) {
            conn.setRequestProperty("Authorization", "Bearer " + token);
        }
        conn.setDoOutput(true);
        byte[] payload = (body == null ? new JSONObject() : body)
                .toString().getBytes(StandardCharsets.UTF_8);
        conn.setFixedLengthStreamingMode(payload.length);
        try (OutputStream os = conn.getOutputStream()) {
            os.write(payload);
        }

        int code = conn.getResponseCode();
        String text = readAll(code >= 400 ? conn.getErrorStream() : conn.getInputStream());

        JSONObject envelope;
        try {
            envelope = new JSONObject(text);
        } catch (Exception e) {
            throw new ApiError("服务器没有返回正常内容（HTTP " + code + "）", code);
        }
        if (!envelope.optBoolean("ok")) {
            JSONObject data = envelope.optJSONObject("data");
            String msg = data != null ? data.optString("message", "请求失败") : "请求失败";
            throw new ApiError(msg, code);
        }
        JSONObject data = envelope.optJSONObject("data");
        return data == null ? new JSONObject() : data;
    }

    private static String readAll(InputStream in) throws Exception {
        if (in == null) {
            return "";
        }
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[4096];
        int n;
        while ((n = in.read(buf)) > 0) {
            bos.write(buf, 0, n);
        }
        in.close();
        return new String(bos.toByteArray(), StandardCharsets.UTF_8);
    }
}
