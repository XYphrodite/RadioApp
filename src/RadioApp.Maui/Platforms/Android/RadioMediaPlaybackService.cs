#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Runtime;

namespace RadioApp.Maui.Platforms.Android;

[Service(
    Name = "com.radiowork.fm.RadioMediaPlaybackService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class RadioMediaPlaybackService : Service
{
    public const int NotificationId = 5480;

    internal static RadioMediaPlaybackService? ActiveInstance { get; private set; }
    internal bool IsInForeground { get; private set; }

    private PowerManager.WakeLock? _wakeLock;
    private WifiManager.WifiLock? _wifiLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        ActiveInstance = this;
    }

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        ActiveInstance = this;

        var bridge = AndroidMediaSessionBridge.Current;
        var notification = bridge?.BuildCurrentNotification(this)
                           ?? AndroidMediaSessionBridge.BuildFallbackNotification(this);

        PromoteToForeground(notification, acquireLocks: bridge?.LatestIsPlaying ?? true);

        if (bridge is null)
        {
            StopCompletely();
            return StartCommandResult.NotSticky;
        }

        return StartCommandResult.Sticky;
    }

    internal void PromoteToForeground(Notification notification, bool acquireLocks)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }

            IsInForeground = true;

            if (acquireLocks)
                AcquireLocks();
            else
                ReleaseLocks();
        }
        catch
        {
            // Fallback to normal notification update if OS restricts foreground promotion
            if (GetSystemService(NotificationService) is NotificationManager nm)
            {
                nm.Notify(NotificationId, notification);
            }
        }
    }

    internal void DemoteToPaused(Notification pausedNotification)
    {
        ReleaseLocks();

        try
        {
            if (IsInForeground)
            {
                StopForeground(StopForegroundFlags.Detach);
                IsInForeground = false;
            }

            if (GetSystemService(NotificationService) is NotificationManager nm)
            {
                nm.Notify(NotificationId, pausedNotification);
            }
        }
        catch
        {
        }
    }

    internal void StopCompletely()
    {
        ReleaseLocks();

        try
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        catch
        {
        }

        IsInForeground = false;

        try
        {
            if (GetSystemService(NotificationService) is NotificationManager nm)
            {
                nm.Cancel(NotificationId);
            }
        }
        catch
        {
        }

        try
        {
            StopSelf();
        }
        catch
        {
        }
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        try
        {
            AndroidMediaSessionBridge.Current?.HandleTaskRemoved();
        }
        catch
        {
        }

        StopCompletely();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        ReleaseLocks();
        IsInForeground = false;
        if (ReferenceEquals(ActiveInstance, this))
        {
            ActiveInstance = null;
        }

        base.OnDestroy();
    }

    private void AcquireLocks()
    {
        try
        {
            if (_wakeLock is null && GetSystemService(PowerService) is PowerManager pm)
            {
                _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "RadioWorkFM::MediaPlaybackWakeLock");
                _wakeLock?.SetReferenceCounted(false);
            }

            if (_wakeLock is { IsHeld: false })
            {
                _wakeLock.Acquire();
            }
        }
        catch
        {
        }

        try
        {
            if (_wifiLock is null && ApplicationContext?.GetSystemService(WifiService) is WifiManager wm)
            {
                WifiMode mode;
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    mode = WifiMode.FullLowLatency;
                }
                else
                {
#pragma warning disable CA1422
                    mode = WifiMode.FullHighPerf;
#pragma warning restore CA1422
                }

                _wifiLock = wm.CreateWifiLock(mode, "RadioWorkFM::MediaPlaybackWifiLock");
                _wifiLock?.SetReferenceCounted(false);
            }

            if (_wifiLock is { IsHeld: false })
            {
                _wifiLock.Acquire();
            }
        }
        catch
        {
        }
    }

    private void ReleaseLocks()
    {
        try
        {
            if (_wakeLock is { IsHeld: true })
                _wakeLock.Release();
        }
        catch
        {
        }

        try
        {
            if (_wifiLock is { IsHeld: true })
                _wifiLock.Release();
        }
        catch
        {
        }
    }
}
#endif
