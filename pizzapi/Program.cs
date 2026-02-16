using System;
using Avalonia;
using Avalonia.ReactiveUI;

namespace pizzapi;

class Program
{
    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();

    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext
    // rely on the .NET runtime to launch the app
    static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }
}
