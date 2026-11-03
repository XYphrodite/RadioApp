using Microsoft.Extensions.Logging;
using RadioApp.Core.Services;

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

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        builder.Services.AddSingleton(_ => new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        });

        builder.Services.AddSingleton(_ =>
        {
            var statePath = Path.Combine(FileSystem.AppDataDirectory, "user_state.json");
            return new UserRadioStore(statePath);
        });

        builder.Services.AddSingleton<RadioBroadcastEngine>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
