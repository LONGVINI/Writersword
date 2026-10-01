using System;

namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>
    /// «Мой эффект» — сохранённый вид букв под своим именем: настраиваемые эффекты
    /// (контур, тень, свечение, отражение), рамка знаков, эффекты из окна шрифта Word
    /// и знак ударения. Лежит в общих настройках модуля и доступен во всех проектах:
    /// собрал вид один раз — ставишь одним нажатием в меню «A».
    ///
    /// Набор ставится целиком: настраиваемые эффекты и рамка знаков у текста
    /// становятся ровно такими, как в наборе. Поля, помеченные «null — не трогать»,
    /// набор может и не задавать: тогда у текста остаётся своё. Так набор, сохранённый
    /// из окна «Эффекты текста», где знака ударения нет, не снимает знак, стоящий у
    /// текста.
    ///
    /// Цвет и гарнитура букв в набор не входят — это не эффект, а шрифт.
    /// Запись неизменяемая и сравнивается по значению.
    /// </summary>
    public sealed record TextEffectPreset
    {
        /// <summary>Постоянный ключ набора: по нему набор переименовывают, заменяют и удаляют.</summary>
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>Имя набора — подпись плитки в меню.</summary>
        public string Name { get; init; } = DefaultName;

        /// <summary>Настраиваемые эффекты; null — без них.</summary>
        public TextEffects? Effects { get; init; }

        /// <summary>Рамка вокруг знаков; null — без рамки.</summary>
        public CharBorderSettings? Border { get; init; }

        /// <summary>Контур из окна шрифта Word (w:outline); null — не трогать.</summary>
        public bool? IsOutline { get; init; }

        /// <summary>Тень из окна шрифта Word (w:shadow); null — не трогать.</summary>
        public bool? IsShadow { get; init; }

        /// <summary>Рельеф (w:emboss); null — не трогать.</summary>
        public bool? IsEmboss { get; init; }

        /// <summary>Гравировка (w:imprint); null — не трогать.</summary>
        public bool? IsImprint { get; init; }

        /// <summary>Знак ударения (w:em); null — не трогать.</summary>
        public EmphasisMark? EmphasisMark { get; init; }

        /// <summary>Имя, которое получает новый набор, пока его не назвали.</summary>
        public const string DefaultName = "Мой эффект";

        /// <summary>
        /// В наборе нет ни одного эффекта: ставить его — значит снимать эффекты.
        /// Такой набор не сохраняется.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsBlank =>
            TextEffects.Normalize(Effects) is null
            && Border?.Enabled != true
            && IsOutline != true
            && IsShadow != true
            && IsEmboss != true
            && IsImprint != true
            && (EmphasisMark ?? Inline.EmphasisMark.None) == Inline.EmphasisMark.None;
    }
}
