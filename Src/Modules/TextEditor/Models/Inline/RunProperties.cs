using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Inline
{
    /// <summary>
    /// Набор всех свойств форматирования одного фрагмента текста (Run).
    /// Хранится в JSON внутри ZIP-проекта.
    /// </summary>
    public sealed class RunProperties
    {
        /// <summary>
        /// Имя символьного стиля, лежащего на этом фрагменте. Null — фрагмент не носит
        /// стиля и берёт всё от абзаца.
        ///
        /// Хранится именно имя, а не свойства: стиль правят один раз на весь документ,
        /// и копия его свойств в каждом фрагменте устарела бы в ту же минуту.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StyleName { get; set; }

        /// <summary>Название шрифта. Null означает "унаследовать от стиля абзаца".</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FontFamily { get; set; }

        /// <summary>Размер шрифта в пунктах. Null — унаследовать.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? FontSize { get; set; }

        /// <summary>
        /// Жирный. Null — унаследовать от стиля.
        ///
        /// Состояний три, а не два. У фрагмента, которому только покрасили цвет, свойства
        /// уже есть, и обычный bool давал ему «не жирный» — слово в заголовке теряло
        /// жирность. False теперь значит «жирность снята руками», null — «как у стиля».
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsBold { get; set; }

        /// <summary>Курсив. Null — унаследовать от стиля (см. IsBold).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsItalic { get; set; }

        /// <summary>
        /// Подчёркивание любого вида. Производное от <see cref="UnderlineStyle"/>:
        /// включение ставит одинарную линию, если вида ещё нет, выключение снимает вид.
        ///
        /// В файл пишется по-прежнему и раньше вида: документы, сохранённые до появления
        /// видов, несут только этот признак и открываются с одинарной линией, а вид,
        /// идущий в файле следом, уточняет его.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsUnderline
        {
            get => UnderlineStyle != UnderlineStyle.None;
            set
            {
                if (!value)
                    UnderlineStyle = UnderlineStyle.None;
                else if (UnderlineStyle == UnderlineStyle.None)
                    UnderlineStyle = UnderlineStyle.Single;
            }
        }

        /// <summary>Вид подчёркивания. None — подчёркивания нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public UnderlineStyle UnderlineStyle { get; set; }

        /// <summary>
        /// Цвет линии подчёркивания в формате #RRGGBB. Null — «авто»: линия того же
        /// цвета, что и буквы.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UnderlineColor { get; set; }

        /// <summary>Зачёркивание.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsStrikethrough { get; set; }

        /// <summary>
        /// Двойное зачёркивание (w:dstrike). Как в Word, с одинарным не сочетается:
        /// включённое, оно рисуется вместо одинарного.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsDoubleStrikethrough { get; set; }

        /// <summary>Надстрочный (x²).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsSuperscript { get; set; }

        /// <summary>Подстрочный (H₂O).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsSubscript { get; set; }

        /// <summary>Все символы в верхнем регистре при отображении.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsAllCaps { get; set; }

        /// <summary>Малые заглавные (Small Caps).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsSmallCaps { get; set; }

        /// <summary>
        /// Межбуквенный интервал в пунктах: прибавка к ширине каждого знака, как
        /// «Интервал — разреженный/уплотнённый» в Word. Отрицательное значение сжимает
        /// текст. Null — унаследовать от стиля (у стиля null — обычный интервал).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? CharacterSpacing { get; set; }

        /// <summary>
        /// Масштаб знаков по ширине в процентах, как «Масштаб» во вкладке «Интервал»
        /// шрифта Word (w:w): 200 — вдвое шире, 50 — вдвое уже. Кегль не меняется.
        /// Null — обычная ширина (100 %).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? CharacterScale { get; set; }

        /// <summary>
        /// Смещение текста вверх (плюс) или вниз (минус) от базовой линии в пунктах,
        /// как «Смещение» во вкладке «Интервал» шрифта Word (w:position). Кегль при этом
        /// не меняется — в отличие от верхнего и нижнего индекса. Null — без смещения.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? BaselineOffset { get; set; }

        /// <summary>
        /// Скрытый текст (w:vanish): не печатается и не показывается, пока выключены
        /// непечатаемые знаки. В тексте документа остаётся.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsHidden { get; set; }

        /// <summary>Контур: буквы полые (w:outline).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsOutline { get; set; }

        /// <summary>Тень под буквами (w:shadow).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsShadow { get; set; }

        /// <summary>Рельеф: буквы выпуклые (w:emboss).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsEmboss { get; set; }

        /// <summary>Гравировка: буквы вдавленные (w:imprint).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsImprint { get; set; }

        /// <summary>Знак ударения над или под буквами (w:em).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public EmphasisMark EmphasisMark { get; set; }

        /// <summary>
        /// Цвет рамки вокруг знаков (w:bdr) в формате #RRGGBB. Null — цвет букв.
        /// Рамка есть, только когда задана её толщина (<see cref="CharBorderWidthPt"/>).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CharBorderColor { get; set; }

        /// <summary>Толщина рамки вокруг знаков в пунктах (w:bdr). Null — рамки нет.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? CharBorderWidthPt { get; set; }

        /// <summary>Вид линии рамки вокруг знаков (w:bdr w:val).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public CharBorderStyle CharBorderStyle { get; set; }

        /// <summary>
        /// Настраиваемые эффекты букв: контур, тень, свечение, отражение (Word 2010+,
        /// w14). Null — эффектов нет. Запись неизменяемая: копия свойств может делить
        /// её с оригиналом.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextEffects? Effects { get; set; }

        /// <summary>Цвет текста в формате #RRGGBB или #AARRGGBB. Null — унаследовать.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TextColor { get; set; }

        /// <summary>Цвет маркера (фон под текстом). Null — нет маркера.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HighlightColor { get; set; }

        /// <summary>
        /// Код языка фрагмента (ru, uk, en и т.д.).
        /// Используется для выбора словаря орфографии.
        /// Null — унаследовать от настроек документа.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Language { get; set; }

        /// <summary>
        /// Фрагмент вставлен под рецензированием (w:ins, w:moveTo): кто и когда его
        /// вставил. Null — не вставка.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RevisionInfo? Inserted { get; set; }

        /// <summary>
        /// Фрагмент удалён под рецензированием (w:del, w:moveFrom): кто и когда его
        /// удалил. Удалённый текст остаётся в абзаце, пока правку не примут: его
        /// показывают зачёркнутым либо прячут — смотря по виду показа исправлений.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RevisionInfo? Deleted { get; set; }

        /// <summary>
        /// Оформление фрагмента сменено под рецензированием (w:rPrChange): прежнее
        /// оформление и кто его сменил.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RunFormatChange? FormatChange { get; set; }

        /// <summary>У фрагмента есть хоть одна правка рецензирования.</summary>
        [JsonIgnore]
        public bool HasRevision => Inserted is not null || Deleted is not null || FormatChange is not null;

        /// <summary>
        /// Создаёт копию свойств. Отметки правок копируются своими: общая запись у
        /// копии и оригинала менялась бы сразу у обоих.
        /// </summary>
        public RunProperties Clone()
        {
            var copy = (RunProperties)MemberwiseClone();
            copy.Inserted = Inserted?.Clone();
            copy.Deleted = Deleted?.Clone();
            copy.FormatChange = FormatChange?.Clone();
            return copy;
        }

        /// <summary>Копия одного оформления — без отметок правок рецензирования.</summary>
        public RunProperties WithoutRevisions()
        {
            var copy = (RunProperties)MemberwiseClone();
            copy.Inserted = null;
            copy.Deleted = null;
            copy.FormatChange = null;
            return copy;
        }

        /// <summary>
        /// Одинаковое оформление: все поля, кроме отметок правок. Null равен
        /// оформлению по умолчанию.
        /// </summary>
        public static bool SameFormatting(RunProperties? a, RunProperties? b)
        {
            if (ReferenceEquals(a, b)) return true;

            bool aDefault = a is null || a.IsDefaultFormatting();
            bool bDefault = b is null || b.IsDefaultFormatting();
            if (aDefault || bDefault) return aDefault && bDefault;

            return a!.StyleName == b!.StyleName
                && a.FontFamily == b.FontFamily
                && a.FontSize == b.FontSize
                && a.IsBold == b.IsBold
                && a.IsItalic == b.IsItalic
                && a.UnderlineStyle == b.UnderlineStyle
                && a.UnderlineColor == b.UnderlineColor
                && a.IsStrikethrough == b.IsStrikethrough
                && a.IsDoubleStrikethrough == b.IsDoubleStrikethrough
                && a.IsSuperscript == b.IsSuperscript
                && a.IsSubscript == b.IsSubscript
                && a.IsAllCaps == b.IsAllCaps
                && a.IsSmallCaps == b.IsSmallCaps
                && a.CharacterSpacing == b.CharacterSpacing
                && a.CharacterScale == b.CharacterScale
                && a.BaselineOffset == b.BaselineOffset
                && a.IsHidden == b.IsHidden
                && a.IsOutline == b.IsOutline
                && a.IsShadow == b.IsShadow
                && a.IsEmboss == b.IsEmboss
                && a.IsImprint == b.IsImprint
                && a.EmphasisMark == b.EmphasisMark
                && a.CharBorderColor == b.CharBorderColor
                && a.CharBorderWidthPt == b.CharBorderWidthPt
                && a.CharBorderStyle == b.CharBorderStyle
                && Equals(a.Effects, b.Effects)
                && a.TextColor == b.TextColor
                && a.HighlightColor == b.HighlightColor
                && a.Language == b.Language;
        }

        /// <summary>
        /// Свойства после «очистки формата»: оформление снято, отметки правок
        /// рецензирования остаются — иначе очистка формата незаметно приняла бы или
        /// потеряла правки. Null — отметок не было, и свойств не остаётся.
        /// </summary>
        public static RunProperties? ClearedKeepingRevisions(RunProperties? props)
        {
            if (props is null || !props.HasRevision) return null;

            return new RunProperties
            {
                Inserted = props.Inserted?.Clone(),
                Deleted = props.Deleted?.Clone(),
                FormatChange = props.FormatChange?.Clone()
            };
        }

        /// <summary>
        /// Снимает с этих свойств всё оформление, оставляя отметки правок. Поля
        /// обходятся все, какие есть, — новое поле оформления сбросится и без правки
        /// этого метода.
        /// </summary>
        public void ResetFormatting()
        {
            var blank = new RunProperties();

            foreach (var property in typeof(RunProperties).GetProperties(
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite) continue;
                if (property.GetIndexParameters().Length > 0) continue;
                if (property.Name is nameof(Inserted) or nameof(Deleted) or nameof(FormatChange)) continue;

                property.SetValue(this, property.GetValue(blank));
            }
        }

        /// <summary>Одинаковые отметки правок рецензирования.</summary>
        public static bool SameRevisions(RunProperties? a, RunProperties? b)
        {
            return RevisionInfo.Same(a?.Inserted, b?.Inserted)
                && RevisionInfo.Same(a?.Deleted, b?.Deleted)
                && RunFormatChange.Same(a?.FormatChange, b?.FormatChange);
        }

        /// <summary>
        /// Возвращает true если все поля имеют значения по умолчанию
        /// (нет явного форматирования).
        /// </summary>
        public bool IsDefault()
        {
            return IsDefaultFormatting()
                && Inserted is null
                && Deleted is null
                && FormatChange is null;
        }

        /// <summary>
        /// Все поля оформления — по умолчанию; отметки правок рецензирования не
        /// учитываются.
        /// </summary>
        public bool IsDefaultFormatting()
        {
            return StyleName is null
                && FontFamily is null
                && FontSize is null
                && IsBold is null
                && IsItalic is null
                && !IsUnderline
                && UnderlineColor is null
                && !IsStrikethrough
                && !IsDoubleStrikethrough
                && !IsSuperscript
                && !IsSubscript
                && !IsAllCaps
                && !IsSmallCaps
                && CharacterSpacing is null
                && CharacterScale is null
                && BaselineOffset is null
                && !IsHidden
                && !IsOutline
                && !IsShadow
                && !IsEmboss
                && !IsImprint
                && EmphasisMark == EmphasisMark.None
                && CharBorderColor is null
                && CharBorderWidthPt is null
                && CharBorderStyle == CharBorderStyle.Single
                && Effects is null
                && TextColor is null
                && HighlightColor is null
                && Language is null;
        }
    }
}
