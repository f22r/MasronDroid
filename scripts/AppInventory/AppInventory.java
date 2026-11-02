import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.os.Looper;
import android.util.Base64;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;

public class AppInventory {
    public static void main(String[] args) throws Exception {
        Looper.prepareMainLooper();
        Class<?> thread = Class.forName("android.app.ActivityThread");
        Object activity = thread.getMethod("systemMain").invoke(null);
        Context context = (Context)thread.getMethod("getSystemContext").invoke(activity);
        PackageManager pm = context.getPackageManager();
        for (ApplicationInfo info : pm.getInstalledApplications(PackageManager.MATCH_UNINSTALLED_PACKAGES)) {
            JSONObject item = new JSONObject();
            item.put("Package", info.packageName);
            item.put("Name", info.packageName);
            item.put("System", (info.flags & ApplicationInfo.FLAG_SYSTEM) != 0);
            item.put("Installed", (info.flags & ApplicationInfo.FLAG_INSTALLED) != 0);
            item.put("Enabled", info.enabled);
            long bytes = 0;
            boolean readable = info.sourceDir != null;
            java.util.ArrayList<String> paths = new java.util.ArrayList<>();
            if (readable) paths.add(info.sourceDir);
            if (info.splitSourceDirs != null) java.util.Collections.addAll(paths, info.splitSourceDirs);
            for (String path : paths) {
                java.io.File file = new java.io.File(path);
                if (!file.isFile()) readable = false;
                else bytes += file.length();
            }
            if (readable) item.put("Bytes", bytes);
            try {
                item.put("Name", info.loadLabel(pm).toString());
                Drawable icon = info.loadIcon(pm);
                Bitmap bitmap = Bitmap.createBitmap(48, 48, Bitmap.Config.ARGB_8888);
                icon.setBounds(0, 0, 48, 48);
                icon.draw(new Canvas(bitmap));
                ByteArrayOutputStream png = new ByteArrayOutputStream();
                bitmap.compress(Bitmap.CompressFormat.PNG, 100, png);
                item.put("Icon", Base64.encodeToString(png.toByteArray(), Base64.NO_WRAP));
                bitmap.recycle();
            } catch (RuntimeException ignored) { }
            System.out.println(item);
        }
    }
}
