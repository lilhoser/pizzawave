using Avalonia;

namespace pizzapi;

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Start Avalonia UI
        var builder = BuildAvaloniaApp(args);
        return builder.StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp(string[] args)
    {
        var builder = AppBuilder.Configure<App>()
             .UsePlatformDetect()
             .WithInterFont()
             .LogToTrace();
        return builder;
    }
    public static AppBuilder BuildAvaloniaApp() // DO NOT REMOVE - for designer
    {
        return AppBuilder.Configure<App>()
             .UsePlatformDetect()
             .WithInterFont()
             .LogToTrace();
    }
}