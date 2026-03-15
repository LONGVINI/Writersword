using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.Generic;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Токен отображения горячей клавиши в подсказке.
    /// IsKey=true  — клавиша, отображается как бейдж.
    /// IsKey=false — разделитель хордов (стрелка).
    /// Объявлен рядом с TooltipView так как используется только здесь.
    /// </summary>
    public record HotKeyToken(string Text, bool IsKey);

    /// <summary>
    /// Визуальное представление кастомной подсказки.
    /// Поддерживает заголовок, описание, горячие клавиши и картинку/гифку.
    /// Все блоки кроме заголовка опциональны — скрываются если не заданы.
    /// Цвета передаются напрямую из TooltipBehavior чтобы корректно работать внутри PopupRoot.
    /// Горячие клавиши строятся программно в code-behind чтобы обойти ограничения биндинга в PopupRoot.
    /// </summary>
    public partial class TooltipView : UserControl
    {
        /// <summary>Заголовок подсказки — основной текст.</summary>
        public static readonly StyledProperty<string?> TitleProperty =
            AvaloniaProperty.Register<TooltipView, string?>(nameof(Title));

        /// <summary>
        /// Описание — дополнительный текст под заголовком.
        /// Если не задано — блок скрывается.
        /// </summary>
        public static readonly StyledProperty<string?> DescriptionProperty =
            AvaloniaProperty.Register<TooltipView, string?>(nameof(Description));

        /// <summary>
        /// Горячие клавиши в виде списка бинд-строк.
        /// При установке программно строит блок клавиш в HotKeysContainer.
        /// Должно устанавливаться после всех кистей.
        /// </summary>
        public static readonly StyledProperty<List<List<HotKeyToken>>?> ParsedHotKeysProperty =
            AvaloniaProperty.Register<TooltipView, List<List<HotKeyToken>>?>(nameof(ParsedHotKeys));

        /// <summary>
        /// Путь к картинке или гифке в формате avares://Assembly/Path/file.ext
        /// Поддерживает GIF (с анимацией), PNG, WebP, JPG.
        /// Если не задан — блок скрывается.
        /// </summary>
        public static readonly StyledProperty<string?> PreviewPathProperty =
            AvaloniaProperty.Register<TooltipView, string?>(nameof(PreviewPath));

        /// <summary>
        /// Управляет позицией треугольника по вертикали.
        /// true — треугольник снизу (подсказка над кнопкой).
        /// false — треугольник сверху (подсказка под кнопкой).
        /// </summary>
        public static readonly StyledProperty<bool> ArrowAtBottomProperty =
            AvaloniaProperty.Register<TooltipView, bool>(nameof(ArrowAtBottom), defaultValue: true);

        /// <summary>
        /// Горизонтальное смещение стрелки относительно левого края тултипа.
        /// Рассчитывается в TooltipBehavior чтобы стрелка указывала на центр кнопки.
        /// </summary>
        public static readonly StyledProperty<double> ArrowHorizontalOffsetProperty =
            AvaloniaProperty.Register<TooltipView, double>(nameof(ArrowHorizontalOffset), defaultValue: 0.0);

        /// <summary>
        /// Кисть фона подсказки — передаётся из TooltipBehavior.
        /// Резолвится из главного дерева ресурсов чтобы работать внутри PopupRoot.
        /// </summary>
        public static readonly StyledProperty<IBrush?> TooltipBackgroundProperty =
            AvaloniaProperty.Register<TooltipView, IBrush?>(nameof(TooltipBackground));

        /// <summary>Кисть границы подсказки — передаётся из TooltipBehavior.</summary>
        public static readonly StyledProperty<IBrush?> TooltipBorderBrushProperty =
            AvaloniaProperty.Register<TooltipView, IBrush?>(nameof(TooltipBorderBrush));

        /// <summary>Кисть текста подсказки — передаётся из TooltipBehavior.</summary>
        public static readonly StyledProperty<IBrush?> TooltipForegroundProperty =
            AvaloniaProperty.Register<TooltipView, IBrush?>(nameof(TooltipForeground));

        /// <summary>
        /// Кисть фона бейджа клавиши — передаётся из TooltipBehavior.
        /// Резолвится из ресурса AppTooltipKeyBackground.
        /// </summary>
        public static readonly StyledProperty<IBrush?> KeyBadgeBackgroundProperty =
            AvaloniaProperty.Register<TooltipView, IBrush?>(nameof(KeyBadgeBackground));

        /// <summary>Заголовок подсказки.</summary>
        public string? Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>Описание подсказки. Скрывается если не задано.</summary>
        public string? Description
        {
            get => GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        /// <summary>
        /// Токены горячих клавиш.
        /// При установке перестраивает HotKeysContainer программно.
        /// Должно устанавливаться после всех кистей.
        /// </summary>
        public List<List<HotKeyToken>>? ParsedHotKeys
        {
            get => GetValue(ParsedHotKeysProperty);
            set
            {
                SetValue(ParsedHotKeysProperty, value);
                RebuildHotKeys(value);
            }
        }

        /// <summary>Путь к картинке/гифке. Скрывается если не задан.</summary>
        public string? PreviewPath
        {
            get => GetValue(PreviewPathProperty);
            set => SetValue(PreviewPathProperty, value);
        }

        /// <summary>Позиция треугольника-стрелки по вертикали.</summary>
        public bool ArrowAtBottom
        {
            get => GetValue(ArrowAtBottomProperty);
            set => SetValue(ArrowAtBottomProperty, value);
        }

        /// <summary>Горизонтальное смещение стрелки.</summary>
        public double ArrowHorizontalOffset
        {
            get => GetValue(ArrowHorizontalOffsetProperty);
            set => SetValue(ArrowHorizontalOffsetProperty, value);
        }

        /// <summary>Кисть фона.</summary>
        public IBrush? TooltipBackground
        {
            get => GetValue(TooltipBackgroundProperty);
            set => SetValue(TooltipBackgroundProperty, value);
        }

        /// <summary>Кисть границы.</summary>
        public IBrush? TooltipBorderBrush
        {
            get => GetValue(TooltipBorderBrushProperty);
            set => SetValue(TooltipBorderBrushProperty, value);
        }

        /// <summary>Кисть текста.</summary>
        public IBrush? TooltipForeground
        {
            get => GetValue(TooltipForegroundProperty);
            set => SetValue(TooltipForegroundProperty, value);
        }

        /// <summary>Кисть фона бейджа клавиши.</summary>
        public IBrush? KeyBadgeBackground
        {
            get => GetValue(KeyBadgeBackgroundProperty);
            set => SetValue(KeyBadgeBackgroundProperty, value);
        }

        public TooltipView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Программно строит блок горячих клавиш в HotKeysContainer.
        /// Каждый бинд — отдельная строка горизонтальных бейджей.
        /// Между клавишами одного хорда добавляется символ +.
        /// Между хордами в последовательности добавляется стрелка.
        /// Обходит ограничение биндинга внутри PopupRoot.
        /// </summary>
        private void RebuildHotKeys(List<List<HotKeyToken>>? hotKeys)
        {
            var container = this.FindControl<StackPanel>("HotKeysContainer");
            if (container is null) return;

            container.Children.Clear();

            if (hotKeys is null || hotKeys.Count == 0)
            {
                container.IsVisible = false;
                return;
            }

            foreach (var binding in hotKeys)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };

                foreach (var token in binding)
                {
                    if (!token.IsKey) continue;

                    row.Children.Add(new Border
                    {
                        Background = KeyBadgeBackground,
                        BorderBrush = TooltipBorderBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(5, 2),
                        Child = new TextBlock
                        {
                            Text = token.Text,
                            Foreground = TooltipForeground,
                            FontSize = 10,
                            FontWeight = FontWeight.Medium,
                        }
                    });
                }

                container.Children.Add(row);
            }

            container.IsVisible = true;
        }
    }
}