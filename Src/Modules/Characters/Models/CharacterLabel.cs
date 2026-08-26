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
        public const string Heart = "heart";
        public const string Flag = "flag";
        public const string Lock = "lock";
        public const string Bolt = "bolt";
        public const string Eye = "eye";
        public const string Shield = "shield";
        public const string Moon = "moon";
        public const string Check = "check";

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
            [Crown] = "M5,16L3,5l5.5,5L12,4l3.5,6L21,5l-2,11H5zM19,19c0,0.55 -0.45,1 -1,1H6c-0.55,0 -1,-0.45 -1,-1v-1h14V19z",
            [Heart] = "M12,21.35l-1.45,-1.32C5.4,15.36 2,12.28 2,8.5 2,5.42 4.42,3 7.5,3c1.74,0 3.41,0.81 4.5,2.09C13.09,3.81 14.76,3 16.5,3 19.58,3 22,5.42 22,8.5c0,3.78 -3.4,6.86 -8.55,11.54L12,21.35z",
            [Flag] = "M14.4,6L14,4H5v17h2v-7h6.6l0.4,2h7V6H14.4z",
            [Lock] = "M18,8h-1V6c0,-2.76 -2.24,-5 -5,-5S7,3.24 7,6v2H6c-1.1,0 -2,0.9 -2,2v10c0,1.1 0.9,2 2,2h12c1.1,0 2,-0.9 2,-2V10C20,8.9 19.1,8 18,8zM12,17c-1.1,0 -2,-0.9 -2,-2s0.9,-2 2,-2 2,0.9 2,2S13.1,17 12,17zM15.1,8H8.9V6c0,-1.71 1.39,-3.1 3.1,-3.1 1.71,0 3.1,1.39 3.1,3.1V8z",
            [Bolt] = "M7,2v11h3v9l7,-12h-4l4,-8z",
            [Eye] = "M12,4.5C7,4.5 2.73,7.61 1,12c1.73,4.39 6,7.5 11,7.5s9.27,-3.11 11,-7.5C21.27,7.61 17,4.5 12,4.5zM12,17c-2.76,0 -5,-2.24 -5,-5s2.24,-5 5,-5 5,2.24 5,5S14.76,17 12,17zM12,9c-1.66,0 -3,1.34 -3,3s1.34,3 3,3 3,-1.34 3,-3S13.66,9 12,9z",
            [Shield] = "M12,1L3,5v6c0,5.55 3.84,10.74 9,12 5.16,-1.26 9,-6.45 9,-12V5L12,1z",
            [Moon] = "M9.37,5.51C9.19,6.15 9.1,6.82 9.1,7.5c0,4.08 3.32,7.4 7.4,7.4 0.68,0 1.35,-0.09 1.99,-0.27C17.45,17.19 14.93,19 12,19c-3.86,0 -7,-3.14 -7,-7C5,9.07 6.81,6.55 9.37,5.51z",
            [Check] = "M9,16.17L4.83,12l-1.42,1.41L9,19 21,7l-1.41,-1.41z"
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
