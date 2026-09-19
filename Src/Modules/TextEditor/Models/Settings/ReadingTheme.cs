using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Settings
{
    /// <summary>Как картинка поля ложится на экран.</summary>
    public enum ReadingBackdropFit
    {
        /// <summary>Закрыть поле целиком, лишнее обрезать. Пропорции сохраняются.</summary>
        Cover = 0,
        /// <summary>Уместить целиком, по краям останется цвет. Пропорции сохраняются.</summary>
        Contain = 1,
        /// <summary>Растянуть на всё поле, не считаясь с пропорциями.</summary>
        Stretch = 2,
        /// <summary>Замостить в исходном размере.</summary>
        Tile = 3
    }

    /// <summary>Как вид разбирается с набором картинок.</summary>
    public enum ReadingImageFlow
    {
        /// <summary>Всегда первая. Набор при этом никуда не девается.</summary>
        Single = 0,

        /// <summary>Каждый следующий лист берёт следующую картинку, по кругу.</summary>
        Sequence = 1,

        /// <summary>Лист берёт картинку из набора по жребию — но всегда одну и ту же.</summary>
        Shuffle = 2
    }

    /// <summary>
    /// Именованный вид чтения: как выглядит книга на экране. Цвет листа и текста,
    /// картинка бумаги, шрифт, свет — всё вместе и под своим именем.
    ///
    /// Вид живёт в двух местах сразу, и это разные вещи:
    ///   в документе — уезжает вместе с рукописью, и у того, кто её откроет,
    ///                 книга будет выглядеть так же;
    ///   везде       — лежит в настройках программы и доступен во всех проектах.
    /// Одному и тому же виду можно назначить обе области: тогда он и уедет с
    /// документом, и останется под рукой в других проектах.
    ///
    /// На печать, экспорт и содержание рукописи вид не влияет никогда.
    /// </summary>
    public sealed class ReadingTheme
    {
        /// <summary>
        /// Опознаватель вида. У встроенных — устойчивая строка, у своих — Guid.
        /// По нему настройки чтения помнят, какой вид выбран.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Имя, которое видно в списке. Своё у каждого вида.</summary>
        public string Name { get; set; } = "Без имени";

        /// <summary>Встроенный вид: его нельзя ни переименовать, ни удалить.</summary>
        public bool IsBuiltIn { get; set; }

        // ── Бумага ────────────────────────────────────────────────────────

        /// <summary>Цвет листа (HEX).</summary>
        public string SheetColor { get; set; } = "#FBF6EC";

        /// <summary>Цвет текста, у которого нет своего (HEX).</summary>
        public string InkColor { get; set; } = "#2E2A24";

        // ── Только для правки ─────────────────────────────────────────────
        // Всё, что ниже, видно за письмом и в чтении не значит ничего: там не
        // правят, линейки нет, и позиции табуляции показывать нечему. Пусто в любом
        // из этих полей — цвет выводится из бумаги и чернил, как и раньше.
        //
        // Живёт это в виде, а не в общих настройках, потому что нужно ровно тогда,
        // когда меняется бумага: подобранное под ночной лист обязано уйти вместе с
        // ним, стоит вернуться на белый.

        /// <summary>
        /// Цвет каретки при правке (HEX). Пусто — каретка берёт цвет текста, который
        /// пишет, и меняется вместе с ним.
        /// </summary>
        public string? CaretColor { get; set; }

        /// <summary>Полоса линейки (HEX). Пусто — цвет бумаги вида.</summary>
        public string? RulerSheetColor { get; set; }

        /// <summary>Деления и цифры линейки (HEX). Пусто — цвет чернил вида.</summary>
        public string? RulerInkColor { get; set; }

        /// <summary>Зона за краем листа на линейке (HEX). Пусто — цвет поля вида.</summary>
        public string? RulerFieldColor { get; set; }

        /// <summary>
        /// Позиции табуляции: засечки на линейке и квадрат-переключатель в её углу
        /// (HEX). Пусто — прежний бирюзовый.
        /// </summary>
        public string? TabMarkColor { get; set; }

        /// <summary>
        /// Картинки бумаги. Пусто — лист заливается цветом.
        ///
        /// Набор, а не одна: листы в книге разные, и читатель вправе собрать их
        /// столько, сколько хочет. Что с набором делать — решает <see cref="ImageFlow"/>.
        /// </summary>
        public List<string> ImagePaths { get; set; } = new();

        /// <summary>Что вид делает с набором картинок бумаги.</summary>
        public ReadingImageFlow ImageFlow { get; set; } = ReadingImageFlow.Sequence;

        /// <summary>
        /// Прежнее поле одной картинки бумаги. Читается из старых файлов и вливается
        /// в набор; наружу не отдаётся — у вида теперь набор, и спрашивать нужно его.
        /// </summary>
        [JsonPropertyName("ImagePath")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyImagePath
        {
            get => null;
            set => AddImage(ImagePaths, value);
        }

        /// <summary>Плотность картинки поверх цвета листа: 0 — не видна, 1 — целиком.</summary>
        public double ImageOpacity { get; set; } = 1.0;

        /// <summary>Замостить картинку вместо растягивания на весь лист.</summary>
        public bool ImageTile { get; set; }

        // ── Поле вокруг книги ─────────────────────────────────────────────
        // Поле занимает больше места на экране, чем сама книга, и от него зависит,
        // устают ли от чтения глаза. Поэтому оно часть вида, а не общая настройка.

        /// <summary>
        /// Заливка поля. Пусто — выводится из бумаги: под светлой книгой поле темнее,
        /// под тёмной чуть светлее.
        ///
        /// Значение здесь то же самое, что и у любого другого цвета в программе: либо
        /// HEX, либо код градиента. Своих видов заливки у поля нет намеренно —
        /// градиенты уже есть у цветов, и поле просто принимает их как есть.
        /// </summary>
        public string? BackdropColor { get; set; }

        /// <summary>
        /// Картинки поля. Пусто — поле заливается цветом.
        ///
        /// Переключателя «использовать картинку» здесь нет намеренно: картинка либо
        /// выбрана, либо нет, и второе состояние про то же самое было лишним. Заливка
        /// при этом остаётся нужной — она видна там, где картинка не закрывает поле
        /// целиком, и это выбор человека, а не недосмотр.
        ///
        /// Набор поле пока показывает по первой картинке: разным картинкам на одном
        /// экране взяться неоткуда. Хранится он целиком — чтобы не собирать заново,
        /// когда появится, к чему их привязать.
        /// </summary>
        public List<string> BackdropImagePaths { get; set; } = new();

        /// <summary>Что вид делает с набором картинок поля.</summary>
        public ReadingImageFlow BackdropImageFlow { get; set; } = ReadingImageFlow.Single;

        /// <summary>Прежнее поле одной картинки поля. Только для чтения старых файлов.</summary>
        [JsonPropertyName("BackdropImagePath")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyBackdropImagePath
        {
            get => null;
            set => AddImage(BackdropImagePaths, value);
        }

        /// <summary>Как картинка поля ложится на экран.</summary>
        public ReadingBackdropFit BackdropImageFit { get; set; } = ReadingBackdropFit.Cover;

        /// <summary>Плотность картинки поля: 0 — не видна, 1 — целиком.</summary>
        public double BackdropImageOpacity { get; set; } = 1.0;

        // Шрифта у вида нет намеренно.
        //
        // Вид — один и тот же за письмом и за чтением, и держит он ровно то, что не
        // доедет до печати: имя, цвета, бумагу, поле. Гарнитура в этот список не
        // входит: за письмом рукопись обязана выглядеть так, как её напечатают, а за
        // чтением начертание — такая же поправка под конкретные глаза, как и ступень
        // размера. Обе живут в настройках чтения (ReadingSettings), обе не уезжают с
        // видом и не меняются молча при его переключении.

        // ── Свет ──────────────────────────────────────────────────────────

        /// <summary>Яркость листа: 1 — как есть, меньше — приглушённее.</summary>
        public double Brightness { get; set; } = 1.0;

        /// <summary>Контрастность: насколько текст расходится с бумагой.</summary>
        public double Contrast { get; set; } = 1.0;

        /// <summary>Тёплота: доля янтарной вуали поверх страницы.</summary>
        public double Warmth { get; set; }

        // ── Область хранения ──────────────────────────────────────────────

        /// <summary>
        /// Вид лежит в документе и уедет вместе с ним. Поле служебное: где вид
        /// хранится, определяет не оно, а то, в каком списке он оказался. Здесь
        /// оно нужно окну настройки, чтобы показать состояние переключателя.
        /// </summary>
        [JsonIgnore]
        public bool InDocument { get; set; }

        /// <summary>Вид лежит в настройках программы и доступен во всех проектах.</summary>
        [JsonIgnore]
        public bool IsGlobal { get; set; }

        /// <summary>
        /// Вид убран из списков выбора. Сам он никуда не девается и правится в окне
        /// видов как обычно — его просто не предлагают: встроенных шесть, а нужны из
        /// них обычно один-два, и остальные только удлиняют список.
        ///
        /// Служебное, как и области: где вид спрятан, помнит не он сам, а настройки
        /// программы — иначе спрятанный встроенный вид негде было бы записать.
        /// </summary>
        [JsonIgnore]
        public bool IsHidden { get; set; }

        /// <summary>Тёмный ли лист. По нему решается вид служебных мелочей.</summary>
        [JsonIgnore]
        public bool IsDark => IsDarkHex(SheetColor);

        // ── Наборы картинок ───────────────────────────────────────────────

        /// <summary>Есть ли у вида хоть одна картинка бумаги.</summary>
        [JsonIgnore]
        public bool HasPaperImage => ImagePaths.Count > 0;

        /// <summary>Есть ли у вида хоть одна картинка поля.</summary>
        [JsonIgnore]
        public bool HasBackdropImage => BackdropImagePaths.Count > 0;

        /// <summary>
        /// Картинка бумаги для листа с этим номером.
        ///
        /// Номер листа, а не порядковый номер вызова: один и тот же лист обязан
        /// выглядеть одинаково на каждом кадре, при возврате к нему и в снимке для
        /// переворота. Отсюда и жребий без генератора случайных чисел — перемешанный
        /// номер листа: он постоянен, но соседние листы не идут подряд по набору.
        /// </summary>
        public string? PaperImageFor(int sheetIndex) => Pick(ImagePaths, ImageFlow, sheetIndex);

        /// <summary>Картинка поля. Экран один, поэтому и картинка одна.</summary>
        public string? BackdropImageFor(int index = 0)
            => Pick(BackdropImagePaths, BackdropImageFlow, index);

        /// <summary>
        /// Прогоняет все картинки вида через преобразователь адресов: перенос в архив
        /// проекта, перенос в данные программы, отсев пропавших. Одно место на оба
        /// набора — иначе очередной перенос однажды забудет половину.
        /// </summary>
        public void MapImageReferences(Func<string?, string?> map)
        {
            if (map is null) return;
            Remap(ImagePaths, map);
            Remap(BackdropImagePaths, map);
        }

        /// <summary>Все картинки вида — бумага и поле вместе.</summary>
        [JsonIgnore]
        public IEnumerable<string> AllImages
        {
            get
            {
                foreach (var one in ImagePaths) yield return one;
                foreach (var one in BackdropImagePaths) yield return one;
            }
        }

        private static void AddImage(List<string> set, string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return;
            if (set.Contains(reference!)) return;
            set.Add(reference!);
        }

        private static void Remap(List<string> set, Func<string?, string?> map)
        {
            for (int i = set.Count - 1; i >= 0; i--)
            {
                string? mapped = map(set[i]);
                if (string.IsNullOrWhiteSpace(mapped)) set.RemoveAt(i);
                else set[i] = mapped!;
            }
        }

        private static string? Pick(List<string> set, ReadingImageFlow flow, int index)
        {
            if (set.Count == 0) return null;
            if (set.Count == 1 || flow == ReadingImageFlow.Single) return set[0];

            int i = index < 0 ? 0 : index;
            if (flow == ReadingImageFlow.Sequence) return set[i % set.Count];

            return set[(int)(Scramble(i) % (uint)set.Count)];
        }

        /// <summary>Перемешивает номер листа. Одинаковый ответ на одинаковый номер.</summary>
        private static uint Scramble(int value)
        {
            unchecked
            {
                uint x = (uint)value * 2654435761u;
                x ^= x >> 15;
                x *= 2246822519u;
                x ^= x >> 13;
                return x;
            }
        }

        public ReadingTheme Clone() => new()
        {
            Id = Id,
            Name = Name,
            IsBuiltIn = IsBuiltIn,
            SheetColor = SheetColor,
            InkColor = InkColor,
            CaretColor = CaretColor,
            RulerSheetColor = RulerSheetColor,
            RulerInkColor = RulerInkColor,
            RulerFieldColor = RulerFieldColor,
            TabMarkColor = TabMarkColor,
            ImagePaths = new List<string>(ImagePaths),
            ImageFlow = ImageFlow,
            ImageOpacity = ImageOpacity,
            ImageTile = ImageTile,
            BackdropColor = BackdropColor,
            BackdropImagePaths = new List<string>(BackdropImagePaths),
            BackdropImageFlow = BackdropImageFlow,
            BackdropImageFit = BackdropImageFit,
            BackdropImageOpacity = BackdropImageOpacity,
            Brightness = Brightness,
            Contrast = Contrast,
            Warmth = Warmth,
            InDocument = InDocument,
            IsGlobal = IsGlobal,
            IsHidden = IsHidden
        };

        /// <summary>
        /// Выглядят ли два вида одинаково. Сравнивается всё, что видно на экране, и
        /// ничего сверх того: имя, опознаватель и область хранения к внешности вида
        /// отношения не имеют.
        ///
        /// По этому и решается, показывать ли в списке имя вида или «Кастомное»:
        /// рабочая копия правится лентой на ходу, и стоит ей разойтись с сохранённым
        /// видом — на экране уже не он.
        /// </summary>
        public static bool SameLook(ReadingTheme? a, ReadingTheme? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;

            const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

            static bool Same(string? x, string? y, StringComparison how)
                => string.Equals(x ?? string.Empty, y ?? string.Empty, how);

            static bool Near(double x, double y) => Math.Abs(x - y) < 0.0005;

            static bool SameSet(List<string> x, List<string> y)
            {
                if (x.Count != y.Count) return false;
                for (int i = 0; i < x.Count; i++)
                    if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
                return true;
            }

            return Same(a.SheetColor, b.SheetColor, Ci)
                && Same(a.InkColor, b.InkColor, Ci)
                && Same(a.CaretColor, b.CaretColor, Ci)
                && Same(a.RulerSheetColor, b.RulerSheetColor, Ci)
                && Same(a.RulerInkColor, b.RulerInkColor, Ci)
                && Same(a.RulerFieldColor, b.RulerFieldColor, Ci)
                && Same(a.TabMarkColor, b.TabMarkColor, Ci)
                && SameSet(a.ImagePaths, b.ImagePaths)
                && a.ImageFlow == b.ImageFlow
                && Near(a.ImageOpacity, b.ImageOpacity)
                && a.ImageTile == b.ImageTile
                && Same(a.BackdropColor, b.BackdropColor, Ci)
                && SameSet(a.BackdropImagePaths, b.BackdropImagePaths)
                && a.BackdropImageFlow == b.BackdropImageFlow
                && a.BackdropImageFit == b.BackdropImageFit
                && Near(a.BackdropImageOpacity, b.BackdropImageOpacity)
                && Near(a.Brightness, b.Brightness)
                && Near(a.Contrast, b.Contrast)
                && Near(a.Warmth, b.Warmth);
        }

        /// <summary>Копия под новым именем и с новым опознавателем.</summary>
        public ReadingTheme CopyAs(string name)
        {
            var copy = Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = name;
            copy.IsBuiltIn = false;
            return copy;
        }

        /// <summary>
        /// Цвет поля вокруг листа в виде HEX — тот же, каким его заливает канвас.
        /// Нужен всему, что стоит вплотную к листу и обязано выглядеть его частью:
        /// в первую очередь линейкам.
        ///
        /// Задана заливка — берётся она; задан градиент — из него берётся первый
        /// цвет: сплошной нужен там, где градиент рисовать нечем. Не задано ничего —
        /// поле выводится из бумаги: под светлой книгой темнее её, под почти чёрной
        /// чуть светлее. Числа те же, что в DrawCanvasBackdrop, и менять их нужно
        /// вместе — иначе линейка разойдётся с полем на пару тонов.
        /// </summary>
        public static string FieldColorHex(ReadingTheme? theme)
        {
            if (theme is null) return "#E8E8E8";

            string? own = theme.BackdropColor;
            if (!string.IsNullOrWhiteSpace(own))
            {
                string first = FirstHexOf(own!);
                if (!string.IsNullOrEmpty(first)) return first;
            }

            if (!TryParseRgb(theme.SheetColor, out int r, out int g, out int b))
                return "#E8E8E8";

            double luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
            bool lighten = luma < 0.14;

            double target = lighten ? 255.0 : 0.0;
            double amount = lighten ? 0.10 : 0.16;

            int nr = ShiftChannel(r, target, amount);
            int ng = ShiftChannel(g, target, amount);
            int nb = ShiftChannel(b, target, amount);

            return $"#{nr:X2}{ng:X2}{nb:X2}";
        }

        private static int ShiftChannel(int v, double target, double amount)
            => (int)Math.Clamp(v + (target - v) * amount, 0.0, 255.0);

        /// <summary>Первый HEX-цвет, встреченный в строке. Пусто — цвета в ней нет.</summary>
        private static string FirstHexOf(string value)
        {
            int hash = value.IndexOf('#');
            if (hash < 0) return string.Empty;

            int end = hash + 1;
            while (end < value.Length && Uri.IsHexDigit(value[end])) end++;

            int len = end - hash - 1;
            if (len != 6 && len != 8) return string.Empty;

            return value.Substring(hash, len + 1);
        }

        /// <summary>Разбирает HEX в каналы. Альфа, если она есть, отбрасывается.</summary>
        private static bool TryParseRgb(string? hex, out int r, out int g, out int b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            string s = hex.TrimStart('#');
            if (s.Length == 8) s = s.Substring(2);
            if (s.Length != 6) return false;

            const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.HexNumber;
            return int.TryParse(s.Substring(0, 2), Hex, null, out r)
                && int.TryParse(s.Substring(2, 2), Hex, null, out g)
                && int.TryParse(s.Substring(4, 2), Hex, null, out b);
        }

        /// <summary>Тёмный ли цвет по HEX. Порог по воспринимаемой светлоте.</summary>
        public static bool IsDarkHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.TrimStart('#');
            if (s.Length == 8) s = s.Substring(2);
            if (s.Length != 6) return false;

            const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.HexNumber;
            if (!int.TryParse(s.Substring(0, 2), Hex, null, out int r)) return false;
            if (!int.TryParse(s.Substring(2, 2), Hex, null, out int g)) return false;
            if (!int.TryParse(s.Substring(4, 2), Hex, null, out int b)) return false;

            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 < 0.45;
        }

        // ── Встроенные виды ───────────────────────────────────────────────

        public const string WhiteId = "builtin.white";
        public const string CreamId = "builtin.cream";
        public const string SepiaId = "builtin.sepia";
        public const string MistId = "builtin.mist";
        public const string NightId = "builtin.night";
        public const string InkId = "builtin.ink";

        /// <summary>Виды, которые есть всегда и у всех.</summary>
        public static IReadOnlyList<ReadingTheme> BuiltIn { get; } = new[]
        {
            Make(WhiteId, "Белая",     "#FFFFFF", "#1A1A1A"),
            Make(CreamId, "Кремовая",  "#FBF6EC", "#2E2A24"),
            Make(SepiaId, "Сепия",     "#F1E7D3", "#4A3B2A"),
            Make(MistId,  "Пасмурная", "#DEE2E6", "#23282C"),
            Make(NightId, "Ночная",    "#292C31", "#DCD8D0"),
            Make(InkId,   "Чёрная",    "#141619", "#E4E1DA")
        };

        private static ReadingTheme Make(string id, string name, string sheet, string ink) => new()
        {
            Id = id,
            Name = name,
            IsBuiltIn = true,
            SheetColor = sheet,
            InkColor = ink
        };

        /// <summary>Встроенный вид по опознавателю. Не нашёлся — кремовая.</summary>
        public static ReadingTheme FindBuiltIn(string? id)
        {
            foreach (var t in BuiltIn)
                if (string.Equals(t.Id, id, StringComparison.Ordinal)) return t;

            foreach (var t in BuiltIn)
                if (t.Id == CreamId) return t;

            return BuiltIn[0];
        }

        public static bool IsBuiltInId(string? id)
            => !string.IsNullOrEmpty(id) && id!.StartsWith("builtin.", StringComparison.Ordinal);
    }
}
