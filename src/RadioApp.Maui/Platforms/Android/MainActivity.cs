using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace RadioApp.Maui;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private PowerManager.WakeLock? _wakeLock;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            if (Window is not null)
            {
#pragma warning disable CA1422
                var darkBg = global::Android.Graphics.Color.ParseColor("#07080C");
                Window.SetStatusBarColor(darkBg);
                Window.SetNavigationBarColor(darkBg);
#pragma warning restore CA1422
                var insetsController = WindowCompat.GetInsetsController(Window, Window.DecorView);
                if (insetsController is not null)
                {
                    insetsController.AppearanceLightStatusBars = false;
                    insetsController.AppearanceLightNavigationBars = false;
                }
            }

            if (GetSystemService(Context.PowerService) is PowerManager pm)
            {
                _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "RadioWorkFM::BroadcastWakeLock");
                _wakeLock?.SetReferenceCounted(false);
                _wakeLock?.Acquire();
            }
        }
        catch
        {
            // Non-critical chrome / wake lock setup
        }
    }

    protected override void OnDestroy()
    {
        try
        {
            if (_wakeLock?.IsHeld == true)
                _wakeLock.Release();
        }
        catch { }
        base.OnDestroy();
    }
}
