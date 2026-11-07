using Microsoft.Extensions.Logging;
using RadioApp.Core.Services;
using RadioApp.Maui.Services;

namespace RadioApp.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // Allow live FM broadcast audio to start immediately on receiver launch without gesture prompts
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "--autoplay-policy=no-user-gesture-required");
#endif

#if ANDROID
        Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler.BlazorWebViewMapper.AppendToMapping(
            "RadioAndroidWebView",
            (handler, _) =>
            {
                var settings = handler.PlatformView.Settings;
                settings.MediaPlaybackRequiresUserGesture = false;
                settings.MixedContentMode = global::Android.Webkit.MixedContentHandling.AlwaysAllow;
            });
#endif

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        builder.Services.AddSingleton(_ =>
        {
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(4),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
        });

        builder.Services.AddSingleton(_ =>
        {
            var statePath = Path.Combine(FileSystem.AppDataDirectory, "user_state.json");
            return new UserRadioStore(statePath);
        });

#if ANDROID
        builder.Services.AddSingleton<ITailscaleBridge, RadioApp.Maui.Platforms.Android.AndroidTsnetBridge>();
#else
        builder.Services.AddSingleton<ITailscaleBridge, NoOpTailscaleBridge>();
#endif

        builder.Services.AddSingleton<RadioBroadcastEngine>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
