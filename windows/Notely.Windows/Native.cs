using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Notely.Windows;

internal sealed class NativeWindow : IDisposable
{
    private readonly nint handle;
    private readonly HwndSource source;
    private readonly Dictionary<int, Action> handlers = [];
    public bool CaptureExcluded { get; private set; }
    public string ProtectionStatus { get; private set; } = "";
    public List<string> HotkeyErrors { get; } = [];
    public NativeWindow(Window window)
    {
        handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle);
        source.AddHook(Hook);
        Protect();
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
        if (msg == 0x0312 && handlers.TryGetValue((int)wparam, out var action)) { action(); handled = true; }
        // WM_MOUSEACTIVATE: buttons and scrolling must not steal focus from the call.
        if (msg == 0x0021) { handled = true; return 3; }
        return 0;
    }
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
