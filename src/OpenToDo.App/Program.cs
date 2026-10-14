using Avalonia;

namespace OpenToDo.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Last-resort guard: a stray background failure should be logged, not silently kill the window.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Trace.TraceError(e.Exception.ToString());
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
