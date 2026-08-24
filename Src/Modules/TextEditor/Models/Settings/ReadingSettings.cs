using System;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Settings
{
    /// <summary>
    /// Подача чтения — как книга разложена на экране.
    /// </summary>
    public enum ReadingFlow
    {
        /// <summary>Книжный разворот: две страницы рядом, перелистывание.</summary>
        Spread = 0,
        /// <summary>Один лист по центру, перелистывание.</summary>
        Single = 1,
        /// <summary>Сплошная лента: страниц нет, текст прокручивается колонкой.</summary>
        Column = 2
    }

    /// <summary>Пропорции листа в книжном чтении.</summary>
    public enum ReadingSheetFormat
    {
        /// <summary>Как у документа — те же пропорции, что при печати.</summary>
        Document = 0,
        /// <summary>Карманный: узкий и высокий, как у бумажной книги.</summary>
        Pocket = 1,
        /// <summary>Квадратный — альбомная подача.</summary>
        Square = 2,
        /// <summary>Широкий: под экран, минимум полей по бокам.</summary>
        Wide = 3
    }

    /// <summary>
    /// Настройки чтения. Влияют только на то, как документ показан в режиме чтения:
    /// ни в печать, ни в экспорт, ни в содержание рукописи они не попадают. Живут
    /// в сессии проекта — выбранный вид переживает перезапуск, но остаётся личным
    /// делом читателя.
    ///
    /// Оформление вынесено в <see cref="ReadingTheme"/>: у него есть имя, его можно
    /// создать своё, положить в документ или сделать общим для всех проектов.
    /// Здесь хранится копия выбранного вида — по ней и рисуется книга. Копия, а не
    /// ссылка: ползунки ленты правят её на ходу, и трогать при этом сам сохранённый
    /// вид неправильно — иначе одно движение яркости переписывало бы то, что человек
    /// когда-то настроил и назвал.
    /// </summary>
    public sealed class ReadingSettings
    {
        // ── Подача ────────────────────────────────────────────────────────

        /// <summary>Разворот, один лист или сплошная лента.</summary>
        public ReadingFlow Flow { get; set; } = ReadingFlow.Spread;

        /// <summary>Пропорции листа. К ленте не относится — там страниц нет.</summary>
        public ReadingSheetFormat Format { get; set; } = ReadingSheetFormat.Document;

        // ── Вид ───────────────────────────────────────────────────────────

        /// <summary>Опознаватель выбранного вида — чтобы список знал, что отмечать.</summary>
        public string ThemeId { get; set; } = ReadingTheme.CreamId;

        /// <summary>
        /// Рабочая копия вида: по ней рисуется книга. Ползунки ленты правят её,
        /// сохранённый вид при этом остаётся нетронутым.
        /// </summary>
        public ReadingTheme Active { get; set; } = ReadingTheme.FindBuiltIn(ReadingTheme.CreamId).Clone();

        /// <summary>Ставит вид в работу: копия его значений становится рабочей.</summary>
        public void ApplyTheme(ReadingTheme theme)
        {
            if (theme is null) return;
            ThemeId = theme.Id;
            Active = theme.Clone();
        }

        // ── Текст поверх вида ─────────────────────────────────────────────

        /// <summary>
        /// Ступень размера текста относительно документа. Своего кегля здесь нет
        /// намеренно: читатель может сделать буквы чуть крупнее или чуть мельче,
        /// но не назначить рукописи другой размер.
        ///
        /// В вид не входит: это не оформление книги, а поправка под конкретные
        /// глаза и конкретный экран.
        /// </summary>
        public int FontStep { get; set; }

        public const int MinFontStep = -3;
        public const int MaxFontStep = 6;

        /// <summary>Множитель кегля, соответствующий текущей ступени.</summary>
        [JsonIgnore]
        public double FontScale
        {
            get
            {
                int step = Math.Clamp(FontStep, MinFontStep, MaxFontStep);
                return Math.Clamp(1.0 + step * 0.06, 0.8, 1.4);
            }
        }

        // ── Показ ─────────────────────────────────────────────────────────

        /// <summary>
        /// Приближение книги: множитель к размеру листа на экране. Разбиение на
        /// страницы при этом не меняется — книга просто ближе или дальше от глаза.
        /// </summary>
        public double Zoom { get; set; } = 1.0;

        public const double MinZoom = 0.5;
        public const double MaxZoom = 3.0;

        /// <summary>
        /// Ужимать картинки и таблицы вместе с листом. Лист чтения меньше бумажного,
        /// и картинка в исходном размере вылезает за колонку.
        /// </summary>
        public bool ScaleContent { get; set; } = true;

        /// <summary>
        /// Показывать номера страниц. Своя нумерация документа имеет приоритет:
        /// если она есть в колонтитулах, чтение своих цифр не рисует.
        /// </summary>
        public bool ShowPageNumbers { get; set; } = true;

        // ── Интерфейс ─────────────────────────────────────────────────────

        /// <summary>Развёрнута ли лента чтения.</summary>
        public bool RibbonExpanded { get; set; } = true;

        /// <summary>Читать на весь экран, спрятав всё окно приложения.</summary>
        public bool Fullscreen { get; set; }

        // ── Производные ───────────────────────────────────────────────────

        /// <summary>Чтение страницами: разворот или одиночный лист.</summary>
        [JsonIgnore]
        public bool IsPaged => Flow != ReadingFlow.Column;

        /// <summary>Разворот из двух половин.</summary>
        [JsonIgnore]
        public bool IsSpread => Flow == ReadingFlow.Spread;

        /// <summary>Один лист по центру.</summary>
        [JsonIgnore]
        public bool IsSinglePage => Flow == ReadingFlow.Single;

        public ReadingSettings Clone() => new()
        {
            Flow = Flow,
            Format = Format,
            ThemeId = ThemeId,
            Active = Active?.Clone() ?? ReadingTheme.FindBuiltIn(ReadingTheme.CreamId).Clone(),
            FontStep = FontStep,
            Zoom = Zoom,
            ScaleContent = ScaleContent,
            ShowPageNumbers = ShowPageNumbers,
            RibbonExpanded = RibbonExpanded,
            Fullscreen = Fullscreen
        };
    }
}
