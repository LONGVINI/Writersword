using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>
    /// Сглаживание текста на экране — как в Word: ClearType там, где его включил
    /// Windows.
    ///
    /// Серое сглаживание кладёт на пиксель одну долю покрытия на все три цвета.
    /// ClearType считает покрытие отдельно для красной, зелёной и синей полоски
    /// пикселя, и по горизонтали у букв втрое больше точности. На крупном тексте
    /// разницы почти нет, а на мелком она решает: у стрелки «→» кеглем 8,5 острие
    /// шириной в пару пикселей серым сглаживанием растворялось в черту, и знак
    /// переставал читаться как стрелка. Word рисует те же буквы через ClearType, и
    /// у него острие видно.
    ///
    /// Порядок полосок в пикселе (RGB или BGR) и то, включён ли ClearType вообще,
    /// берутся из настроек Windows — те же, что человек выставил в «Настройке
    /// текста ClearType». Выключил — текст остаётся серым, как в остальной системе.
    /// Вне Windows ClearType не включается.
    ///
    /// Субпиксели ложатся точно на пиксели экрана только тогда, когда снимок кадра
    /// выводится один к одному. Снимок растрируется в точках интерфейса, и при
    /// масштабе системы больше 100% окно его растягивает — цветные полоски букв
    /// тогда расползлись бы в кайму. Поэтому сглаживание зависит ещё и от того, как
    /// окно выводит снимок (<see cref="GeometryFor"/>).
    /// </summary>
    public static class ScreenTextSmoothing
    {
        private const uint SPI_GETFONTSMOOTHING = 0x004A;
        private const uint SPI_GETFONTSMOOTHINGTYPE = 0x200A;
        private const uint SPI_GETFONTSMOOTHINGORIENTATION = 0x2012;

        private const uint FE_FONTSMOOTHINGCLEARTYPE = 0x0002;
        private const uint FE_FONTSMOOTHINGORIENTATIONRGB = 0x0001;

        // Настройки Windows перечитываются не чаще раза в две секунды: вопрос
        // задаётся на каждом кадре, а меняет их человек раз в годы. Две секунды —
        // столько после смены настройки текст ещё рисуется по-старому.
        private const long RefreshIntervalMs = 2000;

        private static readonly object _lock = new();

        private static SKPixelGeometry _systemGeometry = SKPixelGeometry.Unknown;

        private static long _readAtMs;

        private static bool _everRead;

        /// <summary>
        /// Порядок субпикселей, с которым Windows рисует текст, или Unknown, если
        /// ClearType выключен или система не Windows.
        /// </summary>
        public static SKPixelGeometry SystemGeometry
        {
            get
            {
                long now = Environment.TickCount64;

                lock (_lock)
                {
                    if (!_everRead || now - _readAtMs >= RefreshIntervalMs)
                    {
                        _systemGeometry = ReadSystemGeometry();
                        _readAtMs = now;
                        _everRead = true;
                    }

                    return _systemGeometry;
                }
            }
        }

        /// <summary>
        /// Порядок субпикселей для снимка кадра, который окно выводит с матрицей
        /// deviceMatrix. Unknown — сглаживать серым: снимок будет растянут, повёрнут
        /// или ClearType выключен в системе.
        ///
        /// Сдвиг в матрице значения не имеет: снимок выводится без сглаживания
        /// выборки, и дробный сдвиг переносит его на целый пиксель, не смешивая
        /// соседние столбцы.
        /// </summary>
        public static SKPixelGeometry GeometryFor(SKMatrix deviceMatrix)
        {
            if (!IsOneToOne(deviceMatrix)) return SKPixelGeometry.Unknown;
            return SystemGeometry;
        }

        /// <summary>
        /// Поверхность поверх пикселей готового битмапа с заданным порядком
        /// субпикселей. Для Unknown — null: такой битмап рисуется обычным холстом,
        /// как и прежде. Память битмапа не копируется: поверхность пишет прямо в неё.
        /// </summary>
        public static SKSurface? CreateSurface(SKBitmap target, SKPixelGeometry geometry)
        {
            if (target is null || geometry == SKPixelGeometry.Unknown) return null;

            using var props = new SKSurfaceProperties(geometry);
            return SKSurface.Create(target.Info, target.GetPixels(), target.RowBytes, props);
        }

        private static bool IsOneToOne(SKMatrix m)
        {
            const float eps = 0.001f;

            return Math.Abs(m.ScaleX - 1f) < eps
                && Math.Abs(m.ScaleY - 1f) < eps
                && Math.Abs(m.SkewX) < eps
                && Math.Abs(m.SkewY) < eps
                && Math.Abs(m.Persp0) < eps
                && Math.Abs(m.Persp1) < eps
                && Math.Abs(m.Persp2 - 1f) < eps;
        }

        private static SKPixelGeometry ReadSystemGeometry()
        {
            if (!OperatingSystem.IsWindows()) return SKPixelGeometry.Unknown;

            try
            {
                int smoothing = 0;
                if (!SystemParametersInfoInt(SPI_GETFONTSMOOTHING, 0, ref smoothing, 0) || smoothing == 0)
                    return SKPixelGeometry.Unknown;

                uint type = 0;
                if (!SystemParametersInfoUInt(SPI_GETFONTSMOOTHINGTYPE, 0, ref type, 0)
                    || type != FE_FONTSMOOTHINGCLEARTYPE)
                    return SKPixelGeometry.Unknown;

                // Порядок не прочитался — у подавляющего большинства экранов RGB,
                // его Windows и берёт по умолчанию.
                uint orientation = FE_FONTSMOOTHINGORIENTATIONRGB;
                if (!SystemParametersInfoUInt(SPI_GETFONTSMOOTHINGORIENTATION, 0, ref orientation, 0))
                    orientation = FE_FONTSMOOTHINGORIENTATIONRGB;

                return orientation == FE_FONTSMOOTHINGORIENTATIONRGB
                    ? SKPixelGeometry.RgbHorizontal
                    : SKPixelGeometry.BgrHorizontal;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return SKPixelGeometry.Unknown;
            }
        }

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfoInt(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfoUInt(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);
    }
}
