package cn.appiie.net;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.net.VpnService;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.text.SimpleDateFormat;
import java.net.ConnectException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** 主界面：注册 / 登录 → 选组网 → 连接。 */
public class MainActivity extends Activity {

    private static final int REQ_VPN = 1001;
    private static final String SITE = "https://net.appiie.cn";

    private static final int BLUE = 0xFF2563EB;
    private static final int GREEN = 0xFF16A34A;
    private static final int RED = 0xFFDC2626;
    private static final int GRAY = 0xFF6B7280;
    private static final int INK = 0xFF1E293B;

    private Prefs prefs;
    private final ExecutorService pool = Executors.newSingleThreadExecutor();
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final SimpleDateFormat clock = new SimpleDateFormat("HH:mm:ss", Locale.CHINA);

    private LinearLayout loginForm;
    private LinearLayout regForm;
    private LinearLayout mainBox;
    private TextView authError;
    private TextView accountText;
    private Button logoutBtn;
    private TextView statusText;
    private TextView subText;
    private TextView logText;
    private final StringBuilder logBuf = new StringBuilder();
    private LinearLayout netListBox;
    private Button connectBtn;
    private Button stopBtn;

    // 服务端开关：注册要不要邀请码。默认按"不要"显示（当前口径：注册免费）。
    private volatile boolean inviteRequired = false;
    private TextView regTip;
    private LinearLayout inviteRow;

    private final List<JSONObject> networks = new ArrayList<>();
    private int selectedIndex = -1;
    private JSONObject pendingConfig;

    private BroadcastReceiver stateReceiver;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefs = new Prefs(this);
        setContentView(buildUi());
        registerStateReceiver();

