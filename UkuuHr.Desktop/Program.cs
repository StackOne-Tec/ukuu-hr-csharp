using Avalonia;
using Avalonia.Controls;
using System;

namespace UkuuHr.Sync;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
