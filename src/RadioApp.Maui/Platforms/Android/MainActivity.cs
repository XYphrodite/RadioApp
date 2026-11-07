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
            VolumeControlStream = global::Android.Media.Stream.Music;

            if (Window is not null)
            {
                WindowCompat.SetDecorFitsSystemWindows(Window, false);

                var darkBg = global::Android.Graphics.Color.ParseColor("#07080C");
                Window.DecorView.SetBackgroundColor(darkBg);

#pragma warning disable CA1422
                Window.SetStatusBarColor(darkBg);
                Window.SetNavigationBarColor(darkBg);
#pragma warning restore CA1422

                var insetsController = WindowCompat.GetInsetsController(Window, Window.DecorView);
                if (insetsController is not null)
                {
                    insetsController.AppearanceLightStatusBars = false;
                    insetsController.AppearanceLightNavigationBars = false;
                }

                var content = FindViewById(global::Android.Resource.Id.Content);
                content?.SetBackgroundColor(darkBg);

                ViewCompat.SetOnApplyWindowInsetsListener(Window.DecorView, new SystemBarInsetsListener());
                ViewCompat.RequestApplyInsets(Window.DecorView);
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

    protected override void OnPostResume()
    {
        base.OnPostResume();
        try
        {
            if (Window?.DecorView is { } decor)
            {
                ViewCompat.RequestApplyInsets(decor);
            }
        }
        catch { }
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

#nullable disable
    private sealed class SystemBarInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(global::Android.Views.View v, WindowInsetsCompat insets)
        {
            if (v is null || insets is null)
                return insets;

            int mask = WindowInsetsCompat.Type.SystemBars()
                     | WindowInsetsCompat.Type.DisplayCutout()
                     | WindowInsetsCompat.Type.Ime();
            var bars = insets.GetInsets(mask);

            var content = v.FindViewById(global::Android.Resource.Id.Content);
            content?.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);

            return WindowInsetsCompat.Consumed;
        }
    }
#nullable restore
}
