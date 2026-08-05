using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Serilog;
using SkiaSharp;
using Writersword.Modules.Characters.Interfaces;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Картинки значков меток: растр и вектор приводятся к одному виду —
    /// готовому битмапу нужного размера.
    ///
    /// Вектор растеризуется здесь, а не отдаётся в разметку особым контролом,
    /// по двум причинам. Во-первых, значок метки встречается в шести местах
    /// программы, и каждое из них умеет показывать битмап — своя ветка вывода
    /// под вектор означала бы шесть развилок вместо одной. Во-вторых, значок
    /// рисуется размером от шестнадцати до тридцати четырёх точек, а разбор
    /// вектора при каждой перерисовке списка обошёлся бы дороже самого
    /// рисования.
    ///
    /// Результат кэшируется по ссылке, цвету перекраски и размеру: один и тот
    /// же герб может стоять у сотни персонажей.
    /// </summary>
    public static class LabelIconImages
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(LabelIconImages));

        /// <summary>Ставится модулем при инициализации.</summary>
        public static ICharacterAvatarService? AvatarService { get; set; }

        private static readonly Dictionary<(string Reference, uint Tint, int MaxSide), Bitmap?> _cache = new();

        /// <summary>
        /// Вектор опознаётся по расширению в ссылке: ссылка хранит имя файла,
        /// под которым картинка легла в проект, вместе с расширением.
        /// </summary>
        public static bool IsVector(string? reference) =>
            !string.IsNullOrWhiteSpace(reference) &&
            reference.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Картинка значка. Цвет перекраски применяется только к вектору:
        /// растр идёт как есть — перекрашивать чужой герб или фотографию
        /// программа не берётся.
        /// </summary>
        public static Bitmap? Get(string? reference, Color? tint, int maxSide)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            if (AvatarService == null) return null;
            if (maxSide < 1) maxSide = 1;

            var vector = IsVector(reference);
            var key = (reference, vector && tint.HasValue ? tint.Value.ToUInt32() : 0u, maxSide);

            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;
            }

            Bitmap? result = null;
            try
            {
                result = vector
                    ? RenderVector(reference, tint, maxSide)
                    : AvatarService.LoadBitmap(reference, maxSide);
            }
            catch (Exception ex)
            {
                // Пропавшая или битая картинка не должна ронять список:
                // значок откатится на встроенную фигуру, а ссылку можно
                // убрать в редакторе метки.
                _logger.Error(ex, "Label icon load failed for {Ref}", reference);
            }

            lock (_cache)
            {
                _cache[key] = result;
            }
            return result;
        }

        /// <summary>Сбросить кэш — после смены проекта картинки уже другие.</summary>
        public static void ResetCache()
        {
            lock (_cache)
            {
                foreach (var bitmap in _cache.Values) bitmap?.Dispose();
                _cache.Clear();
            }
        }

        // ── Вектор ────────────────────────────────────────────────────────

        private static Bitmap? RenderVector(string reference, Color? tint, int maxSide)
        {
            var bytes = AvatarService?.LoadAvatarBytes(reference);
            if (bytes == null || bytes.Length == 0) return null;

            var markup = DecodeText(bytes);
            if (tint.HasValue) markup = Recolor(markup, tint.Value);

            using var svg = new Svg.Skia.SKSvg();
            var picture = svg.FromSvg(markup);
            if (picture == null) return null;

            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            var scale = maxSide / Math.Max(bounds.Width, bounds.Height);
            var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface == null) return null;

            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale(scale);
            surface.Canvas.Translate(-bounds.Left, -bounds.Top);
            surface.Canvas.DrawPicture(picture);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return null;

            using var stream = new MemoryStream();
            data.SaveTo(stream);
            stream.Position = 0;
            return new Bitmap(stream);
        }

        // Метка задаёт вектору цвет так же, как встроенной фигуре. Разбирать
        // документ ради этого незачем: цвет в SVG живёт в трёх местах —
        // атрибуте, инлайновом стиле и ключевом слове currentColor, — и все
        // три подменяются по тексту. Значения none и transparent не трогаются:
        // ими автор картинки отключает заливку или обводку, и подмена
        // залила бы дырки в рисунке.
        private static string Recolor(string markup, Color color)
        {
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            markup = Regex.Replace(
                markup,
                @"(?<name>\b(?:fill|stroke))\s*=\s*(?<q>[""'])(?<value>.*?)\k<q>",
                m => IsKeepAsIs(m.Groups["value"].Value)
                    ? m.Value
                    : $"{m.Groups["name"].Value}=\"{hex}\"",
                RegexOptions.IgnoreCase);

            markup = Regex.Replace(
                markup,
                @"(?<name>\b(?:fill|stroke))\s*:\s*(?<value>[^;""'}]+)",
                m => IsKeepAsIs(m.Groups["value"].Value)
                    ? m.Value
                    : $"{m.Groups["name"].Value}:{hex}",
                RegexOptions.IgnoreCase);

            markup = Regex.Replace(markup, "currentColor", hex, RegexOptions.IgnoreCase);

            // Рисунок без единого упоминания заливки берёт чёрный цвет по
            // умолчанию — цвет метки задаётся ему на корневом узле и дальше
            // наследуется всеми фигурами.
            if (!Regex.IsMatch(markup, @"\bfill\s*[=:]", RegexOptions.IgnoreCase))
            {
                markup = Regex.Replace(
                    markup,
                    @"<svg\b",
                    $"<svg fill=\"{hex}\"",
                    RegexOptions.IgnoreCase);
            }

            return markup;
        }

        private static bool IsKeepAsIs(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length == 0
                || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("transparent", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase);
        }

        // Кодировка берётся из метки порядка байтов, если она есть; иначе
        // UTF-8 — так SVG отдают все распространённые редакторы.
        private static string DecodeText(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }
}
