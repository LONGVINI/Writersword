using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;
using System;
using System.Runtime.InteropServices;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Поворот картинки на четверть оборота.
    ///
    /// Через копирование точек, а не через отрисовку в RenderTargetBitmap — по
    /// той же причине, что и обрезка в CharacterAvatarService: копирование не
    /// заводит поверхность рисования и не зависит от графического устройства,
    /// поэтому идёт одинаково на любом бэкенде и в любом потоке. Миниатюры
    /// строятся пачкой и не в UI-потоке, и поворот на этом пути обязан
    /// работать так же.
    ///
    /// Поворот делается в новый битмап один раз, а не преобразованием при
    /// отрисовке: кадр и все размеры вокруг него считаются по сторонам
    /// картинки, и поворот на лету пришлось бы учитывать в каждом расчёте.
    /// </summary>
    public static class AvatarImageRotation
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(AvatarImageRotation));

        /// <summary>
        /// Повёрнутая копия картинки. null — поворачивать нечего (ноль
        /// градусов) либо поворот не удался; в обоих случаях вызывающая сторона
        /// показывает исходник.
        ///
        /// Исходник не трогается и не освобождается: он принадлежит вызывающей
        /// стороне.
        /// </summary>
        public static Bitmap? Rotate(Bitmap? source, int rotation)
        {
            if (source == null) return null;

            var steps = ((rotation / 90) % 4 + 4) % 4;
            if (steps == 0) return null;

            try
            {
                var sourceWidth = source.PixelSize.Width;
                var sourceHeight = source.PixelSize.Height;
                if (sourceWidth <= 0 || sourceHeight <= 0) return null;

                var sourceStride = sourceWidth * 4;
                var bufferSize = sourceStride * sourceHeight;

                // Точки сначала уходят в неуправляемый буфер — другого способа
                // забрать их у битмапа нет, — и сразу переносятся в обычный
                // массив: по нему идёт перебор, и указатели для этого не нужны.
                var sourcePixels = new byte[bufferSize];
                var sourceBuffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    source.CopyPixels(
                        new PixelRect(0, 0, sourceWidth, sourceHeight),
                        sourceBuffer, bufferSize, sourceStride);
                    Marshal.Copy(sourceBuffer, sourcePixels, 0, bufferSize);
                }
                finally
                {
                    Marshal.FreeHGlobal(sourceBuffer);
                }

                // На четверть и на три четверти стороны меняются местами, на
                // половину остаются своими.
                var targetWidth = steps == 2 ? sourceWidth : sourceHeight;
                var targetHeight = steps == 2 ? sourceHeight : sourceWidth;
                var targetStride = targetWidth * 4;
                var targetPixels = new byte[bufferSize];

                // Перебор идёт по точкам итоговой картинки, а не исходной: так
                // каждая её точка заполняется ровно один раз и пропусков от
                // округления не остаётся.
                for (var y = 0; y < targetHeight; y++)
                {
                    for (var x = 0; x < targetWidth; x++)
                    {
                        int sourceX, sourceY;
                        switch (steps)
                        {
                            case 1:
                                sourceX = y;
                                sourceY = sourceHeight - 1 - x;
                                break;
                            case 2:
                                sourceX = sourceWidth - 1 - x;
                                sourceY = sourceHeight - 1 - y;
                                break;
                            default:
                                sourceX = sourceWidth - 1 - y;
                                sourceY = x;
                                break;
                        }

                        Buffer.BlockCopy(
                            sourcePixels, sourceY * sourceStride + sourceX * 4,
                            targetPixels, y * targetStride + x * 4,
                            4);
                    }
                }

                var targetBuffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    Marshal.Copy(targetPixels, 0, targetBuffer, bufferSize);

                    // Обычный Bitmap, а не WriteableBitmap: повёрнутую картинку
                    // ещё уменьшают под размер карточки, а уменьшение в Skia на
                    // источнике-WriteableBitmap падает с «Invalid source bitmap
                    // type». Конструктор копирует точки себе, поэтому буфер
                    // освобождается сразу же в finally.
                    return new Bitmap(
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul,
                        targetBuffer,
                        new PixelSize(targetWidth, targetHeight),
                        source.Dpi,
                        targetStride);
                }
                finally
                {
                    Marshal.FreeHGlobal(targetBuffer);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Rotate failed");
                return null;
            }
        }
    }
}
