package com.adhd.lite;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public class ReminderReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null) return;
        String id = intent.getStringExtra("id");
        String title = intent.getStringExtra("title");
        String priority = intent.getStringExtra("priority");
        long due = intent.getLongExtra("due", System.currentTimeMillis());
        boolean plan = intent.getBooleanExtra("plan", false);
        Notifications.showTask(context, id, title, priority, due, plan);
    }
}
