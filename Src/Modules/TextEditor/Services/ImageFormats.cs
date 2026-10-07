using System;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Формат картинки по её собственному содержимому.
    ///
    /// Расширению и типу содержимого из пакета верить нельзя: Word, пересохраняя
    /// документ, кладёт перекодированные картинки с расширением .bin и типом
    /// image/unknown, хотя внутри лежит обычный PNG. По сигнатуре файла формат
    /// определяется однозначно.
    ///
    /// Здесь же — приведение картинки к виду, который лист умеет показать: растры,
    /// которые читает сам лист, идут как есть, TIFF, EMF и WMF переводятся в PNG, а
    /// JPEG в CMYK — в JPEG в sRGB через цветовой профиль печати (<see cref="CmykJpeg"/>).
    /// </summary>
    internal static class ImageFormats
    {
        /// <summary>
        /// Расширение файла по сигнатуре: ".png", ".jpg", ".gif", ".bmp", ".tiff",
        /// ".ico", ".webp", ".emf", ".wmf". Пустая строка — формат не опознан.
        /// </summary>
        public static string SniffExtension(byte[]? data)
        {
            if (data is null || data.Length < 4) return string.Empty;

            if (data.Length >= 8
                && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
                && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
                return ".png";

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return ".jpg";

            if (data.Length >= 6
                && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8'
                && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
                return ".gif";

            if (data.Length >= 12
                && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
                && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
                return ".webp";

            if ((data[0] == (byte)'I' && data[1] == (byte)'I' && data[2] == 0x2A && data[3] == 0x00)
                || (data[0] == (byte)'M' && data[1] == (byte)'M' && data[2] == 0x00 && data[3] == 0x2A))
                return ".tiff";

            // Размещаемый WMF начинается со своего ключа, обычный — с типа и длины заголовка.
            if (data[0] == 0xD7 && data[1] == 0xCD && data[2] == 0xC6 && data[3] == 0x9A)
                return ".wmf";
            if ((data[0] == 0x01 || data[0] == 0x02) && data[1] == 0x00 && data[2] == 0x09 && data[3] == 0x00)
                return ".wmf";

            // EMF: первая запись — заголовок (тип 1), подпись « EMF» лежит на 40-м байте.
            if (data.Length >= 44
                && data[0] == 0x01 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x00
                && data[40] == (byte)' ' && data[41] == (byte)'E' && data[42] == (byte)'M' && data[43] == (byte)'F')
                return ".emf";

            if (data[0] == 0x00 && data[1] == 0x00 && (data[2] == 0x01 || data[2] == 0x02) && data[3] == 0x00)
                return ".ico";

            if (data.Length >= 26 && data[0] == (byte)'B' && data[1] == (byte)'M')
                return ".bmp";

            return string.Empty;
        }

        /// <summary>
        /// Формат, который лист сам не читает: его нужно перевести в PNG.
        /// </summary>
        public static bool NeedsTranscoding(string extension) =>
            extension is ".tiff" or ".tif" or ".emf" or ".wmf";

        /// <summary>
        /// Приводит картинку к виду, который лист умеет показать.
        /// </summary>
        /// <param name="data">Содержимое файла.</param>
        /// <param name="fallbackExtension">
        /// Расширение, которым картинку назвал источник: берётся, когда сигнатура
        /// не опознана.
        /// </param>
        /// <param name="normalizedData">Содержимое, которое нужно сохранить в проект.</param>
        /// <param name="normalizedExtension">Расширение для сохранения, с точкой.</param>
        /// <returns>
        /// False — картинку показать нечем: формат не опознан либо перевести его в PNG
        /// на этой системе нечем.
        /// </returns>
        public static bool TryNormalize(
            byte[] data, string? fallbackExtension,
            out byte[] normalizedData, out string normalizedExtension)
        {
            normalizedData = data;

            string extension = SniffExtension(data);
            if (extension.Length == 0)
                extension = (fallbackExtension ?? string.Empty).Trim().ToLowerInvariant();
            if (extension.Length > 0 && !extension.StartsWith(".", StringComparison.Ordinal))
                extension = "." + extension;

            normalizedExtension = extension;
            if (extension.Length == 0) return false;

            // JPEG в CMYK лист читает сам, но цвета у него выходят кислотными:
            // краска переводится в цвет без профиля печати. Копия в sRGB — тот же
            // цвет, что показывает Word.
            if (extension is ".jpg" or ".jpeg")
            {
                var rgb = CmykJpeg.TryConvertToRgb(data);
                if (rgb is not null)
                {
                    normalizedData = rgb;
                    normalizedExtension = ".jpg";
                }
                return true;
            }

            if (!NeedsTranscoding(extension)) return true;

            var png = WindowsImageTranscoder.ToPng(data);
            if (png is null) return false;

            normalizedData = png;
            normalizedExtension = ".png";
            return true;
        }
    }
}
