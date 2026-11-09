#if ANDROID
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using RadioApp.Maui.Services;

namespace RadioApp.Maui.Platforms.Android;

public sealed class AndroidMediaSessionBridge : IMediaSessionBridge, IDisposable
{
    public const string ChannelId = "radiowork_media_playback";

    private const string ActionPrev = "com.radiowork.fm.media.PREV";
    private const string ActionToggle = "com.radiowork.fm.media.TOGGLE";
    private const string ActionPlay = "com.radiowork.fm.media.PLAY";
    private const string ActionPause = "com.radiowork.fm.media.PAUSE";
    private const string ActionNext = "com.radiowork.fm.media.NEXT";
    private const string ActionDismiss = "com.radiowork.fm.media.DISMISS";

    internal static AndroidMediaSessionBridge? Current { get; private set; }

    private readonly HttpClient _httpClient;
    private readonly object _sync = new();

    private MediaSession? _mediaSession;
    private SessionCallback? _sessionCallback;
    private MediaActionReceiver? _actionReceiver;
    private Bitmap? _fallbackArtwork;
    private Bitmap? _cachedCoverBitmap;
    private string _cachedCoverUrl = "";
    private CancellationTokenSource? _coverFetchCts;
    private MediaPlaybackSnapshot? _latestSnapshot;
    private bool _channelCreated;
    private bool _receiverRegistered;
    private bool _disposed;

    public bool IsSupported => true;
    internal bool LatestIsPlaying => _latestSnapshot?.IsPlaying ?? false;

    public event Action<bool>? PlayPauseRequested;
    public event Action? TogglePlayPauseRequested;
    public event Action<int>? StepStationRequested;

    public AndroidMediaSessionBridge(HttpClient httpClient)
    {
        _httpClient = httpClient;
        Current = this;
    }

    public void UpdatePlayback(MediaPlaybackSnapshot snapshot)
    {
        if (_disposed)
            return;

        MainThread.BeginInvokeOnMainThread(() => ApplyPlaybackOnMainThread(snapshot));
    }

    public void Stop()
    {
        if (_disposed)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            CancelCoverFetch();
            _latestSnapshot = null;

            if (_mediaSession is not null)
            {
                try
                {
                    var stoppedBuilder = new PlaybackState.Builder();
                    stoppedBuilder.SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPlayPause);
                    stoppedBuilder.SetState(PlaybackStateCode.Stopped, 0L, 0f, SystemClock.ElapsedRealtime());
                    var stoppedState = stoppedBuilder.Build();
                    if (stoppedState is not null)
                    {
                        _mediaSession.SetPlaybackState(stoppedState);
                    }
                    _mediaSession.Active = false;
                }
                catch
                {
                }
            }

