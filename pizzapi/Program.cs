using Avalonia;

namespace pizzapi;

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Check for headless mode argument
        if (args.Length > 0 && (args[0].ToLower() == "--headless" || args[0].ToLower() == "-headless"))
        {
            var headless = new HeadlessMode();
            headless.Run(args).Wait();
            return 0;
        }
        else
        {
            // Start Avalonia UI
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
