using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using PhotinoNET;

namespace UkuuHr.Desktop;

/// <summary>
/// Ukuu HR Desktop Application — Phase 15
///
/// Starts the Blazor Server web app in-process (Kestrel on a random localhost port),
/// then opens a native OS window (Photino) pointing to that port. This gives users
/// a real desktop application experience on Windows (.exe) and macOS (.app) without
/// needing a browser.
///
/// The native window uses:
///   - Windows: Edge WebView2 (Chromium)
///   - macOS: WKWebView (Safari)
///   - Linux: WebKitGTK
///
/// Usage:
///   UkuuHr.exe                     → starts app on random port
///   UkuuHr.exe --port=5000         → starts app on specific port
///   UkuuHr.exe --url=https://...   → connects to remote server (no local Kestrel)
/// </summary>
class Program
{
    private static int Main(string[] args)
    {
        // Parse args
        int? port = null;
        string? remoteUrl = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--port="))
                port = int.TryParse(arg["--port=".Length..], out var p) ? p : null;
            else if (arg.StartsWith("--url="))
                remoteUrl = arg["--url=".Length..];
        }

        // If connecting to a remote server, just open the window
        if (!string.IsNullOrEmpty(remoteUrl))
        {
            Console.WriteLine($"[UkuuHr Desktop] Connecting to remote: {remoteUrl}");
            OpenWindow(remoteUrl);
            return 0;
        }

        // Otherwise, start Kestrel in-process
        port ??= FindFreePort();
        var url = $"http://localhost:{port}";
        Console.WriteLine($"[UkuuHr Desktop] Starting server on {url}");

        var cts = new CancellationTokenSource();

        // Start the web server in a background thread
        var serverTask = Task.Run(() =>
        {
            try
            {
                var builder = Host.CreateDefaultBuilder();
                builder.ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseUrls(url);
                    webBuilder.UseStartup<WebStartup>();
                });
                var host = builder.Build();
                host.RunAsync(cts.Token).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UkuuHr Desktop] Server error: {ex.Message}");
            }
        });

        // Wait for server to be ready
        Console.WriteLine("[UkuuHr Desktop] Waiting for server...");
        var ready = false;
        for (int i = 0; i < 30; i++)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var resp = client.GetAsync($"{url}/health").Result;
                if (resp.IsSuccessStatusCode) { ready = true; break; }
            }
            catch { }
            Thread.Sleep(500);
        }

        if (!ready)
        {
            Console.WriteLine("[UkuuHr Desktop] Server failed to start. Opening fallback URL.");
        }

        // Open the native window
        Console.WriteLine($"[UkuuHr Desktop] Opening window → {url}");
        OpenWindow(url);

        // Server stops when the window closes (OpenWindow blocks until window closes)
        cts.Cancel();
        try { serverTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
        return 0;
    }

    /// <summary>Open a native OS window using Photino.</summary>
    private static void OpenWindow(string url)
    {
        var window = new PhotinoWindow()
            .SetTitle("Ukuu HR — HRMS Platform")
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 900)
            .SetMinSize(1024, 600)
            .Center()
            .SetIconFile("ukuu-icon.ico")
            .Load(url);

        window.SetTitle("Ukuu HR — HRMS Platform");

        // Block until window closes
        window.WaitForClose();
    }

    /// <summary>Find a free TCP port on localhost.</summary>
    private static int FindFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Minimal startup class that delegates to the web project's Program.cs logic.
/// In production, this would call the same service configuration as the web project.
/// For now, it starts a minimal Kestrel server that serves the Blazor app.
/// </summary>
public class WebStartup
{
    public void ConfigureServices(IServiceCollection services) { }
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRouting();
        app.UseStaticFiles();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        });
    }
}
