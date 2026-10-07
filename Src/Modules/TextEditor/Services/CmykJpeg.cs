using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Перевод JPEG в CMYK в обычный JPEG в sRGB — так, как его показывает Word.
    ///
    /// Краска переводится в цвет через цветовой профиль: встроенный в файл, а без
    /// него — профиль печати по умолчанию (<see cref="DefaultCmykProfile"/>). Наивный
    /// перевод системного декодера (краска вычитается из белого, чёрный размазан по
    /// трём каналам) давал кислотные цвета: печатный красный C10 M100 Y90 выходил
    /// чистым (228, 0, 25), а у Word он (208, 51, 51).
    ///
    /// Исходный файл при этом не теряется: импорт кладёт его рядом и отдаёт в .docx
    /// без изменений, перевод нужен только листу.
    /// </summary>
    internal static class CmykJpeg
    {
        /// <summary>Качество JPEG для копии листа: на глаз неотличимо от исходника.</summary>
        private const int DisplayJpegQuality = 95;

        /// <summary>
        /// Копия в sRGB для JPEG с четырьмя компонентами. Null — файл не CMYK, его
        /// не удалось разобрать или профиля нет: тогда картинка остаётся как есть.
        /// </summary>
        public static byte[]? TryConvertToRgb(byte[] data)
        {
            if (!CmykJpegDecoder.IsFourComponent(data)) return null;

            var decoded = CmykJpegDecoder.Decode(data);
            if (decoded is null) return null;

            var profile = CmykColorProfile.Parse(decoded.IccProfile) ?? DefaultCmykProfile.Profile;
            if (profile is null) return null;

            var rgba = new byte[decoded.Ink.Length];
            profile.ConvertInk(decoded.Ink, rgba);

            var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap(info);
            if (bitmap.GetPixels() == IntPtr.Zero) return null;
            if (bitmap.RowBytes != decoded.Width * 4) return null;

            Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
            bitmap.NotifyPixelsChanged();

            using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, DisplayJpegQuality);
            return encoded?.ToArray();
        }
    }
}
