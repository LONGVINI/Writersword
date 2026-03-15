using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Визуальное представление кастомной подсказки.
    /// Поддерживает заголовок, описание, горячую клавишу и картинку/гифку.
    /// Все блоки кроме заголовка опциональны — скрываются если не заданы.
    /// Цвета передаются напрямую из TooltipBehavior чтобы корректно работать внутри PopupRoot.
    /// Картинка передаётся как путь avares:// — поддерживает GIF, PNG, WebP, JPG.
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
        /// Горячая клавиша — отображается в правом нижнем углу в рамочке.
        /// Если не задана — блок скрывается.
        /// </summary>
        public static readonly StyledProperty<string?> HotKeyProperty =
            AvaloniaProperty.Register<TooltipView, string?>(nameof(HotKey));

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

        /// <summary>Горячая клавиша. Скрывается если не задана.</summary>
        public string? HotKey
        {
            get => GetValue(HotKeyProperty);
            set => SetValue(HotKeyProperty, value);
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

        public TooltipView()
        {
            InitializeComponent();
        }
    }
}