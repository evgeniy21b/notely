using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Notely.Core;

namespace Notely.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--smoke-test") return Smoke(args.Length > 1 ? args[1] : "smoke-test.json");
        using var mutex = new Mutex(true, "Local\\Notely.Windows", out var first);
        if (!first) return 0;
        try
        {
            Paths.Ensure();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log.Write("ui", e.Exception.GetType().Name);
                if (app.MainWindow is MainWindow window) window.Report("Ошибка интерфейса: " + e.Exception.GetType().Name);
                e.Handled = true;
            };
            app.Run(new MainWindow());
            return 0;
        }
        catch (Exception e)
        {
            Log.Write("startup", e.GetType().Name);
            return 1;
        }
    }

    private static int Smoke(string reportPath)
    {
        var report = new Dictionary<string, object>();
        var failed = false;
        void Check(string name, Action test)
        {
            try { test(); report[name] = "PASS"; }
            catch (Exception e) { report[name] = e.GetType().Name; failed = true; }
        }
        Paths.UseTestDirectory(Path.Combine(Path.GetTempPath(), "Notely-smoke-" + Guid.NewGuid().ToString("N")));
        Check("storage", () => { Paths.Ensure(); var settings = Settings.Load(); settings.Save(); });
        Check("protected-keys", () =>
        {
            Secrets.Save(new ApiKeys("smoke-dummy-deepgram", "smoke-dummy-openrouter"));
            if (Secrets.Load()?.Deepgram != "smoke-dummy-deepgram") throw new IOException();
        });
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Check("window-and-capture-exclusion", () =>
        {
            var window = new Window { Width = 420, Height = 220, ShowInTaskbar = false };
            using var native = new NativeWindow(window);
            if (!native.CaptureExcluded) throw new IOException();
            window.Close();
        });
        Check("overlay-and-markdown", () =>
        {
            var window = new MainWindow(smoke: true);
            window.Show(); window.UpdateLayout();
            window.SmokeRender("Ответ **жирным** и `код`.\n\n```sql\nSELECT 1;\n```\n| A | B |\n|---|---|\n|1|2|");
            window.UpdateLayout(); window.Close();
        });
        Check("audio-decoder", () =>
        {
            var result = new PcmEncoder(48000, 2, 32, true).Convert(new byte[48000 * 8]);
            if (result.Data.Length != 32000) throw new IOException();
        });
        System.IO.File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        app.Shutdown();
        return failed ? 1 : 0;
    }
}
