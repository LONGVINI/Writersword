using System;
using System.Globalization;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Кадр аватарки — прямоугольник в долях исходной картинки (0..1).
    ///
    /// Доли, а не пиксели: одна и та же картинка отдаётся в разных размерах
    /// (миниатюра пикера, кружок карточки, полоса на всю зону), и служба
    /// уменьшает её перед выдачей. Пиксельный кадр пришлось бы пересчитывать
    /// под каждый размер и он рассыпался бы при смене AvatarMaxSide; доли
    /// переживают любое масштабирование.
    ///
    /// Кадр принадлежит персонажу, а не файлу: одна фотография с двумя людьми
    /// может стоять на двух карточках, вырезанная по-разному. Поэтому кадр
    /// едет прицепом к ссылке на аватар (см. CharacterAvatarRef), а сам файл
    /// в проекте лежит один.
    /// </summary>
    public sealed class CharacterAvatarCrop : IEquatable<CharacterAvatarCrop>
    {
        /// <summary>Погрешность сравнения долей: пятый знак — предел записи.</summary>
        private const double Epsilon = 0.000005;

        /// <summary>Наименьшая доля стороны, которую разрешено вырезать.</summary>
        private const double MinSide = 0.01;

        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }

        /// <summary>Кадр во всю картинку — то же, что отсутствие кадра.</summary>
        public static CharacterAvatarCrop Full { get; } = new(0.0, 0.0, 1.0, 1.0);

        /// <summary>
        /// Значения приводятся к допустимым прямо в конструкторе: кадр приходит
        /// из окна обрезки и из сохранённых данных проекта, и ни один из двух
        /// источников не обязан быть исправным.
        /// </summary>
        public CharacterAvatarCrop(double x, double y, double width, double height)
        {
            var w = Clamp(width, MinSide, 1.0);
            var h = Clamp(height, MinSide, 1.0);
            var px = Clamp(x, 0.0, 1.0 - w);
            var py = Clamp(y, 0.0, 1.0 - h);

            X = px;
            Y = py;
            Width = w;
            Height = h;
        }

        /// <summary>Кадр совпадает со всей картинкой — хранить его незачем.</summary>
        public bool IsFull =>
            X <= Epsilon && Y <= Epsilon
            && Width >= 1.0 - Epsilon && Height >= 1.0 - Epsilon;

        /// <summary>Соотношение сторон вырезанного куска относительно исходного.</summary>
        public double RelativeAspect => Height <= 0.0 ? 1.0 : Width / Height;

        /// <summary>
        /// Кадр в пикселях исходной картинки. Стороны не меньше одного пикселя:
        /// нулевая ширина или высота уронила бы создание битмапа.
        /// </summary>
        public Avalonia.PixelRect ToPixelRect(int sourceWidth, int sourceHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return new Avalonia.PixelRect(0, 0, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));

            var w = Math.Max(1, (int)Math.Round(Width * sourceWidth));
            var h = Math.Max(1, (int)Math.Round(Height * sourceHeight));
            var x = (int)Math.Round(X * sourceWidth);
            var y = (int)Math.Round(Y * sourceHeight);

            // Округление вверх у правого края может вывести прямоугольник за
            // границы картинки — двигаем его внутрь, а не обрезаем сторону:
            // сохранить пропорции кадра важнее, чем его положение до пикселя.
            if (x + w > sourceWidth) x = sourceWidth - w;
            if (y + h > sourceHeight) y = sourceHeight - h;
            if (x < 0) { x = 0; w = Math.Min(w, sourceWidth); }
            if (y < 0) { y = 0; h = Math.Min(h, sourceHeight); }

            return new Avalonia.PixelRect(x, y, w, h);
        }

        /// <summary>
        /// Запись кадра для ссылки: четыре доли через запятую, инвариантная
        /// культура. Пятый знак — ниже трети пикселя на картинке в 512 точек,
        /// дальше запись только пухнет.
        /// </summary>
        public override string ToString() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.#####},{1:0.#####},{2:0.#####},{3:0.#####}",
                X, Y, Width, Height);

        /// <summary>
        /// Разбор записи кадра. Возвращает false на любой непонятной строке:
        /// ссылка могла прийти из руками правленого файла проекта, и терять
        /// из-за этого сам аватар нельзя — вызывающая сторона просто покажет
        /// картинку целиком.
        /// </summary>
        public static bool TryParse(string? text, out CharacterAvatarCrop crop)
        {
            crop = Full;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split(',');
            if (parts.Length != 4) return false;

            var values = new double[4];
            for (var i = 0; i < 4; i++)
            {
                if (!double.TryParse(
                        parts[i].Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out values[i]))
                    return false;

                if (double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                    return false;
            }

            crop = new CharacterAvatarCrop(values[0], values[1], values[2], values[3]);
            return true;
        }

        public bool Equals(CharacterAvatarCrop? other)
        {
            if (other is null) return false;
            return Math.Abs(X - other.X) <= Epsilon
                && Math.Abs(Y - other.Y) <= Epsilon
                && Math.Abs(Width - other.Width) <= Epsilon
                && Math.Abs(Height - other.Height) <= Epsilon;
        }

        public override bool Equals(object? obj) => Equals(obj as CharacterAvatarCrop);

        public override int GetHashCode() => HashCode.Combine(
            Math.Round(X, 5),
            Math.Round(Y, 5),
            Math.Round(Width, 5),
            Math.Round(Height, 5));

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
