using System;
using System.Collections.Generic;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Эффект метки на внешний вид карточки персонажа. Эффект — усилитель,
    /// носитель смысла — всегда объект (значок метки): серый или тёмный вид
    /// сам по себе ничего не значит, потому что серый — легальный цвет
    /// персонажа.
    /// </summary>
    public enum CharacterLabelEffect
    {
        /// <summary>Метка ничего не меняет во внешнем виде карточки.</summary>
        None = 0,

        /// <summary>Карточка затемняется/приглушается (мёртв, недействителен).</summary>
        Dim = 1
    }

    /// <summary>
    /// Метка персонажа: объединяет состояния («ранен», «в бегах»), финалы
    /// («мёртв», «закончил арку») и эмблемы (значок клуба). Пользователь
    /// создаёт метки свободно — ограничение потока идей противоречит
    /// концепции программы. Порядок и показ на карточке задаёт пользователь.
    /// </summary>
    public class CharacterLabel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;

        /// <summary>Ключ встроенной иконки — см. CharacterLabelIcons.</summary>
        public string Icon { get; set; } = CharacterLabelIcons.Dot;

        /// <summary>
        /// Своя картинка вместо встроенного значка: эмблема клуба, герб дома,
        /// нарисованный автором знак. Хранится ссылкой того же вида, что
        /// аватары, поэтому уезжает вместе с проектом.
        ///
        /// Пока пусто — рисуется встроенный значок по ключу Icon. Ключ при этом
        /// не теряется: убрал картинку — вернулся прежний значок.
        /// </summary>
        public string? IconImage { get; set; }

        public bool HasCustomIcon => !string.IsNullOrWhiteSpace(IconImage);

        /// <summary>
        /// Цвет кружка-подложки под фигурой. Поддерживает и одноцвет, и
        /// градиент — тем же разбором, что цвет персонажа.
        /// </summary>
        public string Color { get; set; } = "#607D8B";

        /// <summary>
        /// Цвет самой фигуры: встроенной либо одноцветной картинки формата
        /// SVG. Растровая картинка идёт как есть — перекрашивать чужой герб
        /// или фотографию программа не берётся.
        ///
        /// Пусто означает белый. Именно пусто, а не записанный белый: белая
        /// фигура на цветном кружке — вид по умолчанию, и он не должен
        /// зависеть от того, открывал пользователь редактор цвета или нет.
        /// </summary>
        public string? IconColor { get; set; }

        /// <summary>
        /// Рисовать кружок под фигурой. Без него остаётся одна фигура: так
        /// уместнее для готовых эмблем и гербов, которым круглая подложка
        /// только мешает.
        /// </summary>
        public bool ShowBackdrop { get; set; } = true;

        public CharacterLabelEffect Effect { get; set; } = CharacterLabelEffect.None;

        /// <summary>Показывать значок метки на карточке в списках. Метки с
        /// false живут только внутри карточки персонажа и глаз не мозолят.</summary>
        public bool ShowOnCard { get; set; } = true;

        /// <summary>Порядок показа; задаётся пользователем. На карточке
        /// списка видны первые метки, остальные сворачиваются в «+N».</summary>
        public int Order { get; set; }

        /// <summary>Подсказка при наведении — «что значит эта метка».</summary>
        public string Description { get; set; } = string.Empty;

        public bool IsBuiltIn => Id.StartsWith(CharacterBuiltinLabels.BuiltInPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ключи встроенных иконок меток и их геометрия (Path Data). Набор
    /// расширяется без изменения модели — Icon хранит строковый ключ.
    /// </summary>
    public static class CharacterLabelIcons
    {
        public const string Dot = "dot";
        public const string Cross = "cross";
        public const string Skull = "skull";
        public const string Drop = "drop";
        public const string Star = "star";
        public const string Crown = "crown";

        /// <summary>Геометрия по ключу; неизвестный ключ падает на точку.</summary>
        public static string GetPathData(string icon) =>
            Paths.TryGetValue(icon ?? string.Empty, out var data) ? data : Paths[Dot];

        private static readonly Dictionary<string, string> Paths = new()
        {
            [Dot] = "M12,7 A5,5 0 1 1 11.99,7 Z",
            [Cross] = "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z",
            [Skull] = "M12,2C7.03,2 3,6.03 3,11c0,2.85 1.34,5.38 3.42,7.02V21c0,0.55 0.45,1 1,1h1.5v-2h2v2h2v-2h2v2H16c0.55,0 1,-0.45 1,-1v-2.98C19.66,16.38 21,13.85 21,11C21,6.03 16.97,2 12,2zM8.5,13C7.67,13 7,12.33 7,11.5C7,10.67 7.67,10 8.5,10s1.5,0.67 1.5,1.5C10,12.33 9.33,13 8.5,13zM15.5,13c-0.83,0 -1.5,-0.67 -1.5,-1.5c0,-0.83 0.67,-1.5 1.5,-1.5s1.5,0.67 1.5,1.5C17,12.33 16.33,13 15.5,13z",
            [Drop] = "M12,2C12,2 5,10.27 5,15c0,3.87 3.13,7 7,7s7,-3.13 7,-7C19,10.27 12,2 12,2z",
            [Star] = "M12,17.27L18.18,21l-1.64,-7.03L22,9.24l-7.19,-0.61L12,2L9.19,8.63L2,9.24l5.46,4.73L5.82,21L12,17.27z",
            [Crown] = "M5,16L3,5l5.5,5L12,4l3.5,6L21,5l-2,11H5zM19,19c0,0.55 -0.45,1 -1,1H6c-0.55,0 -1,-0.45 -1,-1v-1h14V19z"
        };
    }

    /// <summary>
    /// Встроенные метки. «Мёртв» — единственная фундаментальная: она есть
    /// всегда, со значком черепа и затемнением карточки. Остальные метки
    /// пользователь создаёт сам.
    /// </summary>
    public static class CharacterBuiltinLabels
    {
        public const string BuiltInPrefix = "builtin.";
        public const string DeadId = "builtin.dead";

        /// <summary>Прежний цвет «Мёртв», когда цвет красил кружок.</summary>
        private const string LegacyDeadColor = "#B71C1C";

        /// <summary>Вид «Мёртв»: красный череп на почти чёрном кружке.</summary>
        public const string DeadBackdropColor = "#1A1012";
        public const string DeadIconColor = "#FF5252";

        /// <summary>Создать экземпляр метки «Мёртв». Имя передаётся снаружи —
        /// модель не тянет ресурсы локализации.</summary>
        public static CharacterLabel CreateDead(string localizedName) => new()
        {
            Id = DeadId,
            Name = localizedName,
            Icon = CharacterLabelIcons.Skull,
            Color = DeadBackdropColor,
            IconColor = DeadIconColor,
            Effect = CharacterLabelEffect.Dim,
            ShowOnCard = true,
            Order = -1
        };

        /// <summary>
        /// Приводит старые сохранения к текущему виду встроенных меток.
        /// Ранее «Мёртв» рисовалась крестиком — тем же символом, которым в
        /// интерфейсе обозначается удаление, из-за чего метка читалась как
        /// кнопка «убрать». Значок заменяется на череп; выбранные
        /// пользователем цвет, эффект и порядок не трогаются.
        /// </summary>
        public static void NormalizeBuiltIn(CharacterLabel label)
        {
            if (label == null) return;
            if (label.Id != DeadId) return;
            if (label.Icon == CharacterLabelIcons.Cross) label.Icon = CharacterLabelIcons.Skull;

            // Цвет метки раньше красил кружок, а фигура была жёстко белой.
            // Теперь цвет фигуры — отдельное поле, и у «Мёртв» это красный
            // череп на почти чёрном кружке. Переносится только нетронутое
            // значение по умолчанию: свой подобранный цвет остаётся как есть.
            if (string.IsNullOrWhiteSpace(label.IconColor) &&
                string.Equals(label.Color, LegacyDeadColor, StringComparison.OrdinalIgnoreCase))
            {
                label.Color = DeadBackdropColor;
                label.IconColor = DeadIconColor;
            }
        }
    }
}
