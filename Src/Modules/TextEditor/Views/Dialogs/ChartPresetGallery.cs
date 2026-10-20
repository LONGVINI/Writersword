using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Writersword.Infrastructure.Behaviours;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Rendering.Charts;
using Writersword.Modules.TextEditor.Services;
using VerticalAlignment = Avalonia.Layout.VerticalAlignment;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Свои пресеты галереи — стили или макеты: где они хранятся и как называются.
    /// Галерея показывает их ниже готовых и даёт сохранить, обновить, переименовать
    /// и удалить.
    /// </summary>
    public sealed class ChartUserPresetSource
    {
        /// <summary>Заголовок раздела: «Мои стили», «Мои макеты».</summary>
        public required string Header { get; init; }

        /// <summary>Подпись плитки сохранения: «Сохранить стиль».</summary>
        public required string SaveCaption { get; init; }

        /// <summary>Пояснение к плитке сохранения: что именно снимается с диаграммы.</summary>
        public required string SaveDescription { get; init; }

        /// <summary>Пояснение к «Обновить по этой диаграмме».</summary>
        public required string UpdateDescription { get; init; }

        public required int Max { get; init; }
        public required Func<IReadOnlyList<(string Id, ChartPresets.Preset Preset)>> Load { get; init; }
        public required Action<ChartModel> Add { get; init; }
        public required Action<string, ChartModel> Update { get; init; }
        public required Action<string> Delete { get; init; }
        public required Action<string, string> Rename { get; init; }

        public static ChartUserPresetSource Styles { get; } = new()
        {
            Header = "Мои стили",
            SaveCaption = "Сохранить стиль",
            SaveDescription = "Фон, область построения, шрифты и оформление надписей, рамка и сетка этой диаграммы — "
                + "новым своим стилем. Свои стили видны во всех проектах.",
            UpdateDescription = "Заменить облик стиля нынешним обликом этой диаграммы. Имя останется.",
            Max = ChartUserStyles.MaxStyles,
            Load = () => ChartUserStyles.Load().Select(s => (s.Id, s.ToPreset())).ToList(),
            Add = chart => ChartUserStyles.AddFrom(chart),
            Update = ChartUserStyles.UpdateFrom,
            Delete = ChartUserStyles.Delete,
            Rename = ChartUserStyles.Rename
        };

        public static ChartUserPresetSource Layouts { get; } = new()
        {
            Header = "Мои макеты",
            SaveCaption = "Сохранить макет",
            SaveDescription = "Состав и расположение элементов этой диаграммы — название, легенда, подписи значений, "
                + "названия осей, сетка и таблица данных — новым своим макетом. Свои макеты видны во всех проектах.",
            UpdateDescription = "Заменить расположение элементов макета нынешним расположением этой диаграммы. Имя останется.",
            Max = ChartUserLayouts.MaxLayouts,
            Load = () => ChartUserLayouts.Load().Select(l => (l.Id, l.ToPreset())).ToList(),
            Add = chart => ChartUserLayouts.AddFrom(chart),
            Update = ChartUserLayouts.UpdateFrom,
            Delete = ChartUserLayouts.Delete,
            Rename = ChartUserLayouts.Rename
        };
    }

    /// <summary>
    /// Галерея стилей или экспресс-макетов диаграммы: каждый образец — уменьшенная
    /// сама диаграмма с этим стилем или макетом, так что видно, что получится.
    ///
    /// Наведение на образец сообщает о нём (<see cref="Hovered"/>): диаграмма в документе
    /// показывает его, пока указатель на образце. Щелчок — <see cref="Chosen"/>.
    ///
    /// Ниже готовых — свои образцы и плитка «Сохранить…», которая снимает облик или
    /// расположение с этой диаграммы. У своего образца при наведении — кнопка «⋯»
    /// (то же меню по правой кнопке): обновить по этой диаграмме, переименовать, удалить.
    /// </summary>
    public sealed class ChartPresetGallery : UserControl
    {
        private const double ChartWidthPt = 432;
        private const double ChartHeightPt = 252;

        private const string PlusIcon = "M11 5h2v6h6v2h-6v6h-2v-6H5v-2h6z";
        private const string MoreIcon = "M6 10.5a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3zm6 0a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3zm6 0a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3z";

        private readonly ChartModel _chart;
        private readonly IReadOnlyList<ChartPresets.Preset> _presets;
        private readonly int _columns;
        private readonly double _thumbWidth;
        private readonly double _thumbHeight;
        private readonly ChartUserPresetSource? _user;

        /// <summary>Выбран образец.</summary>
        public event Action<ChartPresets.Preset>? Chosen;

        /// <summary>Указатель на образце; null — ушёл с образцов.</summary>
        public event Action<ChartPresets.Preset?>? Hovered;

        public ChartPresetGallery(ChartModel chart, IReadOnlyList<ChartPresets.Preset> presets, int columns, double thumbWidth,
            ChartUserPresetSource? user = null)
        {
            _chart = chart;
            _presets = presets;
            _columns = columns;
            _thumbWidth = thumbWidth;
            _thumbHeight = Math.Round(thumbWidth * ChartHeightPt / ChartWidthPt);
            _user = user;
            Rebuild();
        }

        private double PanelWidth => _columns * (_thumbWidth + 14);

        private void Rebuild()
        {
            var root = new StackPanel { Spacing = 2 };
            var builtIn = NewPanel();
            foreach (var preset in _presets) builtIn.Children.Add(Tile(preset, null));
            root.Children.Add(builtIn);

            if (_user is { } user)
            {
                root.Children.Add(Header(user.Header));
                var own = user.Load();
                var panel = NewPanel();
                foreach (var (id, preset) in own) panel.Children.Add(Tile(preset, id));
                panel.Children.Add(SaveTile(user, own.Count < user.Max));
                root.Children.Add(panel);
            }

            Content = root;
        }

        private WrapPanel NewPanel() => new() { Orientation = Orientation.Horizontal, Width = PanelWidth };

        private Control Header(string text)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextMutedBrush"));

            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) };
            line.Bind(Border.BackgroundProperty, this.GetResourceObservable("BorderSubtleBrush"));

            var row = new DockPanel { Margin = new Thickness(6, 8, 6, 2), Width = PanelWidth - 12, HorizontalAlignment = HorizontalAlignment.Left };
            DockPanel.SetDock(label, Avalonia.Controls.Dock.Left);
            row.Children.Add(label);
            row.Children.Add(line);
            return row;
        }

        private Border Frame(Control child)
        {
            var frame = new Border
            {
                Width = _thumbWidth + 2,
                Height = _thumbHeight + 2,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Child = child
            };
            frame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderSubtleBrush"));
            return frame;
        }

        private TextBlock Caption(string text)
        {
            var caption = new TextBlock
            {
                Text = text,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = _thumbWidth
            };
            caption.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));
            return caption;
        }

        private Button TileButton(Control content) => new()
        {
            Classes = { "ghost" },
            Content = content,
            Padding = new Thickness(4),
            Margin = new Thickness(2),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Top
        };

        /// <summary>Образец пресета; id — у своего образца: тогда у него кнопка «⋯» и меню по правой кнопке.</summary>
        private Button Tile(ChartPresets.Preset preset, string? id)
        {
            var image = new Image
            {
                Width = _thumbWidth,
                Height = _thumbHeight,
                Stretch = Stretch.Uniform,
                Source = Thumbnail(preset.Apply(_chart), _thumbWidth, _thumbHeight)
            };
            var frameHost = new Grid();
            frameHost.Children.Add(Frame(image));

            var caption = Caption(preset.Name);
            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(frameHost);
            content.Children.Add(caption);

            var button = TileButton(content);
            TooltipBehavior.SetTip(button, preset.Name);
            TooltipBehavior.SetDescription(button, preset.Description);
            button.Click += (_, _) => Chosen?.Invoke(preset);
            button.PointerEntered += (_, _) => Hovered?.Invoke(preset);
            button.PointerExited += (_, _) => Hovered?.Invoke(null);

            if (id is not null && _user is { } user)
            {
                var more = new Button
                {
                    Width = 22,
                    Height = 18,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 4, 0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Opacity = 0,
                    Content = new PathIcon { Width = 12, Height = 12, Data = StreamGeometry.Parse(MoreIcon) }
                };
                more.Bind(Button.BackgroundProperty, this.GetResourceObservable("BgSurfaceBrush"));
                more.Bind(Button.BorderBrushProperty, this.GetResourceObservable("BorderSubtleBrush"));
                more.Bind(Button.ForegroundProperty, this.GetResourceObservable("TextPrimaryBrush"));
                TooltipBehavior.SetTip(more, "Изменить");
                TooltipBehavior.SetDescription(more, "Обновить по этой диаграмме, переименовать или удалить");
                frameHost.Children.Add(more);

                button.PointerEntered += (_, _) => more.Opacity = 1;
                button.PointerExited += (_, _) => more.Opacity = 0;
                more.Click += (_, e) =>
                {
                    e.Handled = true;
                    ShowUserMenu(more, false, user, id, content, caption);
                };
                button.ContextRequested += (_, e) =>
                {
                    e.Handled = true;
                    ShowUserMenu(button, true, user, id, content, caption);
                };
            }

            return button;
        }

        /// <summary>Плитка «Сохранить…»: облик или расположение этой диаграммы — новым своим образцом.</summary>
        private Button SaveTile(ChartUserPresetSource user, bool enabled)
        {
            var plus = new PathIcon
            {
                Width = 22,
                Height = 22,
                Data = StreamGeometry.Parse(PlusIcon),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            plus.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("AccentDefaultBrush"));
            var frame = Frame(plus);
            frame.Bind(Border.BackgroundProperty, this.GetResourceObservable("BgSurfaceBrush"));

            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(frame);
            content.Children.Add(Caption(user.SaveCaption));

            var button = TileButton(content);
            button.IsEnabled = enabled;
            TooltipBehavior.SetTip(button, user.SaveCaption + " этой диаграммы");
            TooltipBehavior.SetDescription(button, user.SaveDescription);
            button.Click += (_, _) =>
            {
                user.Add(_chart);
                Rebuild();
            };
            return button;
        }

        private void ShowUserMenu(Control anchor, bool atPointer, ChartUserPresetSource user, string id, StackPanel content, TextBlock caption)
        {
            var menu = new MenuFlyout { Placement = atPointer ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedRight };

            var update = new MenuItem { Header = "Обновить по этой диаграмме" };
            TooltipBehavior.SetTip(update, "Обновить по этой диаграмме");
            TooltipBehavior.SetDescription(update, user.UpdateDescription);
            update.Click += (_, _) =>
            {
                Hovered?.Invoke(null);
                user.Update(id, _chart);
                Rebuild();
            };

            var rename = new MenuItem { Header = "Переименовать" };
            rename.Click += (_, _) => BeginRename(content, caption, user, id);

            var delete = new MenuItem { Header = "Удалить" };
            delete.Click += (_, _) =>
            {
                Hovered?.Invoke(null);
                user.Delete(id);
                Rebuild();
            };

            menu.Items.Add(update);
            menu.Items.Add(rename);
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
            menu.ShowAt(anchor, showAtPointer: atPointer);
        }

        /// <summary>Имя своего образца правится прямо под ним: Enter — сохранить, Esc — отказаться.</summary>
        private void BeginRename(StackPanel content, TextBlock caption, ChartUserPresetSource user, string id)
        {
            var box = new TextBox
            {
                Text = caption.Text,
                FontSize = 11,
                MinHeight = 0,
                Padding = new Thickness(4, 2),
                MaxWidth = _thumbWidth
            };
            int index = content.Children.IndexOf(caption);
            if (index < 0) return;
            content.Children[index] = box;

            bool done = false;
            void Finish(bool commit)
            {
                if (done) return;
                done = true;
                if (commit) user.Rename(id, box.Text ?? string.Empty);
                Rebuild();
            }

            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Finish(commit: true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Finish(commit: false); e.Handled = true; }
            };
            box.LostFocus += (_, _) => Finish(commit: true);
            box.AttachedToVisualTree += (_, _) =>
            {
                box.Focus();
                box.SelectAll();
            };
        }

        /// <summary>
        /// Образец: диаграмма в пунктах, уменьшенная до размера образца, в двойном
        /// разрешении — на экранах с увеличением он не мылится.
        /// </summary>
        private static Bitmap? Thumbnail(ChartModel chart, double width, double height)
        {
            try
            {
                int pixelWidth = (int)Math.Ceiling(width * 2);
                int pixelHeight = (int)Math.Ceiling(height * 2);
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