            RadioMediaPlaybackService.ActiveInstance?.StopCompletely();
        });
    }

    internal void HandleTaskRemoved()
    {
        PlayPauseRequested?.Invoke(false);
        Stop();
    }

    private void ApplyPlaybackOnMainThread(MediaPlaybackSnapshot snapshot)
    {
        if (_disposed)
            return;

        var appCtx = global::Android.App.Application.Context;
        EnsureInitialized(appCtx);

        _latestSnapshot = snapshot;

        Bitmap? art = null;
        if (!string.IsNullOrWhiteSpace(snapshot.CoverUrl) &&
            string.Equals(snapshot.CoverUrl, _cachedCoverUrl, StringComparison.Ordinal))
        {
            art = _cachedCoverBitmap;
        }

        art ??= GetOrCreateFallbackArtwork(appCtx);

        UpdateMediaSessionStateAndMetadata(snapshot, art);
        var notification = BuildNotification(appCtx, snapshot, art);

        var service = RadioMediaPlaybackService.ActiveInstance;
        if (snapshot.IsPlaying)
        {
            if (service is { IsInForeground: true })
            {
                service.PromoteToForeground(notification, acquireLocks: true);
            }
            else if (service is not null)
            {
                service.PromoteToForeground(notification, acquireLocks: true);
            }
            else
            {
                StartForegroundServiceSafely(appCtx, notification);
            }
        }
        else
        {
            if (service is not null)
            {
                service.DemoteToPaused(notification);
            }
            else if (appCtx.GetSystemService(Context.NotificationService) is NotificationManager nm)
            {
                nm.Notify(RadioMediaPlaybackService.NotificationId, notification);
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CoverUrl) &&
            !string.Equals(snapshot.CoverUrl, _cachedCoverUrl, StringComparison.Ordinal))
        {
            StartCoverFetch(snapshot.TrackId, snapshot.CoverUrl);
        }
        else if (string.IsNullOrWhiteSpace(snapshot.CoverUrl))
        {
            CancelCoverFetch();
        }
    }

    private void StartForegroundServiceSafely(Context appCtx, Notification fallbackNotification)
    {
        try
        {
            var intent = new Intent(appCtx, typeof(RadioMediaPlaybackService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                appCtx.StartForegroundService(intent);
            }
            else
            {
                appCtx.StartService(intent);
            }
        }
        catch
        {
            if (appCtx.GetSystemService(Context.NotificationService) is NotificationManager nm)
            {
                nm.Notify(RadioMediaPlaybackService.NotificationId, fallbackNotification);
            }
        }
    }

    private void StartCoverFetch(int trackId, string coverUrl)
    {
        CancelCoverFetch();
        var cts = new CancellationTokenSource();
        _coverFetchCts = cts;
        _ = FetchCoverAsync(trackId, coverUrl, cts.Token);
    }

    private void CancelCoverFetch()
    {
        try
        {
            _coverFetchCts?.Cancel();
            _coverFetchCts?.Dispose();
        }
        catch
        {
        }
        _coverFetchCts = null;
    }

    private async Task FetchCoverAsync(int trackId, string coverUrl, CancellationToken ct)
    {
        try
        {
            // Yield 180ms so Opus audio stream wins initial bandwidth on station/track switch
            await Task.Delay(180, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

            var bytes = await _httpClient.GetByteArrayAsync(coverUrl, timeoutCts.Token);
            if (bytes is null || bytes.Length == 0 || ct.IsCancellationRequested)
                return;

            var decoded = await Task.Run(() => DecodeScaledArtwork(bytes), ct);
            if (decoded is null || ct.IsCancellationRequested)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_disposed || ct.IsCancellationRequested)
                    return;

                _cachedCoverUrl = coverUrl;
                _cachedCoverBitmap = decoded;

                if (_latestSnapshot is { } current &&
                    current.TrackId == trackId &&
                    string.Equals(current.CoverUrl, coverUrl, StringComparison.Ordinal))
                {
                    var appCtx = global::Android.App.Application.Context;
                    UpdateMediaSessionStateAndMetadata(current, decoded);
                    var updatedNotification = BuildNotification(appCtx, current, decoded);
                    if (appCtx.GetSystemService(Context.NotificationService) is NotificationManager nm)
                    {
                        nm.Notify(RadioMediaPlaybackService.NotificationId, updatedNotification);
                    }
                }
            });
        }
        catch
        {
            // Fallback RadioWork FM icon remains displayed
        }
    }

    private static Bitmap? DecodeScaledArtwork(byte[] bytes)
    {
        var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
        if (bmp is null)
            return null;

        const int maxDim = 360;
        if (bmp.Width <= maxDim && bmp.Height <= maxDim)
            return bmp;

        float scale = Math.Min((float)maxDim / bmp.Width, (float)maxDim / bmp.Height);
        int w = Math.Max(1, (int)Math.Round(bmp.Width * scale));
        int h = Math.Max(1, (int)Math.Round(bmp.Height * scale));
        return Bitmap.CreateScaledBitmap(bmp, w, h, true);
    }

    internal Notification BuildCurrentNotification(Context context)
    {
        EnsureInitialized(context);

        var snap = _latestSnapshot ?? new MediaPlaybackSnapshot(
            TrackId: 0,
            TrackTitle: "Прямой эфир",
            TrackArtist: "RadioWork FM",
            StationLabel: "RadioWork // FM",
            CoverUrl: "",
            IsPlaying: true,
            ElapsedSec: 0,
            DurationSec: 0);

        Bitmap? art = null;
        if (!string.IsNullOrWhiteSpace(snap.CoverUrl) &&
            string.Equals(snap.CoverUrl, _cachedCoverUrl, StringComparison.Ordinal))
        {
            art = _cachedCoverBitmap;
        }

        art ??= GetOrCreateFallbackArtwork(context);
        return BuildNotification(context, snap, art);
    }

    internal static Notification BuildFallbackNotification(Context context)
    {
        EnsureChannelStatic(context);

        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            builder = new Notification.Builder(context, ChannelId);
        }
        else
        {
#pragma warning disable CA1422
            builder = new Notification.Builder(context);
#pragma warning restore CA1422
        }

        return builder
            .SetSmallIcon(Resource.Drawable.ic_stat_radio)
            .SetContentTitle("RadioWork // FM")
            .SetContentText("Прямой эфир")
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetVisibility(NotificationVisibility.Public)
            .Build();
    }

    private Notification BuildNotification(Context context, MediaPlaybackSnapshot snapshot, Bitmap? artwork)
    {
        EnsureChannelStatic(context);

        var title = string.IsNullOrWhiteSpace(snapshot.TrackTitle) ? "Прямой эфир" : snapshot.TrackTitle;
        var subtitle = FormatSubtitle(snapshot.TrackArtist, snapshot.StationLabel);

        var launchIntent = new Intent(context, typeof(MainActivity));
        launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var contentPendingIntent = PendingIntent.GetActivity(
            context,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var prevIntent = BuildActionPendingIntent(context, ActionPrev, 101);
        var toggleIntent = BuildActionPendingIntent(context, ActionToggle, 102);
        var nextIntent = BuildActionPendingIntent(context, ActionNext, 103);
        var dismissIntent = BuildActionPendingIntent(context, ActionDismiss, 104);

        var prevAction = new Notification.Action.Builder(
            Icon.CreateWithResource(context, Resource.Drawable.ic_notif_prev),
            "Предыдущая волна",
            prevIntent).Build();

        var toggleAction = new Notification.Action.Builder(
            Icon.CreateWithResource(
                context,
                snapshot.IsPlaying ? Resource.Drawable.ic_notif_pause : Resource.Drawable.ic_notif_play),
            snapshot.IsPlaying ? "Пауза" : "Включить эфир",
            toggleIntent).Build();

        var nextAction = new Notification.Action.Builder(
            Icon.CreateWithResource(context, Resource.Drawable.ic_notif_next),
            "Следующая волна",
            nextIntent).Build();

        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            builder = new Notification.Builder(context, ChannelId);
        }
        else
        {
#pragma warning disable CA1422
            builder = new Notification.Builder(context);
#pragma warning restore CA1422
        }

        builder
            .SetSmallIcon(Resource.Drawable.ic_stat_radio)
            .SetContentTitle(title)
            .SetContentText(subtitle)
            .SetSubText(string.IsNullOrWhiteSpace(snapshot.StationLabel) ? "RadioWork // FM" : snapshot.StationLabel)
            .SetContentIntent(contentPendingIntent)
            .SetDeleteIntent(dismissIntent)
            .SetVisibility(NotificationVisibility.Public)
            .SetCategory(Notification.CategoryTransport)
            .SetOngoing(snapshot.IsPlaying)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false)
            .AddAction(prevAction)
            .AddAction(toggleAction)
            .AddAction(nextAction);

        if (artwork is not null)
        {
            builder.SetLargeIcon(artwork);
        }

        if (_mediaSession is not null)
        {
            var mediaStyle = new Notification.MediaStyle();
            mediaStyle.SetMediaSession(_mediaSession.SessionToken);
            mediaStyle.SetShowActionsInCompactView(0, 1, 2);
            builder.SetStyle(mediaStyle);
        }

        return builder.Build();
    }

    private void UpdateMediaSessionStateAndMetadata(MediaPlaybackSnapshot snapshot, Bitmap? artwork)
    {
        if (_mediaSession is null)
            return;

        var title = string.IsNullOrWhiteSpace(snapshot.TrackTitle) ? "Прямой эфир" : snapshot.TrackTitle;
        var subtitle = FormatSubtitle(snapshot.TrackArtist, snapshot.StationLabel);
        var station = string.IsNullOrWhiteSpace(snapshot.StationLabel) ? "RadioWork // FM" : snapshot.StationLabel;
        long durationMs = snapshot.DurationSec > 0
            ? (long)Math.Round(snapshot.DurationSec * 1000.0)
            : -1L;
        long positionMs = Math.Max(0L, (long)Math.Round(snapshot.ElapsedSec * 1000.0));

        var metaBuilder = new MediaMetadata.Builder();
        metaBuilder.PutString(MediaMetadata.MetadataKeyTitle, title);
        metaBuilder.PutString(MediaMetadata.MetadataKeyArtist, subtitle);
        metaBuilder.PutString(MediaMetadata.MetadataKeyAlbum, station);
        metaBuilder.PutString(MediaMetadata.MetadataKeyDisplayTitle, title);
        metaBuilder.PutString(MediaMetadata.MetadataKeyDisplaySubtitle, subtitle);
        metaBuilder.PutLong(MediaMetadata.MetadataKeyDuration, durationMs);

        if (artwork is not null)
        {
            metaBuilder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, artwork);
        }

        var stateBuilder = new PlaybackState.Builder();
        stateBuilder.SetActions(
            PlaybackState.ActionPlay |
            PlaybackState.ActionPause |
            PlaybackState.ActionPlayPause |
            PlaybackState.ActionSkipToPrevious |
            PlaybackState.ActionSkipToNext |
            PlaybackState.ActionStop);
        stateBuilder.SetState(
            snapshot.IsPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
            positionMs,
            snapshot.IsPlaying ? 1.0f : 0.0f,
            SystemClock.ElapsedRealtime());

        _mediaSession.Active = true;
        var builtMeta = metaBuilder.Build();
        if (builtMeta is not null)
        {
            _mediaSession.SetMetadata(builtMeta);
        }

        var builtState = stateBuilder.Build();
        if (builtState is not null)
        {
            _mediaSession.SetPlaybackState(builtState);
        }
    }

    private static string FormatSubtitle(string? artist, string? stationLabel)
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(artist);
        bool hasStation = !string.IsNullOrWhiteSpace(stationLabel);
        if (hasArtist && hasStation)
            return $"{artist!.Trim()} · {stationLabel!.Trim()}";
        if (hasArtist)
            return artist!.Trim();
        if (hasStation)
            return stationLabel!.Trim();
        return "RadioWork // FM";
    }

    private static PendingIntent? BuildActionPendingIntent(Context context, string action, int requestCode)
    {
        var intent = new Intent(action);
        intent.SetPackage(context.PackageName);
        return PendingIntent.GetBroadcast(
            context,
            requestCode,
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private void EnsureInitialized(Context context)
    {
        lock (_sync)
        {
            if (!_channelCreated)
            {
                EnsureChannelStatic(context);
                _channelCreated = true;
            }

            if (_mediaSession is null)
            {
                _mediaSession = new MediaSession(context, "RadioWorkFM");
                _sessionCallback = new SessionCallback(this);
                _mediaSession.SetCallback(_sessionCallback, new Handler(Looper.MainLooper!));

                var launchIntent = new Intent(context, typeof(MainActivity));
                launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
                var sessionActivity = PendingIntent.GetActivity(
                    context,
                    0,
                    launchIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                if (sessionActivity is not null)
                {
                    _mediaSession.SetSessionActivity(sessionActivity);
                }

                _mediaSession.Active = true;
            }

            if (!_receiverRegistered)
            {
                _actionReceiver = new MediaActionReceiver(this);
                var filter = new IntentFilter();
                filter.AddAction(ActionPrev);
                filter.AddAction(ActionToggle);
                filter.AddAction(ActionPlay);
                filter.AddAction(ActionPause);
                filter.AddAction(ActionNext);
                filter.AddAction(ActionDismiss);

                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                {
                    context.RegisterReceiver(_actionReceiver, filter, ReceiverFlags.NotExported);
                }
                else
                {
                    context.RegisterReceiver(_actionReceiver, filter);
                }

                _receiverRegistered = true;
            }
        }
    }

    private static void EnsureChannelStatic(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        if (context.GetSystemService(Context.NotificationService) is not NotificationManager nm)
            return;

        if (nm.GetNotificationChannel(ChannelId) is not null)
            return;

        var channel = new NotificationChannel(
            ChannelId,
            "RadioWork FM — Прямой эфир",
            NotificationImportance.Low)
        {
            Description = "Плеер и переключение волн RadioWork FM на экране блокировки и в шторке уведомлений",
            LockscreenVisibility = NotificationVisibility.Public
        };
        channel.SetShowBadge(false);
        channel.EnableVibration(false);
        channel.SetSound(null, null);
        nm.CreateNotificationChannel(channel);
    }

    private Bitmap? GetOrCreateFallbackArtwork(Context context)
    {
        if (_fallbackArtwork is not null)
            return _fallbackArtwork;

        try
        {
            const int size = 256;
            var bmp = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!);
            using var canvas = new Canvas(bmp);
            canvas.DrawColor(global::Android.Graphics.Color.ParseColor("#07080C"));

            var appIcon = context.ApplicationInfo?.LoadIcon(context.PackageManager);
            if (appIcon is not null)
            {
                appIcon.SetBounds(0, 0, size, size);
                appIcon.Draw(canvas);
            }

            _fallbackArtwork = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void OnActionBroadcast(string? action)
    {
        switch (action)
        {
            case ActionPrev:
                StepStationRequested?.Invoke(-1);
                break;
            case ActionNext:
                StepStationRequested?.Invoke(1);
                break;
            case ActionToggle:
                TogglePlayPauseRequested?.Invoke();
                break;
            case ActionPlay:
                PlayPauseRequested?.Invoke(true);
                break;
            case ActionPause:
                PlayPauseRequested?.Invoke(false);
                break;
            case ActionDismiss:
                if (_mediaSession is not null)
                {
                    try { _mediaSession.Active = false; } catch { }
                }
                RadioMediaPlaybackService.ActiveInstance?.StopCompletely();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        CancelCoverFetch();

        var appCtx = global::Android.App.Application.Context;
        if (_receiverRegistered && _actionReceiver is not null)
        {
            try { appCtx.UnregisterReceiver(_actionReceiver); } catch { }
            _receiverRegistered = false;
        }

        if (_mediaSession is not null)
        {
            try
            {
                _mediaSession.Active = false;
                _mediaSession.Release();
            }
            catch
            {
            }
            _mediaSession = null;
            _sessionCallback = null;
        }

        RadioMediaPlaybackService.ActiveInstance?.StopCompletely();

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }

    private sealed class SessionCallback(AndroidMediaSessionBridge owner) : MediaSession.Callback
    {
        public override void OnPlay() => owner.PlayPauseRequested?.Invoke(true);
        public override void OnPause() => owner.PlayPauseRequested?.Invoke(false);
        public override void OnStop() => owner.PlayPauseRequested?.Invoke(false);
        public override void OnSkipToPrevious() => owner.StepStationRequested?.Invoke(-1);
        public override void OnSkipToNext() => owner.StepStationRequested?.Invoke(1);
    }

    private sealed class MediaActionReceiver(AndroidMediaSessionBridge owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            owner.OnActionBroadcast(intent?.Action);
        }
    }
}
#endif
