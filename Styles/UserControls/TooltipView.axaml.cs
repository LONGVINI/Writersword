using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Text;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// ����� ����������� ������� ������� � ���������.
    /// IsKey=true  � �������, ������������ ��� �����.
    /// IsKey=false � ����������� ������ (�������).
    /// �������� ����� � TooltipView ��� ��� ������������ ������ �����.
    /// </summary>
    public record HotKeyToken(string Text, bool IsKey);

    /// <summary>
    /// ���������� ������������� ��������� ���������.
    /// ������������ ���������, ��������, ������� ������� � ��������/�����.
    /// ��� ����� ����� ��������� ����������� � ���������� ���� �� ������.
    /// ����� ���������� �������� �� TooltipBehavior ����� ��������� �������� ������ PopupRoot.
    /// ������� ������� �������� ���������� � code-behind ����� ������ ����������� �������� � PopupRoot.
    /// </summary>
    public partial class TooltipView : UserControl
    {
        /// <summary>��������� ��������� � �������� �����.</summary>
        public static readonly StyledProperty<string?> TitleProperty =
            AvaloniaProperty.Register<TooltipView, string?>(nameof(Title));

        /// <summary>
        /// �������� � �������������� ����� ��� ����������.
        /// ���� �� ������ � ���� ����������.
        /// </summary>
        public static readonly StyledProperty<string?> DescriptionProperty =
            AvaloniaProperty.Register<TooltipView, string?>(nameof(Description));

        /// <summary>
        /// ������� ������� � ���� ������ ����-�����.
        /// ��� ��������� ���������� ������ ���� ������ � HotKeysContainer.
        /// ������ ��������������� ����� ���� ������.
        /// </summary>
        public static readonly StyledProperty<List<List<HotKeyToken>>?> ParsedHotKeysProperty =
            AvaloniaProperty.Register<TooltipView, List<List<HotKeyToken>>?>(nameof(ParsedHotKeys));

        /// <summary>
        /// ���� � �������� ��� ����� � ������� avares://Assembly/Path/file.ext
        /// ������������ GIF (� ���������), PNG, WebP, JPG.
        /// ���� �� ����� � ���� ����������.
        /// </summary>
        public static readonly StyledProperty<string?> PreviewPathProperty =
            AvaloniaProperty.Register<TooltipView, string?>(nameof(PreviewPath));

        /// <summary>
        /// ��������� �������� ������������ �� ���������.
        /// true � ����������� ����� (��������� ��� �������).
        /// false � ����������� ������ (��������� ��� �������).
        /// </summary>
        public static readonly StyledProperty<bool> ArrowAtBottomProperty =
            AvaloniaProperty.Register<TooltipView, bool>(nameof(ArrowAtBottom), defaultValue: true);

        /// <summary>
        /// �������������� �������� ������� ������������ ������ ���� �������.
        /// �������������� � TooltipBehavior ����� ������� ��������� �� ����� ������.
        /// </summary>
        public static readonly StyledProperty<double> ArrowHorizontalOffsetProperty =
            AvaloniaProperty.Register<TooltipView, double>(nameof(ArrowHorizontalOffset), defaultValue: 0.0);

        /// <summary>
        /// ����� ���� ��������� � ��������� �� TooltipBehavior.
        /// ���������� �� �������� ������ �������� ����� �������� ������ PopupRoot.
        /// </summary>
        public static readonly StyledProperty<IBrush?> TooltipBackgroundProperty =
            AvaloniaProperty.Register<TooltipView, IBrush?>(nameof(TooltipBackground));

        /// <summary>����� ������� ��������� � ��������� �� TooltipBehavior.</summary>
        public static readonly StyledProperty<IBrush?> TooltipBorderBrushProperty =
            AvaloniaProperty.Register<TooltipView, IBrush?>(nameof(TooltipBorderBrush));

        /// <summary>����� ������ ��������� � ��������� �� TooltipBehavior.</summary>
        public static readonly StyledProperty<IBrush?> TooltipForegroundProperty =
            AvaloniaProperty.Register<TooltipView, IBrush?>(nameof(TooltipForeground));

        /// <summary>
        /// ����� ���� ������ ������� � ��������� �� TooltipBehavior.
        /// ���������� �� ������� AppTooltipKeyBackground.
        /// </summary>
        public static readonly StyledProperty<IBrush?> KeyBadgeBackgroundProperty =
            AvaloniaProperty.Register<TooltipView, IBrush?>(nameof(KeyBadgeBackground));

        /// <summary>��������� ���������.</summary>
        public string? Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>�������� ���������. ���������� ���� �� ������.</summary>
        public string? Description
        {
            get => GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        /// <summary>
        /// ������ ������� ������.
        /// ��� ��������� ������������� HotKeysContainer ����������.
        /// ������ ��������������� ����� ���� ������.
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

        /// <summary>���� � ��������/�����. ���������� ���� �� �����.</summary>
        public string? PreviewPath
        {
            get => GetValue(PreviewPathProperty);
            set => SetValue(PreviewPathProperty, value);
        }

        /// <summary>������� ������������-������� �� ���������.</summary>
        public bool ArrowAtBottom
        {
            get => GetValue(ArrowAtBottomProperty);
            set => SetValue(ArrowAtBottomProperty, value);
        }

        /// <summary>�������������� �������� �������.</summary>
        public double ArrowHorizontalOffset
        {
            get => GetValue(ArrowHorizontalOffsetProperty);
            set => SetValue(ArrowHorizontalOffsetProperty, value);
        }

        /// <summary>����� ����.</summary>
        public IBrush? TooltipBackground
        {
            get => GetValue(TooltipBackgroundProperty);
            set => SetValue(TooltipBackgroundProperty, value);
        }

        /// <summary>����� �������.</summary>
        public IBrush? TooltipBorderBrush
        {
            get => GetValue(TooltipBorderBrushProperty);
            set => SetValue(TooltipBorderBrushProperty, value);
        }

        /// <summary>����� ������.</summary>
        public IBrush? TooltipForeground
        {
            get => GetValue(TooltipForegroundProperty);
            set => SetValue(TooltipForegroundProperty, value);
        }

        /// <summary>����� ���� ������ �������.</summary>
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
        /// Пересобирает описание при изменении текста.
        /// </summary>
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == DescriptionProperty)
                RebuildDescription(change.GetNewValue<string?>());
        }

        /// <summary>
        /// Собирает описание из простой разметки.
        ///
        /// Поддерживается три приёма:
        ///   *текст*                     — выделение акцентным цветом и полужирным;
        ///   [img:avares://.../file.png] — картинка прямо в строке, ростом со строку;
        ///   перенос строки              — обычный \n в ресурсной строке.
        ///
        /// Раньше выделение приходилось писать вручную вложенными Run прямо
        /// в разметке окна, из-за чего текст не переводился и повторялся
        /// в каждом месте. Теперь оформление живёт в самой строке.
        ///
        /// Картинка в строке отличается от <see cref="PreviewPath"/> назначением: то —
        /// один большой снимок над всем текстом, а это значок при своей строчке. Там,
        /// где перечисляют варианты — типы табуляции, виды заливки, — большая картинка
        /// не годится: показать один вариант из четырёх значит не показать ничего.
        ///
        /// Разбор идёт посимвольно, а не разбиением по звёздочкам, как было: картинки
        /// рвут строку на куски, и парность выделений считалась бы в каждом куске заново.
        /// </summary>
        private void RebuildDescription(string? text)
        {
            var target = this.FindControl<TextBlock>("DescriptionText");
            if (target is null) return;

            target.Inlines?.Clear();

            if (string.IsNullOrEmpty(text))
                return;

            bool accent = false;
            var buffer = new StringBuilder();

            void Flush()
            {
                if (buffer.Length == 0) return;

                var run = new Run(buffer.ToString());

                if (accent)
                {
                    // Цвет берётся привязкой, а не разовым поиском ресурса:
                    // подсказка живёт в попапе и в момент сборки текста ещё
                    // не подключена к дереву ресурсов — поиск возвращал пусто,
                    // и выделение красилось запасным цветом.
                    run[!TextElement.ForegroundProperty] =
                        new DynamicResourceExtension("AccentDefaultBrush");

                    run.FontWeight = FontWeight.SemiBold;
                }

                target.Inlines?.Add(run);
                buffer.Clear();
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '*')
                {
                    Flush();
                    accent = !accent;
                    continue;
                }

                if (text[i] == '[' && TryReadImageMarker(text, i, out string path, out int close))
                {
                    Flush();

                    var image = BuildInlineImage(path);
                    if (image is not null)
                        target.Inlines?.Add(new InlineUIContainer(image));

                    i = close;
                    continue;
                }

                buffer.Append(text[i]);
            }

            Flush();
        }

        private const string ImageMarkerPrefix = "[img:";

        /// <summary>Высота картинки в строке. Чуть больше кегля описания, чтобы значок читался.</summary>
        private const double InlineImageHeight = 14.0;

        /// <summary>
        /// Разбирает метку картинки, начинающуюся в позиции start. Возвращает путь и место
        /// закрывающей скобки. Незакрытая метка картинкой не считается и остаётся текстом:
        /// квадратная скобка — обычный знак, и обрывать на ней описание нельзя.
        /// </summary>
        private static bool TryReadImageMarker(string text, int start, out string path, out int close)
        {
            path = string.Empty;
            close = start;

            if (start + ImageMarkerPrefix.Length >= text.Length) return false;

            if (string.CompareOrdinal(text, start, ImageMarkerPrefix, 0, ImageMarkerPrefix.Length) != 0)
                return false;

            int end = text.IndexOf(']', start + ImageMarkerPrefix.Length);
            if (end < 0) return false;

            path = text.Substring(
                start + ImageMarkerPrefix.Length,
                end - start - ImageMarkerPrefix.Length);

            close = end;
            return path.Length > 0;
        }

        /// <summary>
        /// Картинки строки. Читаются один раз на путь: подсказка пересобирается на каждое
        /// наведение, и открывать ресурс заново значит лезть в сборку десятки раз за минуту
        /// ради одного и того же значка. Промах тоже запоминается — иначе отсутствующая
        /// картинка искалась бы вечно.
        /// </summary>
        private static readonly Dictionary<string, Bitmap?> _inlineImages = new();

        private static Control? BuildInlineImage(string path)
        {
            Bitmap? bitmap;

            lock (_inlineImages)
            {
                if (!_inlineImages.TryGetValue(path, out bitmap))
                {
                    bitmap = LoadInlineBitmap(path);
                    _inlineImages[path] = bitmap;
                }
            }

            if (bitmap is null) return null;

            return new Image
            {
                Source = bitmap,
                Height = InlineImageHeight,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, -3)
            };
        }

        private static Bitmap? LoadInlineBitmap(string path)
        {
            try
            {
                var uri = new Uri(path);
                if (!AssetLoader.Exists(uri)) return null;

                using var stream = AssetLoader.Open(uri);
                return new Bitmap(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// ���������� ������ ���� ������� ������ � HotKeysContainer.
        /// ������ ���� � ��������� ������ �������������� �������.
        /// ����� ��������� ������ ����� ����������� ������ +.
        /// ����� ������� � ������������������ ����������� �������.
        /// ������� ����������� �������� ������ PopupRoot.
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