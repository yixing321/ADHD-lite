package com.adhd.lite;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.HashSet;
import java.util.Set;

public final class AlarmScheduler {
    public static final String PREFS = "adhd_lite";
    public static final String TASKS_JSON = "tasks_json";
    public static final String SCHEDULED_IDS = "scheduled_ids";
    public static final String ACTION_REMIND = "com.adhd.lite.REMIND";

    private AlarmScheduler() { }

    public static void sync(Context ctx, String json) {
        if (json == null) return;
        SharedPreferences sp = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        sp.edit().putString(TASKS_JSON, json).apply();
        cancelAll(ctx, sp);

        Set<String> ids = new HashSet<String>();
        try {
            JSONObject root = new JSONObject(json);
            JSONArray arr = root.optJSONArray("tasks");
            if (arr == null) arr = new JSONArray(json);
            long now = System.currentTimeMillis();
            for (int i = 0; i < arr.length(); i++) {
                JSONObject t = arr.optJSONObject(i);
                if (t == null || t.optBoolean("done")) continue;
                long due = t.optLong("due", 0);
                if (due <= now + 750) continue;
                String id = t.optString("id", "");
                if (id.length() == 0) continue;
                schedule(ctx, id, t.optString("title", "任务"), t.optString("priority", "mid"), due, false);
                ids.add(id);
            }

            JSONArray plans = root.optJSONArray("plan");
            if (plans != null) {
                for (int i = 0; i < plans.length(); i++) {
                    JSONObject p = plans.optJSONObject(i);
                    if (p == null) continue;
                    long at = p.optLong("at", 0);
                    if (at <= now + 750) continue;
                    String msg = p.optString("message", "该看看清单了");
                    String pid = "plan-" + at + "-" + i;
                    schedule(ctx, pid, msg, p.optInt("level", 1) == 0 ? "high" : "mid", at, true);
                    ids.add(pid);
                }
            }
            sp.edit().putStringSet(SCHEDULED_IDS, ids).apply();
        } catch (Exception ignored) { }
    }

    public static int scheduledCount(Context ctx) {
        SharedPreferences sp = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        Set<String> s = sp.getStringSet(SCHEDULED_IDS, new HashSet<String>());
        return s == null ? 0 : s.size();
    }

    // 手机设置里的“测试通知（5 秒）”：用 Handler 直接发，不依赖精确闹钟权限，
    // 这样能单独验证「通道 + 横幅 + 声音」，不会被 AlarmManager 的降级延迟拖住。
    public static void test(Context ctx) {
        final Context app = ctx.getApplicationContext();
        new Handler(Looper.getMainLooper()).postDelayed(new Runnable() {
            public void run() {
                Notifications.showTask(app, "test-" + System.currentTimeMillis(),
                        "测试提醒：手机本地通知正常", "high", System.currentTimeMillis(), false);
            }
        }, 5000);
    }

    public static void rescheduleFromCache(Context ctx) {
        SharedPreferences sp = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String json = sp.getString(TASKS_JSON, null);
        if (json != null) sync(ctx, json);
    }

    private static void schedule(Context ctx, String id, String title, String priority, long due, boolean plan) {
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        if (am == null) return;
        Intent i = new Intent(ctx, ReminderReceiver.class);
        i.setAction(ACTION_REMIND);
        i.putExtra("id", id);
        i.putExtra("title", title);
        i.putExtra("priority", priority);
        i.putExtra("due", due);
        i.putExtra("plan", plan);
        PendingIntent pi = PendingIntent.getBroadcast(ctx, Notifications.requestCode(id), i,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        try {
            if (Build.VERSION.SDK_INT >= 31 && !am.canScheduleExactAlarms()) {
                am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, due, pi);
            } else if (Build.VERSION.SDK_INT >= 23) {
                am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, due, pi);
            } else {
                am.setExact(AlarmManager.RTC_WAKEUP, due, pi);
            }
        } catch (SecurityException e) {
            try { am.set(AlarmManager.RTC_WAKEUP, due, pi); } catch (Exception ignored) { }
        } catch (Exception ignored) { }
    }

    private static void cancelAll(Context ctx, SharedPreferences sp) {
        Set<String> old = sp.getStringSet(SCHEDULED_IDS, new HashSet<String>());
        if (old == null) return;
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        if (am == null) return;
        for (String id : old) {
            Intent i = new Intent(ctx, ReminderReceiver.class);
            i.setAction(ACTION_REMIND);
            PendingIntent pi = PendingIntent.getBroadcast(ctx, Notifications.requestCode(id), i,
                    PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            try { am.cancel(pi); } catch (Exception ignored) { }
        }
    }
}
