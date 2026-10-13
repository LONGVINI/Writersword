using System;
using System.Collections.Generic;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Значки программы — один набор на всё: анкеты, шаблоны, метки
    /// персонажей, значки полей.
    ///
    /// Значок хранится строкой одного из двух видов:
    ///   «magic»        — ключ встроенного значка (CharacterAnketaIcons.All);
    ///   «img:&lt;ссылка&gt;» — своя картинка из библиотеки значков проекта.
    ///
    /// Ссылка у своей картинки того же вида, что у аватаров и картинок меток:
    /// файл лежит в проекте и уезжает вместе с ним, а кадр обрезки живёт в
    /// самой ссылке. SVG красится цветом значка, растр идёт как есть.
    /// </summary>
    public static class CharacterIcons
    {
        public const string ImagePrefix = "img:";

        /// <summary>Подложка значка выключена: значок рисуется один, на фоне того, где стоит.</summary>
        public const string BackgroundNone = "none";

        /// <summary>Подложка залита цветом значка во всю силу, значок на ней белый.</summary>
        public const string BackgroundFill = "fill";

        /// <summary>Подложка как обычно — бледная, цвета значка, значок цветной.</summary>
        public static bool IsAutoBackground(string? background) => string.IsNullOrWhiteSpace(background);

        public static bool IsNoBackground(string? background) =>
            string.Equals(background, BackgroundNone, StringComparison.Ordinal);

        public static bool IsFillBackground(string? background) =>
            string.Equals(background, BackgroundFill, StringComparison.Ordinal);

        /// <summary>Подложка своего цвета: не «как обычно», не залитая и не выключенная.</summary>
        public static bool IsCustomBackground(string? background) =>
            !IsAutoBackground(background) && !IsNoBackground(background) && !IsFillBackground(background);

        /// <summary>Значок — своя картинка, а не встроенная фигура.</summary>
        public static bool IsImage(string? icon) =>
            icon != null && icon.StartsWith(ImagePrefix, StringComparison.Ordinal);

        /// <summary>Ссылка на картинку своего значка; null — значок встроенный.</summary>
        public static string? ImageRefOf(string? icon) =>
            IsImage(icon) ? icon!.Substring(ImagePrefix.Length) : null;

        /// <summary>Значок из ссылки на картинку.</summary>
        public static string FromImageRef(string imageRef) => ImagePrefix + imageRef;
    }

    /// <summary>
    /// Рамка вокруг подложки значка. Хранится строкой «w&lt;толщина&gt;:&lt;цвет&gt;»:
    /// толщина 1–9 — от волоска до широкой каймы, цвет — «auto» (цвета значка)
    /// или код цвета программы, градиент тоже. Пустая строка — рамки нет.
    /// Префикс толщины не путается с цветом: код цвета начинается с «#» или
    /// «grad|».
    /// </summary>
    public readonly record struct CharacterIconFrame(int Width, string Color)
    {
        public const string Auto = "auto";
        public const int MinWidth = 1;
        public const int MaxWidth = 9;
        public const int DefaultWidth = 3;

        /// <summary>
        /// Толщина линии — доля стороны значка: в списке и в превью рамка
        /// выглядит одинаково. От 1/50 стороны на 1 до примерно 1/9 на 9.
        /// </summary>
        public double Share => 0.02 + (Math.Clamp(Width, MinWidth, MaxWidth) - MinWidth) * 0.0115;

        /// <summary>Рамка цвета значка — меняется вместе с ним.</summary>
        public bool IsAuto => string.IsNullOrWhiteSpace(Color) ||
                              string.Equals(Color, Auto, StringComparison.Ordinal);

        /// <summary>Рамка из строки; null — рамки нет.</summary>
        public static CharacterIconFrame? Parse(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var width = MinWidth;
            var color = code;

            if (code.Length >= 3 && code[0] == 'w' && char.IsDigit(code[1]) && code[2] == ':')
            {
                width = Math.Clamp(code[1] - '0', MinWidth, MaxWidth);
                color = code.Substring(3);
            }

            return new CharacterIconFrame(width, string.IsNullOrWhiteSpace(color) ? Auto : color);
        }

        public static string Format(int width, string? color) =>
"w" + Math.Clamp(width, MinWidth, MaxWidth) + ":" + (string.IsNullOrWhiteSpace(color) ? Auto : color);

        public override string ToString() => Format(Width, Color);
    }

    /// <summary>
    /// Свой значок в библиотеке проекта: картинка, которую автор принёс сам —
    /// герб, эмблема, нарисованный знак. Библиотека одна на проект: значок,
    /// добавленный в анкете, виден и в шаблонах, и в метках персонажей.
    /// </summary>
    public class CharacterIconEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Имя для подсказки и поиска — по умолчанию имя файла.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Ссылка на картинку в проекте, вместе с кадром обрезки.</summary>
        public string ImageRef { get; set; } = string.Empty;

        public DateTime AddedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Значок в строковом виде — тот, что ставится анкете или шаблону.</summary>
        public string Icon => CharacterIcons.FromImageRef(ImageRef);

        public CharacterIconEntry Clone() => new()
        {
            Id = Id,
            Name = Name,
            ImageRef = ImageRef,
            AddedAt = AddedAt
        };
    }

    /// <summary>
    /// Как значок выглядит там, где стоит: Badge — значок анкеты, Tile —
    /// плитка шаблона. Различаются пропорциями — скруглением и размером
    /// фигуры; подложка у обоих задаётся одинаково.
    /// </summary>
    public enum CharacterIconLook
    {
        Badge,
        Tile
    }

    /// <summary>
    /// Что выбрали в окне значков: значок, его цвет, подложку и рамку. Цвета —
    /// коды цвета программы: одноцвет или градиент. Подложка — пусто (как
    /// обычно), «fill» (залитая), «none» или код цвета. Рамка — пусто (нет)
    /// или строка CharacterIconFrame.
    /// </summary>
    public sealed record CharacterIconChoice(string Icon, string Color, string Background, string Border = "",
        string BlockFrame = "");

    /// <summary>
    /// Что показать в окне значков. Color, Background и Border — то, чем
    /// значки нарисованы в наборе и превью; AllowColor — можно ли менять их здесь
    /// же. У метки цветов два и правятся они в её редакторе, поэтому там цвет
    /// только показывается. Look — где значок стоит: превью рисует его ровно
    /// так, как он встанет там. PreviewTitle и PreviewDescription — подписи
    /// рядом в превью: название и описание анкеты или шаблона. BlockFrame —
    /// цвет внешней рамки блока шаблона: пусто — цвета шаблона; правится
    /// только у шаблона (Look = Tile).
    /// </summary>
    public sealed record CharacterIconRequest(
        string Title,
        string Icon,
        string Color,
        bool AllowColor,
        string Background = "",
        CharacterIconLook Look = CharacterIconLook.Badge,
        string PreviewTitle = "",
        string PreviewDescription = "",
        string Border = "",
        string BlockFrame = "");

    /// <summary>
    /// Поиск значков. Подпись встроенного значка — его смысл в анкете
    /// («Бой»), а ищут обычно по тому, что нарисовано («щит»), поэтому у
    /// каждого встроенного есть ещё слова — что на нём и к чему он.
    /// </summary>
    internal static class CharacterIconSearch
    {
        private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
        {
            ["book"] = "книга записи",
            ["person"] = "человек персонаж фигура",
            ["group"] = "группа люди народ толпа",
            ["mood"] = "лицо улыбка характер настроение",
            ["heart"] = "сердце любовь чувства",
            ["idea"] = "лампа лампочка идея ум",
            ["palette"] = "палитра краски внешность",
            ["strength"] = "гантель штанга сила спорт",
            ["shield"] = "щит бой защита",
            ["magic"] = "магия искры звёзды волшебство",
            ["fire"] = "огонь пламя стихия",
            ["bolt"] = "молния энергия ток",
            ["search"] = "лупа поиск расследование",
            ["skull"] = "череп смерть мрак",
            ["flight"] = "самолёт техника полёт",
            ["work"] = "портфель работа профессия",
            ["home"] = "дом жильё",
            ["globe"] = "глобус мир планета",
            ["history"] = "часы прошлое история",
            ["speech"] = "облако речь диалог разговор",
            ["music"] = "нота музыка искусство",
            ["crown"] = "корона власть король",
            ["eye"] = "глаз тайна взгляд",
            ["moon"] = "луна месяц ночь",
            ["star"] = "звезда особое",
            ["lock"] = "замок секрет",
            ["health"] = "крест здоровье медицина аптечка",
            ["school"] = "шапка учёба школа выпускник",
            ["money"] = "деньги доллар богатство",
            ["puzzle"] = "пазл навык умение",
            ["flag"] = "флаг цель",
            ["nature"] = "дерево природа лес",
            ["drop"] = "капля вода кровь",
            ["sun"] = "солнце свет день",
            ["law"] = "молоток закон суд",
            ["key"] = "ключ",
            ["science"] = "колба наука химия",
            ["food"] = "еда вилка нож",
            ["map"] = "карта места путешествие",
            ["time"] = "песочные часы время",
            ["brush"] = "кисть творчество рисование",
            ["trophy"] = "кубок награда достижение",
            ["camera"] = "фотоаппарат камера облик",
            ["check"] = "галочка итог готово",
            ["dot"] = "точка круг",
            ["cross"] = "крестик отмена"
        };

        public static bool Matches(string query, string label, string key)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;

            var words = key.Length > 0 && Words.TryGetValue(key, out var extra) ? extra : string.Empty;

            foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!label.Contains(word, StringComparison.CurrentCultureIgnoreCase) &&
                    !words.Contains(word, StringComparison.CurrentCultureIgnoreCase) &&
                    !key.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return false;

            return true;
        }
    }
}
