package com.adhd.lite;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.media.AudioAttributes;
import android.media.RingtoneManager;
import android.net.Uri;
import android.os.Build;

public final class Notifications {
    // v0.11.1：换成「闹铃」通道（旧 adhd_due 通道的声音改不了，必须用新 id）。
    public static final String CHANNEL = "adhd_alarm_v1";
    public static final String LEGACY_CHANNEL = "adhd_due";

    private Notifications() { }

    public static Uri alarmSound() {
        try {
            Uri u = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM);
            if (u != null) return u;
        } catch (Exception ignored) { }
        return android.provider.Settings.System.DEFAULT_NOTIFICATION_URI;
    }

    public static AudioAttributes alarmAudio() {
        return new AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_ALARM)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build();
    }

    public static void ensureChannel(Context ctx) {
        if (Build.VERSION.SDK_INT < 26) return;
        NotificationManager nm = (NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) return;
        NotificationChannel c = new NotificationChannel(CHANNEL, "ADHD 到点提醒（响铃）", NotificationManager.IMPORTANCE_HIGH);
        c.setDescription("任务到点：响铃 + 震动 + 锁屏可见；可在系统设置里调横幅/声音");
        c.enableVibration(true);
        c.setVibrationPattern(new long[] { 0, 420, 180, 420, 180, 700 });
        c.setSound(alarmSound(), alarmAudio());
        c.setShowBadge(true);
        c.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
        try { nm.createNotificationChannel(c); } catch (Exception ignored) { }
        // 旧通道只改了名字/描述，声音改不了；删掉避免设置页出现两个 ADHD 通道。
        try { nm.deleteNotificationChannel(LEGACY_CHANNEL); } catch (Exception ignored) { }
    }

    public static void showTask(Context ctx, String id, String title, String priority, long due, boolean plan) {
        ensureChannel(ctx);
        NotificationManager nm = (NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) return;
        Intent open = new Intent(ctx, MainActivity.class);
        open.putExtra("taskId", id);
        open.addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent pi = PendingIntent.getActivity(ctx, requestCode(id), open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        int icon = R.drawable.ic_notify;
        Notification.Builder b;
        if (Build.VERSION.SDK_INT >= 26) b = new Notification.Builder(ctx, CHANNEL);
        else b = new Notification.Builder(ctx);

        String when = "";
        try {
            java.text.SimpleDateFormat f = new java.text.SimpleDateFormat("HH:mm", java.util.Locale.CHINA);
            when = f.format(new java.util.Date(due));
        } catch (Exception ignored) { }

        String text = plan ? "AI 提醒 · 点开处理"
             : (when.length() > 0 ? ("截止 " + when + " · 点开处理") : "点开处理");
        b.setSmallIcon(icon)
         .setContentTitle(title == null ? "该做这件事了" : title)
         .setContentText(text)
         .setStyle(new Notification.BigTextStyle().bigText(text))
         .setContentIntent(pi)
         .setAutoCancel(true)
         .setWhen(due)
         .setShowWhen(true)
         .setVisibility(Notification.VISIBILITY_PUBLIC)
         .setCategory(Notification.CATEGORY_REMINDER)
         .setColor(0xffb05f2e)
         .setOnlyAlertOnce(false);
        b.setPriority(Notification.PRIORITY_HIGH);
        if (Build.VERSION.SDK_INT < 26) {
            // Android 8 以下没有通道，声音/震动直接挂在通知上。
            b.setSound(alarmSound(), alarmAudio());
            b.setVibrate(new long[] { 0, 420, 180, 420, 180, 700 });
            b.setDefaults(Notification.DEFAULT_LIGHTS);
        }
        try { nm.notify(requestCode(id), b.build()); } catch (Exception ignored) { }
    }

    public static int requestCode(String id) {
        if (id == null) return 1;
        int h = 17;
        for (int i = 0; i < id.length(); i++) h = 31 * h + id.charAt(i);
        h = h & 0x7fffffff;
        return h == 0 ? 1 : h;
    }
}
