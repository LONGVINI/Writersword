using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Writersword.Modules.TextEditor.Views.Shared
{
    /// <summary>Что показывает образец в меню «Эффекты текста».</summary>
    public enum TextEffectSampleKind
    {
        Outline,
        Shadow,
        Emboss,
        Imprint,
        Hidden,
        CharBorder,
        EmphasisDot,
        EmphasisComma,
        EmphasisCircle,
        EmphasisUnderDot,
        Glow,
        Reflection
    }

    /// <summary>
    /// Образец эффекта букв для плиток меню «Эффекты текста»: буквы «Аа», нарисованные
    /// так же, как документ рисует этот эффект (SKTextRenderer) — контур полыми буквами,
    /// тень сдвинутой копией, рельеф и гравировку светлыми буквами с тёмным краем, скрытый
    /// текст точечным подчёркиванием, рамку знаков рамкой, знаки ударения точками над
    /// каждой буквой. По образцу видно, что включит плитка, ещё до нажатия.
    /// </summary>
    public sealed class TextEffectSample : Control
    {
        /// <summary>Эффект, который показывает образец.</summary>
        public static readonly StyledProperty<TextEffectSampleKind> KindProperty =
            AvaloniaProperty.Register<TextEffectSample, TextEffectSampleKind>(nameof(Kind), TextEffectSampleKind.Outline);

        /// <summary>Кисть букв.</summary>
        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            AvaloniaProperty.Register<TextEffectSample, IBrush?>(nameof(Foreground));

        /// <summary>Текст образца.</summary>
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<TextEffectSample, string>(nameof(Text), "Аа");

        // Светлое лицо букв рельефа и гравировки — как у Word, почти белое.
        private static readonly IBrush EmbossFaceBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));

        // Тёмный край рельефа и гравировки. Свой, а не цвет букв: в тёмной теме буквы
        // светлые, и край цвета букв сливался бы с лицом — образец выглядел бы просто
        // жирными белыми буквами.
        private static readonly IBrush EmbossEdgeBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));

        // Свечение образца — цвет свечения Word по умолчанию, полупрозрачный.
        private static readonly IBrush GlowBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xC0, 0x00));

        // Тень букв — серая, полупрозрачная, как в документе.
        private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80));

        static TextEffectSample()
        {
            AffectsRender<TextEffectSample>(KindProperty, ForegroundProperty, TextProperty);
        }

        public TextEffectSampleKind Kind
        {
            get => GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var brush = Foreground;
            string text = Text;
            double width = Bounds.Width;
            double height = Bounds.Height;
            if (brush is null || string.IsNullOrEmpty(text) || width <= 0 || height <= 0) return;

            bool reflection = Kind == TextEffectSampleKind.Reflection;
            bool emphasis = Kind is TextEffectSampleKind.EmphasisDot
                or TextEffectSampleKind.EmphasisComma
                or TextEffectSampleKind.EmphasisCircle
                or TextEffectSampleKind.EmphasisUnderDot;

            // Знаку ударения нужно место над буквами (или под ними) — буквы мельче.
            double emSize = height * (emphasis || reflection ? 0.58 : 0.72);
            var typeface = new Typeface(FontFamily.Default);

            var formatted = Format(text, typeface, emSize, brush);
            double originX = Math.Round((width - formatted.WidthIncludingTrailingWhitespace) / 2.0);
            double originY = Kind == TextEffectSampleKind.EmphasisUnderDot || reflection
                ? Math.Round(height * 0.02)
                : emphasis
                    ? Math.Round(height - formatted.Height)
                    : Math.Round((height - formatted.Height) / 2.0);
            var origin = new Point(originX, originY);

            double offset = Math.Max(1.0, Math.Round(emSize * 0.06));

            switch (Kind)
            {
                case TextEffectSampleKind.Outline:
                {
                    var geometry = formatted.BuildGeometry(origin);
                    if (geometry is not null)
                        context.DrawGeometry(null, new Pen(brush, Math.Max(0.8, emSize * 0.04)), geometry);
                    return;
                }

                case TextEffectSampleKind.Shadow:
                    context.DrawText(Format(text, typeface, emSize, ShadowBrush),
                        new Point(originX + offset, originY + offset));
                    context.DrawText(formatted, origin);
                    return;

                case TextEffectSampleKind.Emboss:
                case TextEffectSampleKind.Imprint:
                {
                    double edge = Kind == TextEffectSampleKind.Emboss ? offset : -offset;
                    context.DrawText(Format(text, typeface, emSize, EmbossEdgeBrush),
                        new Point(originX + edge, originY + edge));
                    context.DrawText(Format(text, typeface, emSize, EmbossFaceBrush), origin);
                    return;
                }

                case TextEffectSampleKind.Glow:
                {
                    // Ореол — копии букв цветом свечения по кругу вокруг букв.
                    var glowText = Format(text, typeface, emSize, GlowBrush);
                    double radius = Math.Max(1.5, emSize * 0.12);
                    for (int step = 0; step < 12; step++)
                    {
                        double angle = step * Math.PI / 6.0;
                        context.DrawText(glowText, new Point(
                            originX + Math.Cos(angle) * radius,
                            originY + Math.Sin(angle) * radius));
                    }
                    context.DrawText(formatted, origin);
                    return;
                }

                case TextEffectSampleKind.Reflection:
                {
                    // Буквы и под ними — они же, перевёрнутые и тающие книзу.
                    context.DrawText(formatted, origin);

                    double mirrorAxis = originY + formatted.Height;
                    var fade = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(0x90, 0, 0, 0), 0),
                            new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.6)
                        }
                    };
                    var mirrorRect = new Rect(0, mirrorAxis, width, Math.Max(1, height - mirrorAxis));

                    using (context.PushOpacityMask(fade, mirrorRect))
                    using (context.PushTransform(new Matrix(1, 0, 0, -1, 0, mirrorAxis * 2)))
                        context.DrawText(formatted, origin);
                    return;
                }

                case TextEffectSampleKind.Hidden:
                {
                    context.DrawText(formatted, origin);
                    double lineY = Math.Round(originY + formatted.Baseline + emSize * 0.12) + 0.5;
                    var dotted = new Pen(brush, 1, new DashStyle(new double[] { 1, 1 }, 0));
                    context.DrawLine(dotted,
                        new Point(originX, lineY),
                        new Point(originX + formatted.WidthIncludingTrailingWhitespace, lineY));
                    return;
                }

                case TextEffectSampleKind.CharBorder:
                {
                    context.DrawText(formatted, origin);
                    var frame = new Rect(
                        Math.Round(originX - 2) + 0.5,
                        Math.Round(originY) + 0.5,
                        Math.Round(formatted.WidthIncludingTrailingWhitespace + 4),
                        Math.Round(formatted.Height));
                    context.DrawRectangle(null, new Pen(brush, 1), frame);
                    return;
                }
            }

            DrawEmphasis(context, text, typeface, emSize, brush, formatted, origin);
        }

        /// <summary>
        /// Буквы по одной и знак ударения над каждой (под каждой — у точки снизу):
        /// середина знака — над серединой буквы, как в документе.
        /// </summary>
        private void DrawEmphasis(DrawingContext context, string text, Typeface typeface,
            double emSize, IBrush brush, FormattedText whole, Point origin)
        {
            context.DrawText(whole, origin);

            double radius = Math.Max(1.0, emSize * 0.08);
            bool below = Kind == TextEffectSampleKind.EmphasisUnderDot;
            double centerY = below
                ? origin.Y + whole.Height + radius
                : origin.Y - radius * 0.5;

            var dotPen = new Pen(brush, Math.Max(0.8, radius * 0.45));
            var tailPen = new Pen(brush, Math.Max(0.8, radius * 0.6), lineCap: PenLineCap.Round);

            double x = origin.X;
            foreach (char ch in text)
            {
                var letter = Format(ch.ToString(), typeface, emSize, brush);
                double letterWidth = letter.WidthIncludingTrailingWhitespace;

                if (!char.IsWhiteSpace(ch))
                {
                    var center = new Point(x + letterWidth / 2.0, centerY);

                    if (Kind == TextEffectSampleKind.EmphasisCircle)
                    {
                        context.DrawEllipse(null, dotPen, center, radius, radius);
                    }
                    else
                    {
                        context.DrawEllipse(brush, null, center, radius, radius);

                        // Запятая — точка с хвостиком вниз-влево.
                        if (Kind == TextEffectSampleKind.EmphasisComma)
                            context.DrawLine(tailPen,
                                new Point(center.X + radius * 0.6, center.Y),
                                new Point(center.X - radius * 0.4, center.Y + radius * 2.2));
                    }
                }

                x += letterWidth;
            }
        }

        private static FormattedText Format(string text, Typeface typeface, double emSize, IBrush brush)
            => new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, emSize, brush);
    }
}
