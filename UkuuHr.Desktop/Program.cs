using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PhotinoNET;

namespace UkuuHr.Desktop;

/// <summary>
/// Ukuu HR Desktop Application — opens the web app in a native OS window.
/// 
/// The app connects to the live deployment at https://ukuu-hr-csharp.onrender.com
/// by default. Users can override with a --url= argument to connect to a
/// local or self-hosted instance.
/// </summary>
class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Default URL — the live Render deployment
        string url = "https://ukuu-hr-csharp.onrender.com";

        // Parse --url= argument
        foreach (var arg in args)
        {
            if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
                url = arg["--url=".Length..];
        }

        Console.WriteLine($"[Ukuu HR Desktop] Opening {url}");

        var window = new PhotinoWindow()
            .SetTitle("Ukuu HR — HRMS Platform")
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 900)
            .SetMinSize(1024, 600)
            .Center()
            .Load(url);

        window.WaitForClose();
        return 0;
    }
}
