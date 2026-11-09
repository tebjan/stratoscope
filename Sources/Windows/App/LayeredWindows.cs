using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

namespace Stratoscope.Windows;

/// <summary>What the taskbar readout and the tooltip share: window classes, creation, and handing a premultiplied bitmap to a layered window.</summary>
internal static class LayeredWindows
{
    /// <summary>Registers a window class with the arrow cursor. A second call for the same class fails harmlessly.</summary>
    public static unsafe void RegisterClass(string name, WNDPROC proc)
    {
        fixed (char* className = name)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = proc, hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(HINSTANCE.Null, IDC_ARROW), lpszClassName = className,
            };
            RegisterClassEx(in wc);
        }
    }

    /// <summary>Creates a window at the origin of its parent, without a menu or creation data.</summary>
    public static unsafe HWND Create(WINDOW_EX_STYLE exStyle, string className, string title, WINDOW_STYLE style, int width, int height, HWND parent) =>
        CreateWindowEx(exStyle, className, title, style, 0, 0, width, height, parent, HMENU.Null, GetModuleHandle(null), null);

    /// <summary>Gives the window its pixels: a premultiplied 32-bit bitmap through UpdateLayeredWindow, so per-pixel transparency works.</summary>
    public static unsafe void Present(HWND window, Bitmap bmp, Point positionInParent)
    {
        var screenDc = GetDC(HWND.Null);
        var memDc = CreateCompatibleDC(screenDc);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = bmp.Width, biHeight = -bmp.Height, biPlanes = 1, biBitCount = 32 },
        };
        void* bits;
        var dib = CreateDIBSection(memDc, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        var old = HGDIOBJ.Null;
        try
        {
            if (dib.IsNull) return;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, (IntPtr)bits, Math.Min(bytes.Length, bmp.Width * 4 * bmp.Height));
            }
            finally { bmp.UnlockBits(data); }
            old = SelectObject(memDc, dib);
            var blend = new BLENDFUNCTION { BlendOp = (byte)AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = (byte)AC_SRC_ALPHA };
            UpdateLayeredWindow(window, screenDc, positionInParent, bmp.Size, memDc, Point.Empty, default, blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
        }
        finally
        {
            if (!old.IsNull) SelectObject(memDc, old);
            if (!dib.IsNull) DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(HWND.Null, screenDc);
        }
    }
}