        if (prefs.token().isEmpty()) {
            showAuth();
        } else {
            showMain();
            refreshNetworks();
        }
    }

    /* ------------------------------------------------------------ 界面 */

    private View buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(0xFFF2F4F8);

        // 顶栏
        LinearLayout bar = new LinearLayout(this);
        bar.setOrientation(LinearLayout.HORIZONTAL);
        bar.setBackgroundColor(Color.WHITE);
        bar.setGravity(Gravity.CENTER_VERTICAL);
        bar.setPadding(dp(14), dp(12), dp(10), dp(12));

        TextView title = new TextView(this);
        title.setText("艾派互联");
        title.setTextSize(17);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        title.setTextColor(INK);
        bar.addView(title, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        TextView version = new TextView(this);
        version.setTextSize(11);
        version.setTextColor(0xFF9AA3B2);
        version.setText("v" + appVersion());
        version.setPadding(0, dp(4), dp(8), 0);
        bar.addView(version);

        accountText = new TextView(this);
        accountText.setTextSize(12);
        accountText.setTextColor(GRAY);
        accountText.setVisibility(View.GONE);
        bar.addView(accountText);

        logoutBtn = new Button(this);
        logoutBtn.setText("退出");
        logoutBtn.setTextSize(12);
        logoutBtn.setVisibility(View.GONE);
        logoutBtn.setOnClickListener(v -> {
            prefs.clearSession();
            shutdownTunnel();
            networks.clear();
            selectedIndex = -1;
            clearLog();
            showAuth();
        });
        bar.addView(logoutBtn);
        root.addView(bar);

        // 登录 / 注册
        LinearLayout auth = new LinearLayout(this);
        auth.setOrientation(LinearLayout.VERTICAL);
        auth.setPadding(dp(20), dp(18), dp(20), dp(10));
        auth.setBackgroundColor(Color.WHITE);

        authError = new TextView(this);
        authError.setTextSize(12);
        authError.setTextColor(RED);
        authError.setMinHeight(dp(18));

        loginForm = buildLoginForm();
        regForm = buildRegisterForm();
        regForm.setVisibility(View.GONE);
        auth.addView(loginForm);
        auth.addView(regForm);
        auth.addView(authError);
        root.addView(auth);

        // 主界面
        mainBox = new LinearLayout(this);
        mainBox.setOrientation(LinearLayout.VERTICAL);
        mainBox.setVisibility(View.GONE);

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackgroundColor(Color.WHITE);
        card.setPadding(dp(14), dp(12), dp(14), dp(12));
        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        cardParams.setMargins(dp(10), dp(10), dp(10), 0);

        statusText = new TextView(this);
        statusText.setTextSize(15);
        statusText.setTypeface(Typeface.DEFAULT_BOLD);
        statusText.setText("未连接");
        statusText.setTextColor(GRAY);
        card.addView(statusText);

        subText = new TextView(this);
        subText.setTextSize(12);
        subText.setTextColor(GRAY);
        subText.setPadding(0, dp(4), 0, dp(10));
        subText.setText("先在下面选一个组网");
        card.addView(subText);

        LinearLayout btnRow = new LinearLayout(this);
        btnRow.setOrientation(LinearLayout.HORIZONTAL);
        connectBtn = new Button(this);
        connectBtn.setText("一键连接");
        connectBtn.setBackgroundColor(BLUE);
        connectBtn.setTextColor(Color.WHITE);
        connectBtn.setOnClickListener(v -> connectSelected());
        btnRow.addView(connectBtn, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        stopBtn = new Button(this);
        stopBtn.setText("断开");
        stopBtn.setEnabled(false);
        stopBtn.setOnClickListener(v -> shutdownTunnel());
        btnRow.addView(stopBtn, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        card.addView(btnRow);
        mainBox.addView(card, cardParams);

        TextView netTitle = new TextView(this);
        netTitle.setText("我的组网");
        netTitle.setTextSize(12);
        netTitle.setTextColor(GRAY);
        netTitle.setPadding(dp(14), dp(12), 0, dp(4));
        mainBox.addView(netTitle);

        ScrollView netScroll = new ScrollView(this);
        netListBox = new LinearLayout(this);
        netListBox.setOrientation(LinearLayout.VERTICAL);
        netScroll.addView(netListBox);
        mainBox.addView(netScroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        LinearLayout actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.HORIZONTAL);
        actions.setPadding(dp(10), dp(6), dp(10), 2);
        actions.addView(smallButton("创建组网", v -> askCreate()));
        actions.addView(smallButton("加入组网", v -> askJoin()));
        actions.addView(smallButton("刷新", v -> refreshNetworks()));
        mainBox.addView(actions);

        LinearLayout logBar = new LinearLayout(this);
        logBar.setOrientation(LinearLayout.HORIZONTAL);
        logBar.setGravity(Gravity.CENTER_VERTICAL);
        logBar.setPadding(dp(14), dp(8), dp(10), 0);
        TextView logTitle = new TextView(this);
        logTitle.setText("运行日志");
        logTitle.setTextSize(12);
        logTitle.setTextColor(GRAY);
        logBar.addView(logTitle, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        Button copyLog = new Button(this);
        copyLog.setText("复制日志");
        copyLog.setTextSize(11);
        copyLog.setPadding(dp(8), 0, dp(8), 0);
        copyLog.setOnClickListener(v -> copyLog());
        logBar.addView(copyLog);
        mainBox.addView(logBar);

        ScrollView logScroll = new ScrollView(this);
        logText = new TextView(this);
        logText.setTextSize(11);
        logText.setTextColor(GRAY);
        logText.setPadding(dp(14), dp(4), dp(14), dp(8));
        logScroll.addView(logText);
        mainBox.addView(logScroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, dp(110)));

        root.addView(mainBox, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));
        return root;
    }

    private LinearLayout buildLoginForm() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);

        EditText user = new EditText(this);
        user.setHint("账号");
        user.setSingleLine(true);
        user.setText(prefs.username());
        box.addView(user, fieldParams());

        EditText pwd = new EditText(this);
        pwd.setHint("密码");
        pwd.setSingleLine(true);
        pwd.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        pwd.setText(prefs.password());
        box.addView(pwd, fieldParams());

        CheckBox remember = new CheckBox(this);
        remember.setText("记住密码");
        remember.setTextSize(13);
        remember.setChecked(prefs.remember());
        box.addView(remember);

        Button doLogin = new Button(this);
        doLogin.setText("登 录");
        doLogin.setBackgroundColor(BLUE);
        doLogin.setTextColor(Color.WHITE);
        doLogin.setOnClickListener(v -> doLogin(user.getText().toString().trim(),
                pwd.getText().toString(), remember.isChecked()));
        box.addView(doLogin, fieldParams());

        Button toRegister = new Button(this);
        toRegister.setText("注册新账号");
        toRegister.setTextSize(13);
        toRegister.setOnClickListener(v -> {
            authError.setText("");
            loginForm.setVisibility(View.GONE);
            regForm.setVisibility(View.VISIBLE);
        });
        box.addView(toRegister, fieldParams());
        return box;
    }

    private LinearLayout buildRegisterForm() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);

        TextView tip = new TextView(this);
        tip.setTextSize(12);
        tip.setTextColor(GRAY);
        tip.setText("注册免费。填好邮箱收验证码即可，邀请码一般不填。");
        box.addView(tip);
        regTip = tip;

        EditText user = new EditText(this);
        user.setHint("账号");
        user.setSingleLine(true);
        box.addView(user, fieldParams());

        EditText pwd = new EditText(this);
        pwd.setHint("密码（至少 6 位）");
        pwd.setSingleLine(true);
        pwd.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        box.addView(pwd, fieldParams());

        EditText pwd2 = new EditText(this);
        pwd2.setHint("再输一次密码");
        pwd2.setSingleLine(true);
        pwd2.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        box.addView(pwd2, fieldParams());

        EditText email = new EditText(this);
        email.setHint("邮箱（用于收验证码和找回密码）");
        email.setSingleLine(true);
        email.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_EMAIL_ADDRESS);
        box.addView(email, fieldParams());

        LinearLayout codeRow = new LinearLayout(this);
        codeRow.setOrientation(LinearLayout.HORIZONTAL);
        EditText code = new EditText(this);
        code.setHint("邮箱验证码");
        code.setSingleLine(true);
        codeRow.addView(code, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        Button sendCode = new Button(this);
        sendCode.setText("获取验证码");
        sendCode.setTextSize(12);
        sendCode.setOnClickListener(v -> sendEmailCode(email.getText().toString().trim(), sendCode));
        codeRow.addView(sendCode);
        box.addView(codeRow, fieldParams());

        LinearLayout inviteRow = new LinearLayout(this);
        inviteRow.setOrientation(LinearLayout.HORIZONTAL);
        EditText invite = new EditText(this);
        invite.setHint("邀请码");
        invite.setSingleLine(true);
        inviteRow.addView(invite, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        inviteRow.setVisibility(View.GONE);
        box.addView(inviteRow, fieldParams());
        this.inviteRow = inviteRow;

        Button doReg = new Button(this);
        doReg.setText("注册并登录");
        doReg.setBackgroundColor(BLUE);
        doReg.setTextColor(Color.WHITE);
        doReg.setOnClickListener(v -> doRegister(
                user.getText().toString().trim(),
                pwd.getText().toString(),
                pwd2.getText().toString(),
                email.getText().toString().trim(),
                code.getText().toString().trim(),
                invite.getText().toString().trim(),
                true));
        box.addView(doReg, fieldParams());

        Button back = new Button(this);
        back.setText("返回登录");
        back.setTextSize(13);
        back.setOnClickListener(v -> {
            authError.setText("");
            regForm.setVisibility(View.GONE);
            loginForm.setVisibility(View.VISIBLE);
        });
        box.addView(back, fieldParams());

        loadRegisterPolicy();
        return box;
    }

    /** 拉一次服务端配置：注册到底要不要邀请码。拉不到就按不需要处理。 */
    private void loadRegisterPolicy() {
        pool.execute(() -> {
            try {
                final JSONObject cfg = Api.post("public_config", new JSONObject(), null);
                final boolean need = cfg.optBoolean("register_invite_required", false);
                ui.post(() -> applyRegisterPolicy(need));
            } catch (Exception ignored) {
                // 网络不通时保持默认（不要邀请码），真正注册时服务端还会再判一次
            }
        });
    }

    private void applyRegisterPolicy(boolean need) {
        inviteRequired = need;
        if (regTip != null) {
            regTip.setText(need
                    ? "注册需要邀请码，没有的话到官网获取。"
                    : "注册免费。填好邮箱收验证码即可，邀请码一般不填。");
        }
        if (inviteRow != null) {
            inviteRow.setVisibility(need ? View.VISIBLE : View.GONE);
        }
    }

    private LinearLayout.LayoutParams fieldParams() {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        p.setMargins(0, dp(6), 0, 0);
        return p;
    }

    private Button smallButton(String text, View.OnClickListener click) {
        Button b = new Button(this);
        b.setText(text);
        b.setTextSize(12);
        b.setPadding(0, 0, 0, 0);
        b.setOnClickListener(click);
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        p.setMargins(dp(2), 0, dp(2), 0);
        b.setLayoutParams(p);
        return b;
    }

    private int dp(int v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v,
                getResources().getDisplayMetrics());
    }

    private String appVersion() {
        try {
            return getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
        } catch (Exception e) {
            return "?";
        }
    }

    private void showAuth() {
        loginForm.setVisibility(View.VISIBLE);
        regForm.setVisibility(View.GONE);
        mainBox.setVisibility(View.GONE);
        accountText.setVisibility(View.GONE);
        logoutBtn.setVisibility(View.GONE);
    }

    private void showMain() {
        loginForm.setVisibility(View.GONE);
        regForm.setVisibility(View.GONE);
        mainBox.setVisibility(View.VISIBLE);
        accountText.setVisibility(View.VISIBLE);
        accountText.setText(prefs.username());
        logoutBtn.setVisibility(View.VISIBLE);
    }

    private void openUrl(String url) {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url)));
        } catch (Exception e) {
            toast("没有可用的浏览器，请手动访问 " + url);
        }
    }

    /* ------------------------------------------------------------ 日志 */

    private void log(String line) {
        logBuf.append(clock.format(new Date())).append("  ").append(line).append('\n');
        if (logBuf.length() > 6000) {
            logBuf.delete(0, logBuf.length() - 6000);
        }
        logText.setText(logBuf.toString());
    }

    private void clearLog() {
        logBuf.setLength(0);
        logText.setText("");
    }

    private void copyLog() {
        try {
            ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            if (cm != null) {
                cm.setPrimaryClip(ClipData.newPlainText("艾派互联日志", logBuf.toString()));
                toast("日志已复制");
            }
        } catch (Exception e) {
            toast("复制失败：" + e.getMessage());
        }
    }

    /* ------------------------------------------------------------ 注册 / 登录 */

    private void sendEmailCode(final String email, final Button btn) {
        if (email.isEmpty()) {
            authError.setText("先填邮箱");
            return;
        }
        btn.setEnabled(false);
        btn.setText("发送中…");
        pool.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("email", email);
                body.put("purpose", "register");
                Api.post("send_email_code", body, null);
                ui.post(() -> {
                    authError.setTextColor(GREEN);
                    authError.setText("验证码已发到 " + email + "，10 分钟内有效");
                    countdown(btn);
                });
            } catch (final Exception e) {
                ui.post(() -> {
                    authError.setTextColor(RED);
                    authError.setText("发送失败：" + e.getMessage());
                    btn.setEnabled(true);
                    btn.setText("获取验证码");
                });
            }
        });
    }

    private void countdown(final Button btn) {
        btn.setEnabled(false);
        final int[] left = {60};
        final Runnable tick = new Runnable() {
            @Override
            public void run() {
                left[0]--;
                if (left[0] <= 0) {
                    btn.setText("获取验证码");
                    btn.setEnabled(true);
                    return;
                }
                btn.setText(left[0] + "s");
                ui.postDelayed(this, 1000);
            }
        };
        ui.postDelayed(tick, 1000);
    }

    private void doRegister(final String username, final String pwd, final String pwd2,
                            final String email, final String code, final String invite,
                            final boolean remember) {
        authError.setTextColor(RED);
        if (username.isEmpty() || pwd.isEmpty()) {
            authError.setText("账号和密码都要填");
            return;
        }
        if (!pwd.equals(pwd2)) {
            authError.setText("两次输入的密码不一样");
            return;
        }
        if (pwd.length() < 6) {
            authError.setText("密码至少 6 位");
            return;
        }
        if (inviteRequired && invite.isEmpty()) {
            authError.setText("这个阶段注册需要邀请码");
            return;
        }
        authError.setText("注册中…");
        pool.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("username", username);
                body.put("password", pwd);
                body.put("email", email);
                body.put("invite_code", invite);
                body.put("email_code", code);
                Api.post("register", body, null);
                ui.post(() -> {
                    authError.setTextColor(GREEN);
                    authError.setText("注册成功，正在登录…");
                    doLogin(username, pwd, remember);
                });
            } catch (final Exception e) {
                ui.post(() -> {
                    authError.setTextColor(RED);
                    authError.setText("注册失败：" + e.getMessage());
                });
            }
        });
    }

    private void doLogin(final String username, final String password, final boolean remember) {
        if (username.isEmpty() || password.isEmpty()) {
            authError.setText("账号和密码都要填");
            return;
        }
        authError.setTextColor(GRAY);
        authError.setText("登录中…");
        pool.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("username", username);
                body.put("password", password);
                body.put("device_uid", prefs.deviceUid());
                body.put("device_name", Build.MODEL == null ? "Android" : Build.MODEL);
                body.put("platform", "android");
                final JSONObject data = Api.post("login", body, null);

                prefs.setToken(data.optString("token", ""));
                prefs.setUsername(username);
                prefs.setRemember(remember);
                prefs.setPassword(remember ? password : "");

                ui.post(() -> {
                    authError.setText("");
                    clearLog();
                    showMain();
                    log("已登录：" + username);
                    refreshNetworks();
                });
            } catch (final Exception e) {
                ui.post(() -> {
                    authError.setTextColor(RED);
                    authError.setText(e.getMessage());
                });
            }
        });
    }

    /* ------------------------------------------------------------ 组网列表 */

    private void refreshNetworks() {
        log("正在读取组网…");
        pool.execute(() -> {
            try {
                final JSONObject data = Api.post("network_list", null, prefs.token());
                final JSONArray arr = data.optJSONArray("networks");
                ui.post(() -> {
                    networks.clear();
                    selectedIndex = -1;
                    if (arr != null) {
                        for (int i = 0; i < arr.length(); i++) {
                            JSONObject n = arr.optJSONObject(i);
                            if (n != null) {
                                networks.add(n);
                            }
                        }
                    }
                    if (!networks.isEmpty()) {
                        int want = prefs.networkId();
                        for (int i = 0; i < networks.size(); i++) {
                            if (networks.get(i).optInt("id") == want) {
                                selectedIndex = i;
                                break;
                            }
                        }
                        if (selectedIndex < 0) {
                            selectedIndex = 0;
                        }
                    }
                    renderNetworks();
                    log(networks.isEmpty()
                            ? "还没有组网，点「创建组网」，或者用别人的适配码「加入组网」。"
                            : "共 " + networks.size() + " 个组网。");
                });
            } catch (final Exception e) {
                ui.post(() -> log("读取组网失败：" + e.getMessage()));
            }
        });
    }

    private void renderNetworks() {
        netListBox.removeAllViews();
        if (networks.isEmpty()) {
            TextView empty = new TextView(this);
            empty.setText("还没有组网");
            empty.setTextSize(13);
            empty.setTextColor(GRAY);
            empty.setPadding(dp(14), dp(8), dp(14), dp(8));
            netListBox.addView(empty);
            return;
        }
        for (int i = 0; i < networks.size(); i++) {
            final int index = i;
            JSONObject n = networks.get(i);
            boolean on = index == selectedIndex;
            boolean owner = "owner".equals(n.optString("role"));

            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.VERTICAL);
            row.setPadding(dp(14), dp(10), dp(14), dp(10));
            row.setBackgroundColor(on ? 0xFFEAF1FF : Color.WHITE);
            row.setOnClickListener(v -> {
                selectedIndex = index;
                prefs.setNetworkId(n.optInt("id"));
                renderNetworks();
            });

            TextView name = new TextView(this);
            name.setTextSize(14);
            name.setTypeface(Typeface.DEFAULT_BOLD);
            name.setTextColor(owner ? GREEN : INK);
            name.setText(n.optString("name", "-") + (owner ? "　房主" : ""));
            row.addView(name);

            TextView meta = new TextView(this);
            meta.setTextSize(12);
            meta.setTextColor(GRAY);
            String ip = n.optString("device_ipv4", "");
            if (ip.isEmpty()) {
                ip = n.optString("ipv4", "-");
            }
            meta.setText("适配码 " + n.optString("code", "-")
                    + "　本机IP " + ip
                    + "　" + n.optString("server_location", ""));
            row.addView(meta);

            TextView status = new TextView(this);
            status.setTextSize(11);
            boolean active = "active".equals(n.optString("anchor_status"));
            status.setTextColor(active ? GREEN : 0xFFB45309);
            status.setText(active ? "服务端就绪" : "服务端锚点还没起来，稍等再连");
            row.addView(status);

            netListBox.addView(row, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        }
    }

    private void askCreate() {
        final EditText input = new EditText(this);
        input.setHint("给这个组网起个名字");
        new AlertDialog.Builder(this)
                .setTitle("创建组网")
                .setView(input)
                .setPositiveButton("创建", (d, w) -> {
                    String name = input.getText().toString().trim();
                    if (name.isEmpty()) {
                        toast("名字不能为空");
                        return;
                    }
                    createNetwork(name);
                })
                .setNegativeButton("取消", null)
                .show();
    }

    private void createNetwork(final String name) {
        log("正在创建组网…");
        pool.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("name", name);
                body.put("node_id", pickFastestNode());
                final JSONObject cfg = Api.post("network_create", body, prefs.token());
                ui.post(() -> {
                    toast("创建成功，适配码 " + cfg.optString("code"));
                    log("创建成功，适配码 " + cfg.optString("code"));
                    prefs.setNetworkId(cfg.optInt("id"));
                    refreshNetworks();
                });
            } catch (final Exception e) {
                ui.post(() -> log("创建失败：" + e.getMessage()));
            }
        });
    }

    /**
     * 节点按延迟自动挑：并发对每个节点做 TCP 握手，拿握手耗时当延迟，取最快的那台。
     * 为什么不用 ping：安卓上发 ICMP 要 root，而且很多网络本身就把 ICMP 丢了；
     * TCP 握手走的是真实路径，端口没人监听时的 RST 返回一样能算出往返时间。
     */
    private String pickFastestNode() {
        try {
            JSONObject res = Api.post("nodes", new JSONObject(), prefs.token());
            final JSONArray arr = res.optJSONArray("nodes");
            if (arr == null || arr.length() == 0) { return ""; }
            if (arr.length() == 1) { return arr.optJSONObject(0).optString("id"); }

            final int[] rtt = new int[arr.length()];
            Thread[] ts = new Thread[arr.length()];
            for (int i = 0; i < arr.length(); i++) {
                final JSONObject n = arr.optJSONObject(i);
                final int idx = i;
                rtt[i] = -1;
                ts[i] = new Thread(() -> rtt[idx] = tcpRtt(n.optString("host"), n.optInt("probe_port", 443)));
                ts[i].start();
            }
            for (Thread t : ts) { t.join(3000); }

            int best = -1;
            for (int i = 0; i < arr.length(); i++) {
                if (rtt[i] > 0 && (best < 0 || rtt[i] < rtt[best])) { best = i; }
            }
            if (best < 0) { return ""; }
            log("节点已按延迟自动选择：" + arr.optJSONObject(best).optString("name") + " " + rtt[best] + "ms");
            return arr.optJSONObject(best).optString("id");
        } catch (Exception e) {
            return "";
        }
    }

    /** TCP 握手耗时（毫秒），测两次取快的；测不到返回 -1 */
    private static int tcpRtt(String host, int port) {
        if (host == null || host.isEmpty()) { return -1; }
        if (port <= 0) { port = 443; }
        int best = -1;
        for (int i = 0; i < 2; i++) {
            Socket s = new Socket();
            long t0 = System.currentTimeMillis();
            try {
                s.connect(new InetSocketAddress(host, port), 1500);
                int ms = (int) (System.currentTimeMillis() - t0);
                if (ms > 0 && (best < 0 || ms < best)) { best = ms; }
            } catch (ConnectException ce) {
                // 端口没人监听：立刻回 RST，这一样是有效的往返时间
                int ms = (int) (System.currentTimeMillis() - t0);
                if (ms > 0 && (best < 0 || ms < best)) { best = ms; }
            } catch (Exception ignore) {
            } finally {
                try { s.close(); } catch (Exception ignore) { }
            }
        }
        return best;
    }

    private void askJoin() {
        final EditText input = new EditText(this);
        input.setHint("8 位适配码");
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS);
        new AlertDialog.Builder(this)
                .setTitle("加入组网")
                .setView(input)
                .setPositiveButton("加入", (d, w) -> {
                    String code = input.getText().toString().trim().toUpperCase();
                    if (code.length() != 8) {
                        toast("适配码是 8 位");
                        return;
                    }
                    joinNetwork(code);
                })
                .setNegativeButton("取消", null)
                .show();
    }

    private void joinNetwork(final String code) {
        log("正在加入…");
        pool.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("code", code);
                final JSONObject cfg = Api.post("network_join", body, prefs.token());
                ui.post(() -> {
                    toast("已加入「" + cfg.optString("name") + "」");
                    log("已加入「" + cfg.optString("name") + "」，本机IP " + cfg.optString("ipv4"));
                    prefs.setNetworkId(cfg.optInt("id"));
                    refreshNetworks();
                });
            } catch (final Exception e) {
                ui.post(() -> log("加入失败：" + e.getMessage()));
            }
        });
    }

    /* ------------------------------------------------------------ 连接 */

    private void connectSelected() {
        if (selectedIndex < 0 || selectedIndex >= networks.size()) {
            toast("先选一个组网");
            return;
        }
        final int id = networks.get(selectedIndex).optInt("id");
        final String code = networks.get(selectedIndex).optString("code", "");
        final String netName = networks.get(selectedIndex).optString("name", "");
        log("准备连接「" + netName + "」…");
        connectBtn.setEnabled(false);
        pool.execute(() -> {
            try {
                JSONObject body = new JSONObject();
                body.put("network_id", id);
                JSONObject cfg;
                try {
                    cfg = Api.post("network_config", body, prefs.token());
                } catch (Api.ApiError e) {
                    // 账号在别的设备上进过这个组网，这台设备还没有成员记录，
                    // 用适配码补一次加入再取配置。
                    if (e.getMessage() != null && e.getMessage().contains("不在该组网")
                            && code.length() == 8) {
                        ui.post(() -> log("这台设备还没进过这个组网，先补一次加入…"));
                        JSONObject join = new JSONObject();
                        join.put("code", code);
                        Api.post("network_join", join, prefs.token());
                        cfg = Api.post("network_config", body, prefs.token());
                    } else {
                        throw e;
                    }
                }
                final JSONObject ready = cfg;
                ui.post(() -> {
                    log("配置已获取：IP " + ready.optString("ipv4")
                            + "，锚点 " + ready.optString("server_host")
                            + ":" + ready.optInt("anchor_port", 11010));
                    startTunnel(ready);
                });
            } catch (final Exception e) {
                ui.post(() -> {
                    connectBtn.setEnabled(true);
                    log("获取配置失败：" + e.getMessage());
                });
            }
        });
    }

    private void startTunnel(JSONObject cfg) {
        String ipv4 = Tunnel.plain(cfg.optString("ipv4", ""));
        if (ipv4.isEmpty()) {
            connectBtn.setEnabled(true);
            log("服务端没下发虚拟 IP，刷新一下组网再试。");
            return;
        }
        pendingConfig = cfg;

        Intent consent = VpnService.prepare(this);
        if (consent != null) {
            log("等待系统授权 VPN…");
            startActivityForResult(consent, REQ_VPN);
            return;
        }
        launchService(cfg);
    }

    private void launchService(JSONObject cfg) {
        String ipv4 = Tunnel.plain(cfg.optString("ipv4", ""));
        String ipv6 = Tunnel.plain(cfg.optString("ipv6", ""));
        Intent intent = new Intent(this, AipaiVpnService.class);
        intent.setAction(AipaiVpnService.ACTION_START);
        intent.putExtra(AipaiVpnService.EXTRA_CONFIG, Tunnel.toml(cfg, false));
        intent.putExtra(AipaiVpnService.EXTRA_IPV4, ipv4);
        intent.putExtra(AipaiVpnService.EXTRA_SUBNET, Tunnel.subnetOf(ipv4));
        intent.putExtra(AipaiVpnService.EXTRA_IPV6, ipv6);
        intent.putExtra(AipaiVpnService.EXTRA_V6SUBNET, Tunnel.v6SubnetOf(ipv6));
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent);
        } else {
            startService(intent);
        }
        log("已交给后台服务，正在建立通道…");
        if (cfg.optBoolean("relay_blocked", false)) {
            log("本月中继流量已用完，这次只走直连。直连不经过服务器，不受额度影响。");
        }
        subText.setText("本机IP " + ipv4 + "　连接中…");
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQ_VPN) {
            return;
        }
        if (resultCode == RESULT_OK && pendingConfig != null) {
            log("已获得 VPN 授权");
            launchService(pendingConfig);
        } else {
            connectBtn.setEnabled(true);
            log("没有授予 VPN 权限，连不上。");
        }
    }

    private void shutdownTunnel() {
        Intent intent = new Intent(this, AipaiVpnService.class);
        intent.setAction(AipaiVpnService.ACTION_STOP);
        startService(intent);
        connectBtn.setEnabled(true);
        stopBtn.setEnabled(false);
        statusText.setText("未连接");
        statusText.setTextColor(GRAY);
        subText.setText("已断开");
    }

    private void registerStateReceiver() {
        stateReceiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                if (intent == null) {
                    return;
                }
                String traceLine = intent.getStringExtra(AipaiVpnService.EXTRA_TRACE);
                if (traceLine != null) {
                    log(traceLine);
                    return;
                }
                String text = intent.getStringExtra(AipaiVpnService.EXTRA_TEXT);
                boolean on = intent.getBooleanExtra(AipaiVpnService.EXTRA_RUNNING, false);
                statusText.setText(on ? "已连接" : "未连接");
                statusText.setTextColor(on ? GREEN : GRAY);
                if (!TextUtils.isEmpty(text)) {
                    subText.setText(text);
                    if (!on) {
                        log(text);
                    }
                }
                connectBtn.setEnabled(!on);
                stopBtn.setEnabled(on);
            }
        };
        IntentFilter filter = new IntentFilter(AipaiVpnService.ACTION_STATE);
        if (Build.VERSION.SDK_INT >= 33) {
            registerReceiver(stateReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(stateReceiver, filter);
        }
    }

    @Override
    protected void onDestroy() {
        if (stateReceiver != null) {
            unregisterReceiver(stateReceiver);
            stateReceiver = null;
        }
        super.onDestroy();
    }

    private void toast(String text) {
        Toast.makeText(this, text, Toast.LENGTH_SHORT).show();
    }
}
