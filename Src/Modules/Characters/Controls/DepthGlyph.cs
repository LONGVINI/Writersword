using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Значок уровня детализации: три полоски по возрастанию, как шкала
    /// сигнала. Залитых полосок столько, какой уровень: одна — кратко, две —
    /// подробно, три — глубоко. Уровень 0 — «по важности персонажа»: все три
    /// полоски контуром, без заливки.
    ///
    /// Полоски, а не буквы: «К», «П», «Г» приходилось расшифровывать, а
    /// «больше полосок — глубже» читается сразу и одинаково в редакторе
    /// анкеты, в карточке и в шаблонах.
    ///
    /// Цвет берётся из Foreground — того же, что у текста вокруг: на кнопке
    /// значок перекрашивается вместе с её подписью при наведении и выборе.
    /// </summary>
    public class DepthGlyph : Control
    {
        public static readonly StyledProperty<int> LevelProperty =
            AvaloniaProperty.Register<DepthGlyph, int>(nameof(Level));

        public static readonly StyledProperty<double> SizeProperty =
            AvaloniaProperty.Register<DepthGlyph, double>(nameof(Size), 12d);

        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner<DepthGlyph>();

        static DepthGlyph()
        {
            AffectsRender<DepthGlyph>(LevelProperty, SizeProperty, ForegroundProperty);
            AffectsMeasure<DepthGlyph>(SizeProperty);
        }

        /// <summary>Уровень: 1 — кратко, 2 — подробно, 3 — глубоко, 0 — по важности персонажа.</summary>
        public int Level
        {
            get => GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        public double Size
        {
            get => GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var side = Math.Max(6, Size);
            return new Size(side, side);
        }

        public override void Render(DrawingContext context)
        {
            var side = Math.Min(Bounds.Width, Bounds.Height);
            if (side <= 0) return;

            var brush = Foreground ?? Brushes.Gray;
            var color = brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;

            var filled = new SolidColorBrush(color, brush.Opacity);
            var empty = new SolidColorBrush(color, brush.Opacity * 0.28);
            var outline = new Pen(new SolidColorBrush(color, brush.Opacity * 0.9), Math.Max(1, side / 12));

            var count = CharacterDetailLevels.Max;
            var barWidth = side * 0.24;
            var gap = (side - barWidth * count) / (count - 1);
            var left = (Bounds.Width - side) / 2;
            var bottom = (Bounds.Height + side) / 2;
            var radius = Math.Max(0.5, barWidth * 0.3);

            for (int i = 0; i < count; i++)
            {
                var height = side * (0.42 + 0.29 * i);
                var rect = new Rect(left + i * (barWidth + gap), bottom - height, barWidth, height);

                if (Level <= 0)
                {
                    var inset = outline.Thickness / 2;
                    context.DrawRectangle(null, outline, rect.Deflate(inset), radius, radius);
                    continue;
                }

                context.DrawRectangle(i < Level ? filled : empty, null, rect, radius, radius);
            }
        }
    }
}
