using System;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Settings
{
    /// <summary>
    /// Вид рабочей области при правке: чем залит лист, каким цветом идёт текст,
    /// что лежит фоном позади страниц и что уезжает с экрана в режиме фокуса.
    ///
    /// От <see cref="ReadingSettings"/> отличается назначением, а не устройством:
    /// оформление берётся тем же <see cref="ReadingTheme"/> — второй такой же набор
    /// полей рядом с первым разошёлся бы с ним через неделю. Разведены выбор и
    /// рабочая копия: писать и читать человек может при разном свете, и навязывать
    /// одно другому нельзя.
    ///
    /// Ни на печать, ни на экспорт, ни на содержание рукописи вид не влияет: цвет
    /// листа на экране — это про глаза, а не про документ.
    /// </summary>
    public sealed class EditorViewSettings
    {
        /// <summary>
        /// Вид применяется к правке. Выключено — лист белый, поле серое, текст
        /// авторского цвета, то есть ровно так, как было до появления вкладки.
        /// </summary>
        public bool ThemeEnabled { get; set; }

        /// <summary>Опознаватель выбранного вида — по нему список знает, что отмечать.</summary>
        public string ThemeId { get; set; } = ReadingTheme.WhiteId;

        /// <summary>
        /// Рабочая копия вида: по ней рисуется лист. Лента правит её — свет, фон,
        /// цвета, — а сохранённый вид остаётся нетронутым, как и в чтении.
        /// </summary>
        public ReadingTheme Active { get; set; } =
            ReadingTheme.FindBuiltIn(ReadingTheme.WhiteId).Clone();

        // ── Режим фокуса ──────────────────────────────────────────────────

        /// <summary>Убирать линейки, пока идёт фокус.</summary>
        public bool FocusHidesRuler { get; set; } = true;

        /// <summary>Убирать строку состояния, пока идёт фокус.</summary>
        public bool FocusHidesStatusBar { get; set; } = true;

        /// <summary>
        /// Возвращать ленту, когда указатель подходит к верхнему краю. Выключено —
        /// лента возвращается только язычком: тем, кто пишет с тачпада, случайный
        /// заезд мышью к верху экрана мешает больше, чем помогает.
        /// </summary>
        public bool FocusRibbonOnHover { get; set; } = true;

        // ── Производные ───────────────────────────────────────────────────

        /// <summary>Тёмный ли лист. По нему решается, что делать со служебными мелочами.</summary>
        [JsonIgnore]
        public bool IsDark => ThemeEnabled && Active is not null && Active.IsDark;

        /// <summary>Позади страниц лежит картинка.</summary>
        [JsonIgnore]
        public bool HasBackdropImage
            => ThemeEnabled
               && Active is { UseBackdropImage: true }
               && !string.IsNullOrWhiteSpace(Active.BackdropImagePath);

        /// <summary>Ставит вид в работу: копия его значений становится рабочей.</summary>
        public void ApplyTheme(ReadingTheme theme)
        {
            if (theme is null) return;
            ThemeId = theme.Id;
            Active = theme.Clone();
        }

        /// <summary>Приводит значения к допустимым: настройки могли прийти из файла.</summary>
        public void Normalize()
        {
            Active ??= ReadingTheme.FindBuiltIn(ThemeId).Clone();

            Active.Brightness = Math.Clamp(Active.Brightness, 0.35, 1.0);
            Active.Contrast = Math.Clamp(Active.Contrast, 0.6, 1.6);
            Active.Warmth = Math.Clamp(Active.Warmth, 0.0, 1.0);
            Active.BackdropImageOpacity = Math.Clamp(Active.BackdropImageOpacity, 0.0, 1.0);

            if (string.IsNullOrWhiteSpace(ThemeId)) ThemeId = ReadingTheme.WhiteId;
        }

        public EditorViewSettings Clone() => new()
        {
            ThemeEnabled = ThemeEnabled,
            ThemeId = ThemeId,
            Active = Active?.Clone() ?? ReadingTheme.FindBuiltIn(ReadingTheme.WhiteId).Clone(),
            FocusHidesRuler = FocusHidesRuler,
            FocusHidesStatusBar = FocusHidesStatusBar,
            FocusRibbonOnHover = FocusRibbonOnHover
        };
    }
}
