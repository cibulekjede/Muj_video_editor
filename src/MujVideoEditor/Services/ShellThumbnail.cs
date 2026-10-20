using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MujVideoEditor.Services;

/// <summary>
/// Nacita nahledove obrazky souboru stejne jako Pruzkumnik Windows
/// (funguje pro videa, obrazky i hudbu s obalem alba).
/// </summary>
internal static class ShellThumbnail
{
    public static BitmapSource? TryGet(string filePath, int size)
    {
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iid, out var factory) != 0)
                return null;

            try
            {
                if (factory.GetImage(new SIZE(size, size), SIIGBF.ThumbnailOnly, out IntPtr hBitmap) != 0)
                    return null;

                try
                {
                    var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze(); // aby sel obrazek pouzit i mimo vlakno, kde vznikl
                    return bitmap;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        ThumbnailOnly = 0x08
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SIZE(int cx, int cy)
    {
        public readonly int Cx = cx;
        public readonly int Cy = cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
