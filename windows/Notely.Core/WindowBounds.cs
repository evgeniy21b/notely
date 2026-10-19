namespace Notely.Core;

// Physical pixels, including negative coordinates on monitors left/above the primary one.
public readonly record struct WindowBounds(int Left, int Top, int Width, int Height)
{
    public WindowBounds Fit(WindowBounds work)
    {
        if (work.Width <= 0 || work.Height <= 0) throw new ArgumentException("Empty work area");
        var width = Math.Clamp(Width, 1, work.Width);
        var height = Math.Clamp(Height, 1, work.Height);
        return new(Math.Clamp(Left, work.Left, work.Left + work.Width - width),
            Math.Clamp(Top, work.Top, work.Top + work.Height - height), width, height);
    }
}
