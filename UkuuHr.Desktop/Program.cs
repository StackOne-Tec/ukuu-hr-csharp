using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UkuuHr.Desktop;

/// <summary>
/// Ukuu HR Desktop Launcher — opens the web app in the system's default browser.
/// 
/// This is a lightweight launcher that:
/// 1. Opens https://ukuu-hr-csharp.onrender.com in the default browser
/// 2. Stays running as a background process (so the user sees it in their taskbar)
/// 3. Prints instructions to the console
/// 
/// Users can override the URL with --url= argument.
/// 
/// For a true native window experience, users can install the PWA version:
/// open the URL in Chrome/Edge → click the install icon in the address bar.
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        string url = "https://ukuu-hr-csharp.onrender.com/dashboard";

        foreach (var arg in args)
        {
            if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
                url = arg["--url=".Length..];
        }

        Console.WriteLine("╔══════════════════════════════════════════╗");
        Console.WriteLine("║         UKUU HR — Desktop Launcher        ║");
        Console.WriteLine("╠══════════════════════════════════════════╣");
        Console.WriteLine("║                                          ║");
        Console.WriteLine($"║  Opening: {url}");
        Console.WriteLine("║                                          ║");
        Console.WriteLine("║  The app will open in your browser.      ║");
        Console.WriteLine("║  Keep this window open while using the   ║");
        Console.WriteLine("║  app. Close it when done.                ║");
        Console.WriteLine("║                                          ║");
        Console.WriteLine("║  Tip: Install as a PWA for a native      ║");
        Console.WriteLine("║  app experience (Chrome/Edge → install   ║");
        Console.WriteLine("║  icon in address bar).                   ║");
        Console.WriteLine("║                                          ║");
        Console.WriteLine("╚══════════════════════════════════════════╝");
        Console.WriteLine();

        // Open the URL in the default browser
        OpenUrl(url);

        Console.WriteLine("Browser opened. Press Ctrl+C to exit.");
        Console.WriteLine();

        // Keep the process running
        var resetEvent = new System.Threading.ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; resetEvent.Set(); };
        resetEvent.Wait();

        return 0;
    }

    static void OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open browser automatically: {ex.Message}");
            Console.WriteLine($"Please open this URL manually: {url}");
        }
    }
}
