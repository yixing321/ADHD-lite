package com.adhd.lite;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;

import java.io.File;
import java.io.FileNotFoundException;

/**
 * v0.11：只给系统安装器读一个文件（cache/update.apk）。
 * Android 7+ 不允许把 file:// 交给别的应用，所以用一个最小的 ContentProvider 暴露 content://，
 * 安装时通过 FLAG_GRANT_READ_URI_PERMISSION 临时授权，不对外导出。
 */
public class ApkProvider extends ContentProvider {
    public static final String AUTHORITY = "com.adhd.lite.apk";
    public static final String FILE_NAME = "update.apk";

    public static Uri uriFor(Context ctx, File file) {
        return new Uri.Builder().scheme("content").authority(AUTHORITY)
                .appendPath(file.getName()).build();
    }

    @Override
    public boolean onCreate() { return true; }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        String name = uri == null ? "" : uri.getLastPathSegment();
        if (!FILE_NAME.equals(name)) throw new FileNotFoundException("不允许访问该文件");
        File f = new File(getContext().getCacheDir(), FILE_NAME);
        if (!f.exists()) throw new FileNotFoundException("更新包不存在");
        return ParcelFileDescriptor.open(f, ParcelFileDescriptor.MODE_READ_ONLY);
    }

    @Override
    public String getType(Uri uri) { return "application/vnd.android.package-archive"; }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection,
                        String[] selectionArgs, String sortOrder) { return null; }

    @Override
    public Uri insert(Uri uri, ContentValues values) { return null; }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }
}
