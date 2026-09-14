using System.Windows.Media.Imaging;

namespace PDFPlus.Services;

public static class AppIcon
{
    private static IconBitmapDecoder? _decoder;

    /// <summary>Returns the icon frame closest to the requested pixel size (WPF otherwise picks the first, tiny frame).</summary>
    public static BitmapSource Get(int size)
    {
        _decoder ??= new IconBitmapDecoder(new Uri("pack://application:,,,/Assets/PDFPlus.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return _decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
    }
}
