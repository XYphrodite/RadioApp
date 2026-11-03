using Microsoft.AspNetCore.Components.WebView;

namespace RadioApp.Maui;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
#if WINDOWS
        e.WebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 7, 8, 12);
        if (e.WebView.CoreWebView2?.Settings is { } settings)
        {
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
        }
#elif ANDROID
        e.WebView.SetBackgroundColor(Android.Graphics.Color.Rgb(7, 8, 12));
        e.WebView.Settings.MediaPlaybackRequiresUserGesture = false;
#endif
    }
}
