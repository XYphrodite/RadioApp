namespace RadioApp.Maui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage())
        {
            Title = "RadioWork // FM — Прямой эфир",
            Width = 1480,
            Height = 880,
            MinimumWidth = 980,
            MinimumHeight = 640
        };
    }
}
