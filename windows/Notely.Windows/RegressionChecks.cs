using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Notely.Core;
using Forms = System.Windows.Forms;

namespace Notely.Windows;

// Packaged-executable regressions. Only dummy keys, an isolated temporary directory and a fake AI.
internal static class RegressionChecks
{
    internal static int VerifyRestart(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) ||
            !Path.GetFileName(full).StartsWith("Notely-smoke-", StringComparison.Ordinal)) return 2;
        Paths.UseTestDirectory(full);
        return Secrets.Load() == new ApiKeys("smoke-dummy-deepgram", "smoke-dummy-openrouter") ? 0 : 1;
    }

    internal static void Keys()
    {
        Paths.UseTestDirectory(Path.Combine(Path.GetTempPath(), "Notely-smoke-" + Guid.NewGuid().ToString("N")));
        Paths.Ensure();
        using (var initial = new ClosingWindow())
            initial.Window.SmokeEditKeys("smoke-first-deepgram", ""); // Close before autosave can fire.
        Require(Secrets.Load() == new ApiKeys("smoke-first-deepgram", ""));
        using (var second = new ClosingWindow())
        {
            second.Window.SmokeEditKeys("", "smoke-first-openrouter");
            second.Window.SmokeCloseSettings();
        }
        Require(Secrets.Load() == new ApiKeys("smoke-first-deepgram", "smoke-first-openrouter"));
        RunAsync(async () =>
        {
            using var auto = new ClosingWindow();
            auto.Window.SmokeEditKeys("smoke-dummy-deepgram", "smoke-dummy-openrouter");
            await Task.Delay(750);
            Require(Secrets.Load() == new ApiKeys("smoke-dummy-deepgram", "smoke-dummy-openrouter"));
        });
        var encrypted = File.ReadAllBytes(Paths.File("keys.dat"));
        using (var blank = new ClosingWindow()) blank.Window.SmokeCloseSettings();
        Require(encrypted.SequenceEqual(File.ReadAllBytes(Paths.File("keys.dat"))));
        try { Secrets.Merge(Secrets.Load(), "broken\nkey", ""); throw new InvalidOperationException(); }
        catch (ArgumentException) { }
        Require(encrypted.SequenceEqual(File.ReadAllBytes(Paths.File("keys.dat"))));
        Directory.CreateDirectory(Paths.File("keys.dat.tmp")); // Simulate a write failure; keep last good keys.
        try { Secrets.Save(new("replacement", "replacement")); throw new InvalidOperationException(); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        finally { Directory.Delete(Paths.File("keys.dat.tmp")); }
        Require(encrypted.SequenceEqual(File.ReadAllBytes(Paths.File("keys.dat"))));
        File.WriteAllBytes(Paths.File("keys.dat"), [1, 2, 3]);
        Require(Secrets.Load() is null);
        using (var corrupt = new ClosingWindow()) corrupt.Window.SmokeCloseSettings();
        Require(File.ReadAllBytes(Paths.File("keys.dat")).SequenceEqual(new byte[] { 1, 2, 3 }));
        Secrets.Save(new("smoke-dummy-deepgram", "smoke-dummy-openrouter"));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--check-saved-keys"); start.ArgumentList.Add(Paths.DirectoryPath);
        using var child = Process.Start(start) ?? throw new IOException();
        _ = child.Handle;
        if (!child.WaitForExit(10000)) { child.Kill(); throw new TimeoutException(); }
        Require(child.ExitCode == 0);
    }

    internal static void Window()
    {
        using var owned = new ClosingWindow(); var window = owned.Window;
        window.ShowOverlay(); window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        var work = Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        foreach (var (x, y) in new[] { (-5000, 100), (5000, 100), (100, -5000), (100, 5000), (-5000, -5000), (5000, 5000) })
        {
            window.Left = x; window.Top = y; window.UpdateLayout();
            Require(GetWindowRect(handle, out var rect));
            Require(rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom);
        }
        window.Width = work.Width * 2 / dpi.DpiScaleX;
        window.Height = work.Height * 2 / dpi.DpiScaleY; window.UpdateLayout();
        Require(GetWindowRect(handle, out var oversized));
        Require(oversized.Right - oversized.Left <= work.Width && oversized.Bottom - oversized.Top <= work.Height);
        Require(window.ResizeMode == ResizeMode.CanResizeWithGrip);
        window.Hide(); window.WindowState = WindowState.Maximized;
        window.ShowOverlay(); // Regression for the exact exception reported by the tester.
        Require(window.IsVisible && window.WindowState == WindowState.Normal && !window.ShowActivated);
        SendMessage(handle, 0x0112, 0xf030, 0); // System maximize/double-click must not reintroduce it.
        Require(window.WindowState == WindowState.Normal);
    }

    internal static void Hotkey()
    {
        var receiver = new System.Windows.Window { ShowActivated = false, ShowInTaskbar = false, Width = 200, Height = 100 };
        using var native = new NativeWindow(receiver);
        var calls = 0; native.Add(Key.S, () => calls++);
        Require(native.HotkeyErrors.Count == 0);
        var other = new System.Windows.Window { Width = 200, Height = 100, ShowInTaskbar = false };
        try
        {
            other.Show(); other.Activate();
            SendMessage(new WindowInteropHelper(receiver).Handle, 0x0312, 1, 0); // WM_HOTKEY without receiver focus
            Require(calls == 1 && !receiver.IsActive);
            using var collision = new NativeWindow(other);
            collision.Add(Key.S, () => throw new InvalidOperationException());
            Require(collision.HotkeyErrors.Count == 1);
        }
        finally { other.Close(); receiver.Close(); }
    }

    internal static void Screenshot() => RunAsync(async () =>
    {
        var fake = new ScreenshotClient();
        using var brain = new Brain(fake, () => "", () => "test");
        using var owned = new ClosingWindow(); var window = owned.Window;
        window.ShowOverlay();
        // Exercise the same capture/Attach/Send path used by the button and Ctrl+Alt+S. No extra Send click.
        await Task.WhenAll(window.SmokeScreenshotAsync(brain), window.SmokeScreenshotAsync(brain));
        var request = await fake.Requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Require(request.Image is { Length: > 0 } && request.Image.Length <= 8_000_000);
        Require(request.Context.Contains("Проверь экран"));
        Require(!fake.Requests.Reader.TryRead(out _));
    });

    private sealed class ScreenshotClient : ICompletionClient
    {
        public readonly Channel<CompletionRequest> Requests = Channel.CreateUnbounded<CompletionRequest>();
        public async IAsyncEnumerable<string> StreamAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Writer.TryWrite(request);
            await Task.Yield(); yield return "Экран: тестовый рабочий стол. Ответ.";
        }
    }
    private sealed class ClosingWindow : IDisposable
    {
        public MainWindow Window { get; } = new(smoke: true);
        public void Dispose() => Window.Close();
    }
    private static void Require(bool condition, [CallerLineNumber] int line = 0)
    { if (!condition) throw new InvalidOperationException("Regression check failed at line " + line); }
    private static void RunAsync(Func<Task> action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = action();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int msg, nint wparam, nint lparam);
}
