using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Notely.Windows;

internal static class Images
{
    // Capture the monitor under the cursor; hiding is done by the caller only for a question screenshot.
    public static BitmapSource Capture()
    {
        var bounds = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); return source;
        }
        finally { DeleteObject(handle); }
    }

    public static byte[] Jpeg(BitmapSource image)
    {
        if ((long)image.PixelWidth * image.PixelHeight > 100_000_000) throw new IOException("Слишком большое изображение");
        var scale = Math.Min(1, 2400.0 / Math.Max(image.PixelWidth, image.PixelHeight));
        BitmapSource resized = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
        var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
        encoder.Frames.Add(BitmapFrame.Create(resized));
        using var stream = new MemoryStream(); encoder.Save(stream);
        if (stream.Length > 8_000_000) throw new IOException("Изображение не удалось уменьшить");
        return stream.ToArray();
    }

    public static string SaveCapture(BitmapSource image)
    {
        var path = Paths.File("invisibility-test.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = System.IO.File.Create(path); encoder.Save(stream); return path;
    }
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
}
