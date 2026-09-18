using System;
using System.Runtime.InteropServices;

namespace _3DThumbnailShell.Shell
{
    /// <summary>
    /// 将软件渲染器的 BGRA（straight alpha，top-down）缓冲转换为带 alpha 的 32bpp DIB 段 HBITMAP。
    /// 由资源管理器接管所有权，调用方不得 Dispose。
    /// </summary>
    internal static class HbitmapFromBgra
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        public static IntPtr Create(int width, int height, byte[] bgra)
        {
            if (width <= 0 || height <= 0 || bgra == null || bgra.Length != width * height * 4)
                return IntPtr.Zero;

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // 负高度 = top-down DIB
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;  // BI_RGB
            bmi.bmiHeader.biSizeImage = (uint)(width * height * 4);

            IntPtr bits;
            var hbmp = CreateDIBSection(IntPtr.Zero, ref bmi, 0 /*DIB_RGB_COLORS*/, out bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero || bits == IntPtr.Zero)
                return IntPtr.Zero;

            Marshal.Copy(bgra, 0, bits, bgra.Length);
            return hbmp;
        }
    }
}
