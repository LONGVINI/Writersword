using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using Writersword.Infrastructure.Behaviours;
using Writersword.Modules.TextEditor.ViewModels.Components;
using Strings = Writersword.Modules.TextEditor.Resources.TextEditorStrings;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Переключатель типа табуляции — квадратик на пересечении линеек.
    ///
    /// Стоит там же, где его держит Word, и по той же причине: это не команда над текстом,
    /// а состояние линейки, и место ему на самой линейке, а не в ленте. Щелчок перебирает
    /// типы по кругу; следующая позиция, поставленная щелчком по линейке, встанет
    /// показанного вида.
    ///
    /// Раньше квадрат рисовался внутри горизонтальной линейки, у её левого края. Место было
    /// не своё: линейка начинается там, где начинается прокручиваемая область, и квадрат
    /// налезал на шкалу, а подсказка к нему — на весь лист. Отдельный контрол в углу сетки
    /// решает и то, и другое: у него свои границы, и подсказка встаёт рядом с ним сама.
    ///
    /// Рисуется обычными средствами Avalonia, без Skia: фигура из четырёх отрезков не стоит
    /// отдельного слоя отрисовки, а рядом с линейкой этот квадрат живёт только визуально.
    /// </summary>
    public sealed class TabTypeBoxControl : Control
    {
        private const double BoxSizePx = 18.0;

        // Прежний бирюзовый: им квадрат рисуется, пока вид не задал свой цвет
        // позиций табуляции.
        private static readonly Color DefaultGlyphColor = Color.FromRgb(0x0E, 0x7A, 0x7A);

        private RulerViewModel? _vm;

        // Кисть и перо значка пересобираются при смене цвета, а не на каждый кадр:
        // цвет меняется от силы раз в сеанс, а перерисовок у квадрата много.
        private Color _glyphColor = DefaultGlyphColor;
        private IBrush _glyphBrush = new SolidColorBrush(DefaultGlyphColor);
        private IPen? _glyphPen;

        public TabTypeBoxControl()
        {
            Width = 24;
            Height = 24;
            Cursor = new Cursor(StandardCursorType.Hand);
            Focusable = false;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = DataContext as RulerViewModel;

            if (_vm is not null)
                _vm.PropertyChanged += OnVmPropertyChanged;

            UpdateHint();
            InvalidateVisual();
        }

        private void OnVmPropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RulerViewModel.NextTabAlignment))
            {
                UpdateHint();
                InvalidateVisual();
                return;
            }

            // Квадрат стоит в углу линеек и обязан краситься вместе с ними: вид листа
            // меняется на ходу, и оставшийся бирюзовым значок на ночной бумаге читается
            // как чужая деталь.
            if (e.PropertyName is nameof(RulerViewModel.ThemeActive)
                or nameof(RulerViewModel.ThemeTabHex)
                or nameof(RulerViewModel.ThemeSheetHex)
                or nameof(RulerViewModel.ThemeInkHex))
                InvalidateVisual();
        }

        /// <summary>Цвет значка: заданный виду или прежний бирюзовый.</summary>
        private Color ResolveGlyphColor()
        {
            if (_vm is { ThemeActive: true, ThemeTabHex: { Length: > 0 } hex }
                && Color.TryParse(hex, out var parsed))
                return parsed;

            return DefaultGlyphColor;
        }

        /// <summary>Цвет из вида линейки, если он задан и разбирается.</summary>
        private static bool TryThemeColor(string? hex, out Color color)
        {
            color = default;
            return !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out color);
        }

        /// <summary>
        /// Перекладывает подсказку под выбранный тип. Тексты ставятся свойствами, а не
        /// разметкой, потому что меняются вместе с типом: разметка задала бы их один раз.
        ///
        /// Перечисляются все четыре типа, а не только выбранный. Переключатель перебирает
        /// их по кругу вслепую: щёлкнув, человек видит новый значок, но не знает, что тот
        /// делает и сколько ещё щелчков до нужного. Список отвечает на оба вопроса сразу,
        /// а выбранный в нём выделен цветом.
        /// </summary>
        private void UpdateHint()
        {
            var alignment = _vm?.NextTabAlignment ?? Models.Styles.TabAlignment.Left;

            var all = new[]
            {
                Models.Styles.TabAlignment.Left,
                Models.Styles.TabAlignment.Center,
                Models.Styles.TabAlignment.Right,
                Models.Styles.TabAlignment.Decimal
            };

            var list = new System.Text.StringBuilder();

            foreach (var item in all)
            {
                if (list.Length > 0) list.Append('\n');

                // Имя выбранного берётся в звёздочки — разметка подсказки красит такие
                // куски акцентным цветом. Пара звёздочек в строке «Сейчас» и пара здесь
                // дают ровно два выделения: разбор идёт по парам, и остальные имена
                // остаются обычным текстом.
                string name = item == alignment
                    ? "*" + AlignmentName(item) + "*"
                    : AlignmentName(item);

                list.Append(HorizontalRulerControl.TabIconMarkup(item));
                list.Append(string.Format(Strings.Tab_Hint_TypeLine, name, AlignmentMeaning(item)));
            }

            TooltipBehavior.SetShowDelay(this, 500);
            TooltipBehavior.SetDescription(this,
                string.Format(Strings.Tab_Hint_TypeBody,
                    AlignmentName(alignment), list.ToString()));

            // Заголовок ставится последним: именно он подписывает контрол на события
            // указателя, и с ним подсказка соберётся уже с готовым описанием.
            TooltipBehavior.SetTip(this, Strings.Tab_Hint_TypeTitle);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (_vm is null || _vm.IsReadOnly) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            _vm.CycleNextTabAlignment();
            e.Handled = true;
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);

            var alignment = _vm?.NextTabAlignment ?? Models.Styles.TabAlignment.Left;

            double left = (Bounds.Width - BoxSizePx) / 2;
            double top = (Bounds.Height - BoxSizePx) / 2;
            var box = new Rect(left, top, BoxSizePx, BoxSizePx);

            // Подложка и рамка берутся у вида, когда он назначен: квадрат — часть
            // линейки, и светлая коробка над ночной шкалой выглядит дырой в ней.
            IBrush? background = this.FindResource("BgSurfaceBrush") as IBrush;
            IBrush? border = this.FindResource("BorderDefaultBrush") as IBrush;

            if (_vm is { ThemeActive: true } themed)
            {
                if (TryThemeColor(themed.ThemeSheetHex, out var sheet))
                    background = new SolidColorBrush(sheet);

                if (TryThemeColor(themed.ThemeInkHex, out var ink))
                    border = new SolidColorBrush(ink, 0.24);
            }

            if (background is not null)
                ctx.DrawRectangle(background, null, new RoundedRect(box, 3));

            if (border is not null)
                ctx.DrawRectangle(null, new Pen(border, 1), new RoundedRect(box, 3));

            var glyphColor = ResolveGlyphColor();
            if (glyphColor != _glyphColor || _glyphPen is null)
            {
                _glyphColor = glyphColor;
                _glyphBrush = new SolidColorBrush(glyphColor);
                _glyphPen = new Pen(_glyphBrush, 1.6, lineCap: PenLineCap.Round);
            }

            // Значок тот же, что линейка рисует у поставленных позиций: ножка стоит на
            // отметке, полка показывает, в какую сторону от неё пойдёт текст. Совпадение
            // формы здесь и там — единственное, что связывает переключатель с результатом.
            double cx = Math.Round(box.Center.X) + 0.5;
            double bottom = box.Bottom - 4;
            double topY = box.Top + 4;
            const double Arm = 5;

            ctx.DrawLine(_glyphPen, new Point(cx, topY), new Point(cx, bottom));

            switch (alignment)
            {
                case Models.Styles.TabAlignment.Left:
                    ctx.DrawLine(_glyphPen, new Point(cx, bottom), new Point(cx + Arm, bottom));
                    break;
                case Models.Styles.TabAlignment.Right:
                    ctx.DrawLine(_glyphPen, new Point(cx - Arm, bottom), new Point(cx, bottom));
                    break;
                default:
                    ctx.DrawLine(_glyphPen,
                        new Point(cx - Arm + 1, bottom), new Point(cx + Arm - 1, bottom));
                    break;
            }

            if (alignment == Models.Styles.TabAlignment.Decimal)
                ctx.DrawEllipse(_glyphBrush, null, new Point(cx + 3, bottom - 3.5), 1.3, 1.3);
        }

        private static string AlignmentName(Models.Styles.TabAlignment alignment)
            => alignment switch
            {
                Models.Styles.TabAlignment.Center => Strings.Tab_AlignCenter,
                Models.Styles.TabAlignment.Right => Strings.Tab_AlignRight,
                Models.Styles.TabAlignment.Decimal => Strings.Tab_AlignDecimal,
                _ => Strings.Tab_AlignLeft
            };

        private static string AlignmentMeaning(Models.Styles.TabAlignment alignment)
            => alignment switch
            {
                Models.Styles.TabAlignment.Center => Strings.Tab_Hint_WhatCenter,
                Models.Styles.TabAlignment.Right => Strings.Tab_Hint_WhatRight,
                Models.Styles.TabAlignment.Decimal => Strings.Tab_Hint_WhatDecimal,
                _ => Strings.Tab_Hint_WhatLeft
            };
    }
}
