using System;
using System.Collections.Generic;
using SkiaSharp;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Toc;
using Writersword.Modules.TextEditor.Resources;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Оглавление как единый блок, а не россыпь абзацев.
    ///
    /// Строки оглавления — обычные абзацы: их правят, переносят, отменяют тем же кодом,
    /// что и рукопись. Но читается оглавление целиком, и человек, ткнувший в него мышью,
    /// спрашивает не «где тут курсор», а «что это за кусок и откуда он взялся». Ответ даёт
    /// подложка на весь блок и закладка над ним — ровно то, что показывает Word, встав
    /// кареткой в поле оглавления.
    ///
    /// Подложка не выделение: она ничего не копирует и не удаляется клавишей. Выделение
    /// блока — отдельное действие, по левой половине закладки; по правой оглавление
    /// пересобирается.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Высота закладки над блоком в пунктах.</summary>
        private const float TocChipHeightPt = 13f;

        /// <summary>Кегль надписи на закладке.</summary>
        private const float TocChipFontPt = 8f;

        /// <summary>Поля надписи внутри закладки по горизонтали.</summary>
        private const float TocChipPadXPt = 5f;

        /// <summary>Зазор между закладкой и первой строкой блока.</summary>
        private const float TocChipGapPt = 2f;

        /// <summary>Скругление углов закладки и подложки.</summary>
        private const float TocChipRadiusPt = 2.5f;

        // Шрифт надписей закладки. Заводится один раз на приложение: начертание ищется
        // по имени семейства в системном списке шрифтов, а закладка перерисовывается на
        // каждом кадре листа с оглавлением.
        private static SKFont? _tocChipFont;

        private static SKFont ChipFont
        {
            get
            {
                if (_tocChipFont is not null) return _tocChipFont;

                var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
                    ?? SKTypeface.Default;

                _tocChipFont = new SKFont(typeface, TocChipFontPt);
                return _tocChipFont;
            }
        }

        // Закладка, нарисованная в прошлом кадре: по ней работает нажатие. Координаты
        // логические, без сдвига центрирования листа — тот же уговор, что у картинок.
        private SKRect _tocChipRectPt = SKRect.Empty;

        // Границы между кнопками закладки: имя | обновить | удалить.
        private float _tocChipSplit1XPt;
        private float _tocChipSplit2XPt;

        // Лист, на котором нарисована закладка. Нужен при развороте страниц рядом:
        // точка нажатия переводится в координаты именно того листа.
        private int _tocChipPageIndex = -1;

        // Чьё оглавление показала закладка. Каретка могла уйти из блока после кадра,
        // и нажатие по устаревшему прямоугольнику не должно ничего пересобирать.
        private Guid _tocChipOwnerId = Guid.Empty;

        /// <summary>Кнопка закладки под указателем.</summary>
        private enum TocChipZone
        {
            /// <summary>Мимо закладки.</summary>
            None = 0,
            /// <summary>Имя блока — взять оглавление целиком.</summary>
            Name = 1,
            /// <summary>Пересобрать оглавление.</summary>
            Update = 2,
            /// <summary>Убрать оглавление из рукописи.</summary>
            Remove = 3
        }

        // ── Кто сейчас под кареткой ───────────────────────────────────────

        /// <summary>
        /// Опознаватель оглавления, внутри которого стоит каретка. null — каретка вне
        /// оглавления. Абзацы ячеек не в счёт: оглавление живёт в потоке документа.
        /// </summary>
        private Guid? CaretTocOwnerId(List<ParaLayout> layouts)
        {
            if (_caretPara < 0 || _caretPara >= layouts.Count) return null;

            var pl = layouts[_caretPara];
            if (pl.Cell is not null) return null;

            return pl.Vm.Model?.Properties.TocOwnerId;
        }

        /// <summary>
        /// Настройки оглавления по опознавателю. Спрашиваются у модели документа, а не
        /// ищутся здесь по списку: потерянные настройки она восстанавливает по самим
        /// строкам, и своим поиском полотно получало бы «нет» там, где оглавление есть.
        /// </summary>
        private TocSettings? FindTocSettings(Guid ownerId) => DocVm?.TocSettingsFor(ownerId);

        // ── Отрисовка ─────────────────────────────────────────────────────

        /// <summary>
        /// Подложка блока и закладка над ним. Зовётся из отрисовки каждого слайса абзаца
        /// до текста: подложка лежит под буквами, иначе она забивала бы их собой.
        /// </summary>
        private void DrawTocField(
            SKCanvas canvas, int idx, ParaLayout pl,
            List<ParaLayout> layouts, SKTextLayout renderLayout,
            float absX, float absY)
        {
            if (pl.Cell is not null) return;

            // Подложка показывает, что каретка стоит внутри оглавления. Там, где каретки
            // не бывает вовсе — в чтении, в развороте, в предпросмотре, — показывать
            // нечего: подложка читалась бы как выделение, которого никто не делал.
            //
            // Спрашивается именно «каретка здесь бывает», а не «каретка нарисована в
            // этом кадре». Второе — фаза мигания, и подложка мигала вместе с палочкой.
            // Заметно это было не как мигание: тик мигания рисует одну палочку поверх
            // готового снимка, а подложка остаётся в снимке до следующего полного
            // рендера — то есть до прокрутки за его край. Прокрутил — пропала,
            // прокрутил ещё — вернулась.
            if (!CaretPlaceable) return;

            Guid? caretOwner = CaretTocOwnerId(layouts);
            if (caretOwner is null) return;

            Guid? own = pl.Vm.Model?.Properties.TocOwnerId;
            if (own != caretOwner) return;

            int from = Math.Max(pl.LineFrom, 0);
            int to = Math.Min(pl.LineTo, renderLayout.Lines.Count);

            float height = 0f;
            for (int i = from; i < to; i++)
                height += renderLayout.Lines[i].Height;

            if (height <= 0.5f) return;

            float left = absX;
            float width = renderLayout.LeftIndentPt
                          + renderLayout.TextAreaWidthPt
                          + renderLayout.RightIndentPt;

            if (width <= 1f) return;

            // Подложка забирает и интервалы абзаца: без них между строками оглавления
            // оставались бы просветы, и сплошной блок читался бы полосатым. Интервал
            // до берётся только у настоящего начала абзаца, интервал после — только у
            // его конца: у слайса-продолжения ни того, ни другого нет.
            float top = absY - (from == 0 ? renderLayout.SpaceBeforePt : 0f);
            float bottom = absY + height
                           + (to >= renderLayout.Lines.Count ? renderLayout.SpaceAfterPt : 0f);

            var band = new SKRect(left, top, left + width, bottom);

            using (var fill = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = new SKColor(0x5A, 0x6B, 0x86, 0x1E)
            })
            {
                canvas.DrawRect(band, fill);
            }

            // Закладка ставится один раз — над первым слайсом блока. Первым считается
            // тот, перед которым в потоке нет абзаца того же оглавления: у блока,
            // разрезанного между листами, продолжение своей закладки не носит, как и
            // в Word.
            if (!IsFirstTocSlice(idx, layouts, caretOwner.Value)) return;

            DrawTocChip(canvas, pl, caretOwner.Value, left, band.Top);
        }

        /// <summary>
        /// Слайс открывает блок оглавления — выше него нет ни одной его строки.
        ///
        /// Проверяется весь путь вверх, а не один соседний абзац. Разница видна, когда в
        /// середину оглавления попал чужой абзац: по соседу первой считалась бы и строка
        /// сразу за ним, и закладок на листе оказывалось две. Хуже того, на нажатие
        /// отзывалась бы только нижняя — прямоугольник закладки в полотне один, и вторая
        /// отрисовка затирала первую.
        ///
        /// Проход вверх обрывается на первой же своей строке, поэтому для всех слайсов,
        /// кроме самого первого, он стоит одно сравнение.
        /// </summary>
        private static bool IsFirstTocSlice(int idx, List<ParaLayout> layouts, Guid ownerId)
        {
            for (int i = idx - 1; i >= 0; i--)
            {
                var prev = layouts[i];
                if (prev.Cell is not null) continue;

                if (prev.Vm.Model?.Properties.TocOwnerId == ownerId) return false;
            }

            return true;
        }

        /// <summary>
        /// Закладка над блоком: имя, «Обновить», «Удалить».
        ///
        /// Три кнопки, а не одна: имя отвечает на вопрос «что это», остальные две —
        /// действия над блоком целиком. Слить имя с обновлением значило бы пересобирать
        /// оглавление всякий раз, когда человек просто хотел взять его целиком.
        ///
        /// «Удалить» стоит последним и отделено от «Обновить» полным зазором кнопки: это
        /// единственное здесь действие, которое уносит текст с листа, и промах по нему
        /// стоит дороже остальных. Шаг отмены у него свой — тот же, что у кнопки ленты.
        /// </summary>
        private void DrawTocChip(
            SKCanvas canvas, ParaLayout pl, Guid ownerId, float leftPt, float blockTopPt)
        {
            string name = TextEditorStrings.Toc_Group_Main;
            string update = TextEditorStrings.Toc_Update;
            string remove = TextEditorStrings.Toc_Remove;

            // Шрифт закладки живёт между кадрами. Поиск начертания по имени семейства —
            // обращение к системному списку шрифтов, и делать его заново на каждую
            // отрисовку листа значит платить за одно и то же по нескольку раз в секунду.
            var font = ChipFont;

            float nameBoxW = font.MeasureText(name) + TocChipPadXPt * 2f;
            float updateBoxW = font.MeasureText(update) + TocChipPadXPt * 2f;
            float removeBoxW = font.MeasureText(remove) + TocChipPadXPt * 2f;

            float top = blockTopPt - TocChipGapPt - TocChipHeightPt;
            float bottom = blockTopPt - TocChipGapPt;

            var chip = new SKRect(
                leftPt, top, leftPt + nameBoxW + updateBoxW + removeBoxW, bottom);

            // Заливка непрозрачная. Полупрозрачная читалась ровно до тех пор, пока под
            // закладкой был чистый лист: строка, оказавшаяся под ней, просвечивала сквозь
            // надписи, и разобрать нельзя было ни строку, ни кнопки. Закладка — часть
            // оснастки редактора, а не часть рукописи, и вести себя должна как оснастка:
            // закрывать собой то, на чём лежит.
            using (var fill = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = new SKColor(0xEC, 0xEF, 0xF4)
            })
            {
                canvas.DrawRoundRect(chip, TocChipRadiusPt, TocChipRadiusPt, fill);
            }

            using (var frame = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.6f,
                Color = new SKColor(0x8A, 0x97, 0xAA)
            })
            {
                canvas.DrawRoundRect(chip, TocChipRadiusPt, TocChipRadiusPt, frame);
            }

            float split1X = leftPt + nameBoxW;
            float split2X = split1X + updateBoxW;

            using (var divider = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 0.6f,
                Color = new SKColor(0xB6, 0xBF, 0xCD)
            })
            {
                canvas.DrawLine(split1X, top + 2f, split1X, bottom - 2f, divider);
                canvas.DrawLine(split2X, top + 2f, split2X, bottom - 2f, divider);
            }

            float baseline = bottom - (TocChipHeightPt - TocChipFontPt) * 0.5f - 1.2f;

            using (var ink = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0x2B, 0x33, 0x40)
            })
            {
                canvas.DrawText(name, leftPt + TocChipPadXPt, baseline, font, ink);
                canvas.DrawText(update, split1X + TocChipPadXPt, baseline, font, ink);
            }

            // Удаление подписано своим цветом: в ряду одинаковых надписей глаз не
            // отличает действие, которое можно повторить, от того, что уносит текст.
            using (var warn = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0x9A, 0x33, 0x2B)
            })
            {
                canvas.DrawText(remove, split2X + TocChipPadXPt, baseline, font, warn);
            }

            // Прямоугольник закладки нужен нажатию, а оно приходит на другом потоке:
            // рисует канвас в своём, мышь обрабатывается в потоке окна. Числа,
            // прочитанные вразнобой, дали бы промах по кнопке — редкий и необъяснимый.
            lock (_renderLock)
            {
                _tocChipRectPt = chip;
                _tocChipSplit1XPt = split1X;
                _tocChipSplit2XPt = split2X;
                _tocChipPageIndex = pl.PageIndex;
                _tocChipOwnerId = ownerId;
            }
        }

        // ── Нажатие ───────────────────────────────────────────────────────

        /// <summary>
        /// Нажатие по закладке блока. Точка приходит в координатах окна, переведённых в
        /// пункты, но ещё не привязанных к листу: закладка могла быть нарисована на
        /// соседней странице разворота, и переводить надо через её лист, а не через
        /// ближайший к указателю.
        /// </summary>
        /// <returns>true — нажатие израсходовано закладкой.</returns>
        private bool TocChipPointerPressed(float rawXPt, float rawYPt)
        {
            var zone = TocChipHitTest(rawXPt, rawYPt);
            if (zone == TocChipZone.None) return false;

            Guid ownerId = _tocChipOwnerId;

            if (zone == TocChipZone.Name)
            {
                SelectTocBlock(ownerId);
                return true;
            }

            var settings = FindTocSettings(ownerId);
            if (settings is null) return false;

            // Время операции пишется в журнал. Обе они трогают весь документ — снимают
            // и вставляют абзацы, пересобирают раскладку, — и на большой рукописи
            // заметны на глаз. Без замера спорить о том, где именно уходит время,
            // приходится догадками.
            var watch = System.Diagnostics.Stopwatch.StartNew();

            if (zone == TocChipZone.Update)
            {
                DocVm?.RebuildToc(settings);
                watch.Stop();
                _logger.Debug("[TOC] Обновление: {Ms} мс", watch.ElapsedMilliseconds);
                return true;
            }

            // Удаление уносит блок целиком и кладёт свой шаг отмены — тот же, что у
            // кнопки ленты. Закладку гасим сразу: каретка уйдёт в текст за оглавлением,
            // и прямоугольник от последнего кадра остался бы висеть до следующего.
            DocVm?.RemoveToc(settings);
            ForgetTocChip();

            watch.Stop();
            _logger.Debug(
                "[TOC] Удаление: {Ms} мс ({Phases})",
                watch.ElapsedMilliseconds, DocVm?.LastTocTiming ?? "по фазам не замерено");
            return true;
        }

        /// <summary>Забывает нарисованную закладку: блока, которому она принадлежала, больше нет.</summary>
        private void ForgetTocChip()
        {
            lock (_renderLock)
            {
                _tocChipRectPt = SKRect.Empty;
                _tocChipSplit1XPt = 0f;
                _tocChipSplit2XPt = 0f;
                _tocChipPageIndex = -1;
                _tocChipOwnerId = Guid.Empty;
            }
        }

        /// <summary>
        /// Кнопка закладки под точкой — без последствий. Отдельно от нажатия, потому что
        /// тем же вопросом задаётся курсор при наведении, а менять от наведения ничего
        /// нельзя.
        /// </summary>
        private TocChipZone TocChipHitTest(float rawXPt, float rawYPt)
        {
            SKRect chip;
            float split1X;
            float split2X;
            int chipPage;
            Guid ownerId;
            List<ParaLayout> layouts;

            lock (_renderLock)
            {
                chip = _tocChipRectPt;
                split1X = _tocChipSplit1XPt;
                split2X = _tocChipSplit2XPt;
                chipPage = _tocChipPageIndex;
                ownerId = _tocChipOwnerId;
                layouts = _layouts;
            }

            if (chip.IsEmpty) return TocChipZone.None;
            if (ownerId == Guid.Empty) return TocChipZone.None;

            // Каретка ушла из блока — закладки на листе больше нет, а прямоугольник
            // остался от прошлого кадра.
            if (CaretTocOwnerId(layouts) != ownerId) return TocChipZone.None;

            var (xPt, yPt) = VisualToLogicalPt(rawXPt, rawYPt, chipPage);

            float shift = GetPageShiftXPt();

            if (xPt < chip.Left + shift || xPt > chip.Right + shift) return TocChipZone.None;
            if (yPt < chip.Top || yPt > chip.Bottom) return TocChipZone.None;

            if (xPt <= split1X + shift) return TocChipZone.Name;
            if (xPt <= split2X + shift) return TocChipZone.Update;

            return TocChipZone.Remove;
        }

        /// <summary>
        /// Указателю здесь полагается рука: он либо над закладкой блока, либо над строкой
        /// оглавления с зажатым Ctrl.
        ///
        /// Про Ctrl+щелчок иначе не догадаться — в тексте он ничем не помечен. Рука и есть
        /// единственная подсказка, поэтому проверка строки идёт только при зажатом Ctrl:
        /// без него хит-тест на каждом движении мыши не окупается.
        /// </summary>
        private bool TocHandCursorWanted(
            Avalonia.Input.PointerEventArgs e, Avalonia.Point rawPoint,
            float rawXPt, float rawYPt)
        {
            if (TocChipHitTest(rawXPt, rawYPt) != TocChipZone.None) return true;

            if (!e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return false;

            var (pi, _) = HitTest(rawPoint);
            if (pi < 0 || pi >= _layouts.Count) return false;

            var pl = _layouts[pi];
            if (pl.Cell is not null) return false;

            return pl.Vm.Model?.Properties.TocTargetBlockId is not null;
        }

        /// <summary>
        /// Берёт блок оглавления целиком — от первой его строки до последней.
        ///
        /// Это настоящее выделение текста, а не подсветка: по нему работают копирование,
        /// смена стиля и удаление. Человек, ткнувший в закладку, чаще всего собирается
        /// сделать с оглавлением что-то одним движением.
        /// </summary>
        private void SelectTocBlock(Guid ownerId)
        {
            List<ParaLayout> layouts;
            lock (_renderLock) { layouts = _layouts; }

            int first = -1;
            int last = -1;

            for (int i = 0; i < layouts.Count; i++)
            {
                var pl = layouts[i];
                if (pl.Cell is not null) continue;

                bool mine = pl.Vm.Model?.Properties.TocOwnerId == ownerId;

                if (mine)
                {
                    if (first < 0) first = i;
                    last = i;
                }
                else if (first >= 0)
                {
                    break;
                }
            }

            if (first < 0 || last < 0) return;

            string lastText = GetVmAt(last)?.PlainText ?? string.Empty;

            _selStartPara = first;
            _selStartChar = 0;
            _selEndPara = last;
            _selEndChar = lastText.Length;
            _caretPara = last;
            _caretChar = lastText.Length;

            _isSelecting = false;
            _isCellRangeSelecting = false;
            _tableSelections.Clear();
            _cellFlowRanges.Clear();
            _cellFlowFull.Clear();

            UpdatePreferredX();

            var pvm = GetVmAt(_caretPara);
            if (pvm is not null)
            {
                pvm.SelectionStart = 0;
                pvm.SelectionEnd = lastText.Length;
                DocVm?.SetActiveParagraph(pvm);
            }

            UpdateSelectionContext();
            ResetCaretNoScroll();
            InvalidateFull();
        }

        // ── Переход по строке ─────────────────────────────────────────────

        /// <summary>
        /// Ctrl+щелчок по строке оглавления уводит к её главе — как в Word.
        ///
        /// Обычный щелчок остаётся за кареткой: строка оглавления — текст, и её правят.
        /// Ctrl отличает «пойти по ссылке» от «встать сюда курсором», и ничего у правки
        /// не отбирает.
        /// </summary>
        /// <returns>true — переход сделан и нажатие израсходовано.</returns>
        private bool TocFollowLink(int paragraphIndex)
        {
            if (paragraphIndex < 0 || paragraphIndex >= _layouts.Count) return false;

            var pl = _layouts[paragraphIndex];
            if (pl.Cell is not null) return false;

            if (pl.Vm.Model?.Properties.TocTargetBlockId is not Guid target) return false;

            return GoToBlock(target);
        }
    }
}
