using Avalonia;
using System;
using System.Linq;

namespace pizzapi;

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        RuntimeOptions.DisableListener = args.Any(a =>
            string.Equals(a, "--no-listener", StringComparison.OrdinalIgnoreCase));
        pizzalib.RuntimeFlags.DisableTranscription = args.Any(a =>
            string.Equals(a, "--no-transcribe", StringComparison.OrdinalIgnoreCase));
        pizzalib.RuntimeFlags.DisableAudioEncoding = args.Any(a =>
            string.Equals(a, "--no-audio-encode", StringComparison.OrdinalIgnoreCase));

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

        if (OperatingSystem.IsLinux())
        {
            bool useX11Tuned = args.Any(a =>
                string.Equals(a, "--x11-tuned", StringComparison.OrdinalIgnoreCase));
            bool forceSoftware = args.Any(a =>
                string.Equals(a, "--software-rendering", StringComparison.OrdinalIgnoreCase));

            if (useX11Tuned || forceSoftware)
            {
                builder = builder.With(new X11PlatformOptions
                {
                    // Optional Linux tuning switches for A/B testing.
                    ShouldRenderOnUIThread = true,
                    UseRetainedFramebuffer = true,
                    RenderingMode = forceSoftware
                        ? new[] { X11RenderingMode.Software }
                        : new[] { X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software }
                });
            }
        }

        return builder;
    }

    public static AppBuilder BuildAvaloniaApp() // DO NOT REMOVE - for designer
    {
        return BuildAvaloniaApp(Array.Empty<string>());
    }
}
