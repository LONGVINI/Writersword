using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Перевод в PNG картинок, которые лист сам не читает: TIFF, EMF и WMF.
    ///
    /// Читает их системный GDI+ — тот же код, которым эти форматы показывает сам
    /// Windows. Растр (TIFF, в том числе многостраничный — берётся первая страница)
    /// переносится точка в точку. Метафайл проигрывается на прозрачный холст: места,
    /// где в нём ничего не нарисовано, остаются прозрачными, как на листе Word.
    ///
    /// Вне Windows перевод недоступен: метод возвращает null, и вызывающий сообщает,
    /// что формат не поддержан.
    /// </summary>
    internal static class WindowsImageTranscoder
    {
        private const int Ok = 0;
        private const int ImageTypeMetafile = 2;
        private const int PixelFormat32bppArgb = 0x0026200A;
        private const uint ImageLockModeRead = 1;
        private const int InterpolationHighQualityBicubic = 7;
        private const int SmoothingAntiAlias = 4;

        /// <summary>Единиц размера метафайла (0,01 мм) в дюйме.</summary>
        private const float HimetricPerInch = 2540f;

        /// <summary>
        /// Плотность, с которой метафайл переводится в точки: втрое плотнее экрана,
        /// чтобы линии оставались чёткими при увеличении листа.
        /// </summary>
        private const float MetafileDpi = 288f;

        /// <summary>Предел стороны растра из метафайла, точек.</summary>
        private const int MaxMetafileSidePx = 4096;

        /// <summary>Предел стороны исходного растра, точек: защита от испорченного файла.</summary>
        private const int MaxBitmapSidePx = 20000;

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GpRect
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapData
        {
            public uint Width;
            public uint Height;
            public int Stride;
            public int PixelFormat;
            public IntPtr Scan0;
            public IntPtr Reserved;
        }

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern void GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipLoadImageFromStream(IntPtr stream, out IntPtr image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGetImageType(IntPtr image, out int type);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGetImageWidth(IntPtr image, out uint width);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGetImageHeight(IntPtr image, out uint height);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGetImageDimension(IntPtr image, out float width, out float height);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipBitmapLockBits(
            IntPtr bitmap, ref GpRect rect, uint flags, int format, ref BitmapData lockedData);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref BitmapData lockedData);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipCreateBitmapFromScan0(
            int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipGraphicsClear(IntPtr graphics, int color);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipSetSmoothingMode(IntPtr graphics, int mode);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipDrawImageRectI(
            IntPtr graphics, IntPtr image, int x, int y, int width, int height);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipDeleteGraphics(IntPtr graphics);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("shlwapi.dll", ExactSpelling = true)]
        private static extern IntPtr SHCreateMemStream(byte[] initialData, uint initialSize);

        /// <summary>
        /// Переводит картинку в PNG. Null — перевести не удалось: система не Windows,
        /// файл испорчен или GDI+ его не читает.
        /// </summary>
        public static byte[]? ToPng(byte[]? data)
        {
            if (data is null || data.Length == 0) return null;
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                return ToPngCore(data);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            catch (ExternalException)
            {
                return null;
            }
        }

        private static byte[]? ToPngCore(byte[] data)
        {
            var startup = new GdiplusStartupInput { GdiplusVersion = 1 };
            if (GdiplusStartup(out IntPtr token, ref startup, IntPtr.Zero) != Ok) return null;

            IntPtr stream = IntPtr.Zero;
            IntPtr image = IntPtr.Zero;
            try
            {
                stream = SHCreateMemStream(data, (uint)data.Length);
                if (stream == IntPtr.Zero) return null;

                if (GdipLoadImageFromStream(stream, out image) != Ok || image == IntPtr.Zero)
                    return null;

                if (GdipGetImageType(image, out int imageType) != Ok) return null;

                return imageType == ImageTypeMetafile
                    ? MetafileToPng(image)
                    : BitmapToPng(image);
            }
            finally
            {
                if (image != IntPtr.Zero) GdipDisposeImage(image);
                if (stream != IntPtr.Zero) Marshal.Release(stream);
                GdiplusShutdown(token);
            }
        }

        /// <summary>Растр: точки берутся как есть, в 32 битах с прозрачностью.</summary>
        private static byte[]? BitmapToPng(IntPtr image)
        {
            if (GdipGetImageWidth(image, out uint width) != Ok) return null;
            if (GdipGetImageHeight(image, out uint height) != Ok) return null;
            if (width == 0 || height == 0 || width > MaxBitmapSidePx || height > MaxBitmapSidePx)
                return null;

            var rect = new GpRect { X = 0, Y = 0, Width = (int)width, Height = (int)height };
            var locked = new BitmapData();

            if (GdipBitmapLockBits(image, ref rect, ImageLockModeRead, PixelFormat32bppArgb, ref locked) != Ok)
                return null;

            try
            {
                int rowBytes = (int)width * 4;
                var pixels = new byte[rowBytes * (int)height];

                // Строки копируются по одной: шаг строки у GDI+ может быть больше её длины,
                // а у перевёрнутого растра — отрицательным.
                for (int y = 0; y < (int)height; y++)
                {
                    IntPtr row = IntPtr.Add(locked.Scan0, y * locked.Stride);
                    Marshal.Copy(row, pixels, y * rowBytes, rowBytes);
                }

                return EncodePng(pixels, (int)width, (int)height);
            }
            finally
            {
                GdipBitmapUnlockBits(image, ref locked);
            }
        }

        /// <summary>Метафайл: проигрывается на прозрачный холст в своих пропорциях.</summary>
        private static byte[]? MetafileToPng(IntPtr image)
        {
            if (GdipGetImageDimension(image, out float himetricWidth, out float himetricHeight) != Ok)
                return null;
            if (!(himetricWidth > 0f) || !(himetricHeight > 0f)) return null;

            float widthPx = himetricWidth / HimetricPerInch * MetafileDpi;
            float heightPx = himetricHeight / HimetricPerInch * MetafileDpi;

            float longest = Math.Max(widthPx, heightPx);
            if (longest > MaxMetafileSidePx)
            {
                float shrink = MaxMetafileSidePx / longest;
                widthPx *= shrink;
                heightPx *= shrink;
            }

            int width = Math.Max(1, (int)Math.Round(widthPx));
            int height = Math.Max(1, (int)Math.Round(heightPx));
            int stride = width * 4;

            var pixels = new byte[stride * height];
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            IntPtr canvas = IntPtr.Zero;
            IntPtr graphics = IntPtr.Zero;
            try
            {
                if (GdipCreateBitmapFromScan0(
                        width, height, stride, PixelFormat32bppArgb, pin.AddrOfPinnedObject(), out canvas) != Ok
                    || canvas == IntPtr.Zero)
                    return null;

                if (GdipGetImageGraphicsContext(canvas, out graphics) != Ok || graphics == IntPtr.Zero)
                    return null;

                GdipGraphicsClear(graphics, 0);
                GdipSetInterpolationMode(graphics, InterpolationHighQualityBicubic);
                GdipSetSmoothingMode(graphics, SmoothingAntiAlias);

                if (GdipDrawImageRectI(graphics, image, 0, 0, width, height) != Ok) return null;
            }
            finally
            {
                // Холст освобождается до снятия закрепления: пока он жив, GDI+ пишет
                // прямо в массив точек.
                if (graphics != IntPtr.Zero) GdipDeleteGraphics(graphics);
                if (canvas != IntPtr.Zero) GdipDisposeImage(canvas);
                pin.Free();
            }

            return EncodePng(pixels, width, height);
        }

        /// <summary>Точки GDI+ (B, G, R, A без предумножения) — в файл PNG.</summary>
        private static byte[]? EncodePng(byte[] pixels, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var skImage = SKImage.FromPixelCopy(info, pin.AddrOfPinnedObject(), width * 4);
                if (skImage is null) return null;

                using var encoded = skImage.Encode(SKEncodedImageFormat.Png, 100);
                return encoded?.ToArray();
            }
            finally
            {
                pin.Free();
            }
        }
    }
}
