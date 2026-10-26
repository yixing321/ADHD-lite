package com.adhd.lite;

import android.Manifest;
import android.app.Activity;
import android.app.AlarmManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.PowerManager;
import android.provider.Settings;
import android.view.View;
import android.webkit.JavascriptInterface;
import android.webkit.ValueCallback;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;

public class MainActivity extends Activity {
    private WebView web;
    private String pendingTaskId = "";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Notifications.ensureChannel(this);

        web = new WebView(this);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(true);
        s.setAllowContentAccess(true);
        s.setAllowFileAccessFromFileURLs(true);
        s.setAllowUniversalAccessFromFileURLs(true);
        s.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);
        s.setSupportZoom(false);
        s.setBuiltInZoomControls(false);
        s.setJavaScriptCanOpenWindowsAutomatically(false);
        web.setBackgroundColor(0xfff5f7fb);
        web.setWebViewClient(new WebViewClient() {
            @Override
            public void onPageFinished(WebView view, String url) {
                super.onPageFinished(view, url);
                focusPendingTask();
            }
        });
        web.addJavascriptInterface(new Bridge(this), "AndroidHost");
        setContentView(web);
        // 用 http 基地址加载内嵌页面：origin 不是 file://，fetch 到桌面 HTTP API 不会被
        // WebView 的文件源限制拦住；页面本身不需要这个域名真的可解析。
        web.loadDataWithBaseURL("http://adhd.local/", readAsset("mobile.html"), "text/html", "UTF-8", null);
        handleIntent(getIntent());

        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, 101);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (web != null) web.evaluateJavascript("window.adhdResume && window.adhdResume()", null);
        focusPendingTask();
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleIntent(intent);
    }

    private void handleIntent(Intent intent) {
        if (intent == null) return;
        String id = intent.getStringExtra("taskId");
        if (id != null && id.length() > 0) {
            pendingTaskId = id;
            focusPendingTask();
        }
    }

    private void focusPendingTask() {
        if (web == null || pendingTaskId == null || pendingTaskId.length() == 0) return;
        String id = pendingTaskId;
        pendingTaskId = "";
        try {
            web.evaluateJavascript("window.adhdFocusTask && window.adhdFocusTask("
                    + JSONObject.quote(id) + ")", null);
        } catch (Exception ignored) { }
    }

    private String readAsset(String name) {
        try {
            java.io.InputStream in = getAssets().open(name);
            java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
            byte[] buf = new byte[4096];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
            in.close();
            return new String(out.toByteArray(), "UTF-8");
        } catch (Exception e) {
            return "<html><body style='background:#111;color:#eee;font-family:sans-serif;padding:24px'>页面加载失败：" + e.getMessage() + "</body></html>";
        }
    }

    @Override
    public void onBackPressed() {
        if (web == null) { super.onBackPressed(); return; }
        // 先问页面：设置页 → 主界面，非「今天」tab → 今天；页面不处理才退出。
        web.evaluateJavascript(
                "(function(){return window.adhdHandleBack?window.adhdHandleBack():false;})()",
                new ValueCallback<String>() {
                    @Override
                    public void onReceiveValue(String value) {
                        if ("true".equals(value)) return;
                        if (web != null && web.canGoBack()) web.goBack();
                        else finish();
                    }
                });
    }

    public static class Bridge {
        private final Context ctx;

        Bridge(Context c) { ctx = c; }

        @JavascriptInterface
        public void scheduleNotifications(final String json) {
            new Thread(new Runnable() {
                public void run() { AlarmScheduler.sync(ctx, json); }
            }).start();
        }

        @JavascriptInterface
        public void requestNotificationPermission() {
            if (Build.VERSION.SDK_INT >= 33 && ctx instanceof Activity) {
                ((Activity) ctx).runOnUiThread(new Runnable() {
                    public void run() {
                        ((Activity) ctx).requestPermissions(
                            new String[] { Manifest.permission.POST_NOTIFICATIONS }, 101);
                    }
                });
            }
        }

        @JavascriptInterface
        public void requestExactAlarm() {
            if (Build.VERSION.SDK_INT >= 31) {
                try {
                    Intent i = new Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM);
                    i.setData(Uri.parse("package:" + ctx.getPackageName()));
                    i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    ctx.startActivity(i);
                } catch (Exception ignored) { }
            }
        }

        @JavascriptInterface
        public boolean canExactAlarm() {
            if (Build.VERSION.SDK_INT < 31) return true;
            try {
                AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
                return am != null && am.canScheduleExactAlarms();
            } catch (Exception e) { return false; }
        }

        @JavascriptInterface
        public void testNotification() {
            AlarmScheduler.test(ctx);
        }

        @JavascriptInterface
        public void testBackgroundAlarm() {
            AlarmScheduler.testBackground(ctx);
        }

        @JavascriptInterface
        public String getStatus() {
            try {
                JSONObject o = new JSONObject();
                boolean notif = true;
                if (Build.VERSION.SDK_INT >= 24) {
                    android.app.NotificationManager nm =
                        (android.app.NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
                    notif = nm != null && nm.areNotificationsEnabled();
                }
                o.put("notifications", notif);
                o.put("channel", channelOn());
                o.put("sound", soundOn());
                o.put("exact", canExactAlarm());
                o.put("battery", ignoresBattery());
                o.put("fullscreen", fullScreenOn());
                o.put("scheduled", AlarmScheduler.scheduledCount(ctx));
                return o.toString();
            } catch (Exception e) {
                return "{}";
            }
        }

        private boolean channelOn() {
            if (Build.VERSION.SDK_INT < 26) return true;
            try {
                android.app.NotificationManager nm =
                        (android.app.NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
                if (nm == null) return false;
                android.app.NotificationChannel ch = nm.getNotificationChannel(Notifications.CHANNEL);
                return ch != null && ch.getImportance() != android.app.NotificationManager.IMPORTANCE_NONE;
            } catch (Exception e) { return false; }
        }

        private boolean soundOn() {
            if (Build.VERSION.SDK_INT < 26) return true;
            try {
                android.app.NotificationManager nm =
                        (android.app.NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
                if (nm == null) return false;
                android.app.NotificationChannel ch = nm.getNotificationChannel(Notifications.CHANNEL);
                return ch != null
                        && ch.getImportance() != android.app.NotificationManager.IMPORTANCE_NONE
                        && ch.getSound() != null;
            } catch (Exception e) { return false; }
        }

        private boolean fullScreenOn() {
            if (Build.VERSION.SDK_INT < 34) return true;
            try {
                android.app.NotificationManager nm =
                        (android.app.NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
                return nm != null && nm.canUseFullScreenIntent();
            } catch (Exception e) { return false; }
        }

        private boolean ignoresBattery() {
            if (Build.VERSION.SDK_INT < 23) return true;
            try {
                PowerManager pm = (PowerManager) ctx.getSystemService(Context.POWER_SERVICE);
                return pm != null && pm.isIgnoringBatteryOptimizations(ctx.getPackageName());
            } catch (Exception e) { return false; }
        }

        @JavascriptInterface
        public void requestFullScreen() {
            if (Build.VERSION.SDK_INT < 34) { openNotificationSettings(); return; }
            try {
                Intent i = new Intent(Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT,
                        Uri.parse("package:" + ctx.getPackageName()));
                i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                ctx.startActivity(i);
            } catch (Exception e) {
                openNotificationSettings();
            }
        }

        @JavascriptInterface
        public void requestIgnoreBattery() {
            if (Build.VERSION.SDK_INT < 23) return;
            try {
                Intent i = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                        Uri.parse("package:" + ctx.getPackageName()));
                i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                ctx.startActivity(i);
            } catch (Exception e) {
                try {
                    Intent i = new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS);
                    i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    ctx.startActivity(i);
                } catch (Exception ignored) { }
            }
        }

        @JavascriptInterface
        public void openNotificationSettings() {
            try {
                if (Build.VERSION.SDK_INT >= 26) {
                    Intent i = new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS);
                    i.putExtra(Settings.EXTRA_APP_PACKAGE, ctx.getPackageName());
                    i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    ctx.startActivity(i);
                } else {
                    Intent i = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                            Uri.parse("package:" + ctx.getPackageName()));
                    i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    ctx.startActivity(i);
                }
            } catch (Exception ignored) { }
        }

        @JavascriptInterface
        public void openChannelSettings() {
            if (Build.VERSION.SDK_INT < 26) { openNotificationSettings(); return; }
            try {
                Intent i = new Intent(Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS);
                i.putExtra(Settings.EXTRA_APP_PACKAGE, ctx.getPackageName());
                i.putExtra(Settings.EXTRA_CHANNEL_ID, Notifications.CHANNEL);
                i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                ctx.startActivity(i);
            } catch (Exception e) {
                openNotificationSettings();
            }
        }

        @JavascriptInterface
        public String getVersion() {
            try {
                return ctx.getPackageManager().getPackageInfo(ctx.getPackageName(), 0).versionName;
            } catch (Exception e) { return ""; }
        }

        /**
         * v0.11：从电脑端 /apk 下载最新 APK 并交给系统安装器。
         * Android 不允许静默安装，所以这里是「一键下载 + 系统确认」。
         */
        @JavascriptInterface
        public void downloadUpdate(final String url, final String token) {
            new Thread(new Runnable() {
                public void run() {
                    try {
                        File out = new File(ctx.getCacheDir(), ApkProvider.FILE_NAME);
                        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
                        c.setConnectTimeout(10000);
                        c.setReadTimeout(30000);
                        c.setRequestProperty("X-ADHD-Token", token == null ? "" : token);
                        int code = c.getResponseCode();
                        if (code != 200) throw new Exception("HTTP " + code + "（电脑上可能没有 APK）");
                        InputStream in = c.getInputStream();
                        FileOutputStream fos = new FileOutputStream(out);
                        try {
                            byte[] buf = new byte[8192];
                            int n;
                            while ((n = in.read(buf)) > 0) fos.write(buf, 0, n);
                        } finally {
                            fos.close();
                            in.close();
                        }
                        if (out.length() < 1024) throw new Exception("下载的 APK 不完整");

                        if (Build.VERSION.SDK_INT >= 26
                                && !ctx.getPackageManager().canRequestPackageInstalls()) {
                            showToast("请先允许「安装未知应用」，再点一次更新");
                            Intent i = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                                    Uri.parse("package:" + ctx.getPackageName()));
                            i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                            ctx.startActivity(i);
                            return;
                        }
                        Intent i = new Intent(Intent.ACTION_VIEW);
                        i.setDataAndType(ApkProvider.uriFor(ctx, out),
                                "application/vnd.android.package-archive");
                        i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION
                                | Intent.FLAG_ACTIVITY_NEW_TASK);
                        ctx.startActivity(i);
                        showToast("下载完成，请按系统提示安装");
                    } catch (Exception e) {
                        showToast("更新失败：" + e.getMessage());
                    }
                }
            }).start();
        }

        @JavascriptInterface
        public void showToast(final String text) {
            if (ctx instanceof Activity) {
                ((Activity) ctx).runOnUiThread(new Runnable() {
                    public void run() {
                        Toast.makeText(ctx, text == null ? "" : text, Toast.LENGTH_SHORT).show();
                    }
                });
            }
        }

        @JavascriptInterface
        public String getInfo() {
            return "Android " + Build.VERSION.RELEASE + " / " + Build.MANUFACTURER + " " + Build.MODEL;
        }
    }
}
