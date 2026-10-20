using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using System;
using System.IO;
using Writersword.Infrastructure.Behaviours;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Rendering.Charts;
using Writersword.Modules.TextEditor.Services;
using VerticalAlignment = Avalonia.Layout.VerticalAlignment;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// «Мои стили» — управление своими стилями диаграмм. Сверху — «Сохранить стиль этой
    /// диаграммы», ниже — свои стили строками: образец, имя (правится прямо в строке) и
    /// кнопки «Применить», «Обновить по диаграмме» и «Удалить».
    ///
    /// Стиль правится через диаграмму: его применяют, настраивают диаграмму в её окне
    /// (карточки «Надписи», «Фон», «Оформление») и нажимают «Обновить по диаграмме» —
    /// облик стиля заменяется нынешним обликом диаграммы, имя остаётся.
    /// </summary>
    public sealed class ChartUserStylesPanel : UserControl
    {
        private const double ChartWidthPt = 432;
        private const double ChartHeightPt = 252;
        private const double ThumbWidth = 84;

        private readonly Func<ChartModel?> _chart;

        /// <summary>Применить стиль к выделенной диаграмме.</summary>
        public event Action<ChartUserStyle>? ApplyRequested;

        /// <summary>Указатель на строке стиля; null — ушёл. Для предпросмотра на диаграмме.</summary>
        public event Action<ChartUserStyle?>? Hovered;

        public ChartUserStylesPanel(Func<ChartModel?> chart)
        {
            _chart = chart;
            MinWidth = 380;
            Rebuild();
        }

        private void Rebuild()
        {
            var chart = _chart();
            var styles = ChartUserStyles.Load();
            var root = new StackPanel { Spacing = 6 };

            var save = new Button
            {
                Classes = { "primary" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(10, 6),
                IsEnabled = chart is not null && styles.Count < ChartUserStyles.MaxStyles,
                Content = "Сохранить стиль этой диаграммы"
            };
            TooltipBehavior.SetTip(save, "Сохранить стиль этой диаграммы");
            TooltipBehavior.SetDescription(save, "Фон, область построения, шрифты и оформление надписей, рамка и сетка выделенной "
                + "диаграммы — новым своим стилем. Свои стили видны во всех проектах и в галерее «Стили».");
            save.Click += (_, _) =>
            {
                if (_chart() is { } current) ChartUserStyles.AddFrom(current);
                Rebuild();
            };
            root.Children.Add(save);

            if (styles.Count == 0)
            {
                root.Children.Add(Muted("Своих стилей пока нет. Настройте облик диаграммы в её окне и сохраните его кнопкой выше."));
            }
            else
            {
                foreach (var style in styles) root.Children.Add(StyleRow(style, chart));
                root.Children.Add(Muted("Изменить стиль: примените его, настройте диаграмму в её окне (карточки «Надписи», «Фон», "
                    + "«Оформление») и нажмите «Обновить по диаграмме». Имя правится прямо в строке."));
            }

            Content = root;
        }

        private Control StyleRow(ChartUserStyle style, ChartModel? chart)
        {
            var image = new Image
            {
                Width = ThumbWidth,
                Height = Math.Round(ThumbWidth * ChartHeightPt / ChartWidthPt),
                Stretch = Stretch.Uniform,
                Source = chart is null ? null : Thumbnail(style.ApplyTo(chart))
            };
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Child = image,
                VerticalAlignment = VerticalAlignment.Center
            };
            frame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderSubtleBrush"));

            var name = new TextBox
            {
                Text = style.Name,
                FontSize = 12,
                MinHeight = 26,
                Padding = new Thickness(6, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            TooltipBehavior.SetTip(name, "Имя стиля");
            string id = style.Id;
            void CommitName()
            {
                string text = name.Text?.Trim() ?? string.Empty;
                if (text.Length == 0) { name.Text = style.Name; return; }
                if (text != style.Name)
                {
                    ChartUserStyles.Rename(id, text);
                    style.Name = text;
                }
            }
            name.LostFocus += (_, _) => CommitName();
            name.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitName(); e.Handled = true; }
                else if (e.Key == Key.Escape) { name.Text = style.Name; e.Handled = true; }
            };

            var apply = SmallButton("Применить", "Применить стиль к выделенной диаграмме", chart is not null);
            apply.Click += (_, _) => ApplyRequested?.Invoke(style);

            var update = SmallButton("Обновить по диаграмме", "Заменить облик стиля нынешним обликом выделенной диаграммы. Имя останется.", chart is not null);
            update.Click += (_, _) =>
            {
                if (_chart() is { } current) ChartUserStyles.UpdateFrom(id, current);
                Rebuild();
            };

            var delete = SmallButton("Удалить", "Удалить свой стиль", true);
            delete.Click += (_, _) =>
            {
                Hovered?.Invoke(null);
                ChartUserStyles.Delete(id);
                Rebuild();
            };

            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
            buttons.Children.Add(apply);
            buttons.Children.Add(update);
            buttons.Children.Add(delete);

            var right = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(name);
            right.Children.Add(buttons);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Background = Brushes.Transparent };
            Grid.SetColumn(frame, 0);
            Grid.SetColumn(right, 1);
            row.Children.Add(frame);
            row.Children.Add(right);
            row.PointerEntered += (_, _) => Hovered?.Invoke(style);
            row.PointerExited += (_, _) => Hovered?.Invoke(null);

            var card = new Border
            {
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = row
            };
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderSubtleBrush"));
            return card;
        }

        private static Button SmallButton(string text, string tip, bool enabled)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 11,
                Padding = new Thickness(8, 3),
                MinHeight = 0,
                IsEnabled = enabled,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            TooltipBehavior.SetTip(button, text);
            TooltipBehavior.SetDescription(button, tip);
            return button;
        }

        private TextBlock Muted(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(2, 2, 2, 0) };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextMutedBrush"));
            return label;
        }

        private static Bitmap? Thumbnail(ChartModel chart)
        {
            try
            {
                int pixelWidth = (int)Math.Ceiling(ThumbWidth * 2);
                int pixelHeight = (int)Math.Ceiling(ThumbWidth * ChartHeightPt / ChartWidthPt * 2);
                using var bitmap = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.White);
                    canvas.Scale((float)(pixelWidth / ChartWidthPt), (float)(pixelHeight / ChartHeightPt));
                    ChartRenderer.Draw(canvas, chart, new SKRect(0, 0, (float)ChartWidthPt, (float)ChartHeightPt));
                    canvas.Flush();
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = new MemoryStream(data.ToArray());
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }
    }
}
