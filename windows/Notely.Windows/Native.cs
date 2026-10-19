using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Notely.Core;
using Forms = System.Windows.Forms;

namespace Notely.Windows;

internal sealed class NativeWindow : IDisposable
{
    private readonly nint handle;
    private readonly HwndSource source;
    private readonly Window window;
    private bool constraining;
    private readonly Dictionary<int, Action> handlers = [];
    public bool CaptureExcluded { get; private set; }
    public string ProtectionStatus { get; private set; } = "";
    public List<string> HotkeyErrors { get; } = [];
    public NativeWindow(Window window)
    {
        this.window = window;
        handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle);
        source.AddHook(Hook);
        Protect();
    }
    public void Constrain()
    {
        if (constraining || window.WindowState != WindowState.Normal || !GetWindowRect(handle, out var rect)) return;
        constraining = true;
        try
        {
            var fitted = Fit(rect, Forms.Screen.FromHandle(handle).WorkingArea);
            if (!rect.Equals(fitted)) SetWindowPos(handle, 0, fitted.Left, fitted.Top,
                fitted.Right - fitted.Left, fitted.Bottom - fitted.Top, 0x0014); // no activation/z-order change
        }
        finally { constraining = false; }
    }
    private static Rect Fit(Rect rect, System.Drawing.Rectangle work)
    {
        var fitted = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            .Fit(new(work.Left, work.Top, work.Width, work.Height));
        return new() { Left = fitted.Left, Top = fitted.Top, Right = fitted.Left + fitted.Width, Bottom = fitted.Top + fitted.Height };
    }
    public void Protect()
    {
        CaptureExcluded = SetWindowDisplayAffinity(handle, 0x11) && GetWindowDisplayAffinity(handle, out var value) && value == 0x11;
        ProtectionStatus = CaptureExcluded ? "исключение из захвата включено (проверьте в звонке)" :
            "исключение из захвата НЕ включено: Win32 " + Marshal.GetLastWin32Error();
    }
    public void Add(Key key, Action handler)
    {
        var id = handlers.Count + 1;
        if (RegisterHotKey(handle, id, 0x0001 | 0x0002 | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(key))) handlers[id] = handler;
        else HotkeyErrors.Add($"Ctrl+Alt+{key}: занято другой программой");
    }
    internal static void ProtectPopup(System.Windows.Media.Visual visual)
    {
        if (PresentationSource.FromVisual(visual) is HwndSource popup)
            SetWindowDisplayAffinity(popup.Handle, 0x11);
    }
    private nint Hook(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
    {
        // This non-activating overlay must not maximize (ShowActivated=false + Maximized throws in WPF).
        if ((msg == 0x0112 && ((long)wparam & 0xfff0) == 0xf030) || (msg == 0x00A3 && wparam == 2))
        { handled = true; return 0; }
        if ((msg is 0x0216 or 0x0214) && lparam != 0) // WM_MOVING / WM_SIZING
        {
            var rect = Marshal.PtrToStructure<Rect>(lparam);
            var screen = msg == 0x0216 ? Forms.Screen.FromPoint(Forms.Cursor.Position) : Forms.Screen.FromHandle(handle);
            Marshal.StructureToPtr(Fit(rect, screen.WorkingArea), lparam, false);
            handled = true; return 1;
        }
        if (msg is 0x007E or 0x02E0 or 0x001A) window.Dispatcher.BeginInvoke(Constrain); // display/DPI/work-area change
        if (msg == 0x0312 && handlers.TryGetValue((int)wparam, out var action)) { action(); handled = true; }
        // WM_MOUSEACTIVATE: buttons and scrolling must not steal focus from the call.
        if (msg == 0x0021) { handled = true; return 3; }
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    public void Dispose()
    {
        foreach (var id in handlers.Keys) UnregisterHotKey(handle, id);
        source.RemoveHook(Hook);
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
