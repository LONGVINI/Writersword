using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI;
using Serilog;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Views
{
    /// <summary>Вариант значка в наборе редактора метки.</summary>
    public sealed class LabelIconOption : ReactiveObject
    {
        public string Key { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        /// <summary>
        /// Метка-образец для показа этого варианта. Набор рисуется тем же
        /// контролом, что и сама метка на карточках: иначе значок в наборе и
        /// значок на карточке снова разъедутся, как это уже было.
        ///
        /// Подложки у образца нет — она к выбору фигуры отношения не имеет
        /// и только мешала бы разглядеть очертание.
        /// </summary>
        public CharacterLabel Sample { get; } = new();

        public LabelIconOption(string key)
        {
            Key = key;
            Sample.Icon = key;
            Sample.ShowBackdrop = false;
        }

        /// <summary>Показать набор в текущем цвете фигуры.</summary>
        public void SetColor(string color)
        {
            Sample.IconColor = color;
            this.RaisePropertyChanged(nameof(Sample));
        }
    }

    /// <summary>
    /// Черновик метки: правки живут здесь и применяются к персонажу только
    /// по OK. «Отмена»/крестик закрывают окно без изменений.
    /// </summary>
    public sealed class LabelEditorDraft : ReactiveObject
    {
        private string _name = string.Empty;
        public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

        private string _icon = CharacterLabelIcons.Dot;
        public string Icon
        {
            get => _icon;
            set
            {
                this.RaiseAndSetIfChanged(ref _icon, value);
                RaisePreviewChanged();
            }
        }

        private string _color = "#607D8B";
        public string Color
        {
            get => _color;
            set
            {
                this.RaiseAndSetIfChanged(ref _color, value);
                RaisePreviewChanged();
            }
        }

        /// <summary>
        /// Цвет фигуры. В черновике всегда непустой — редактору цвета
        /// нечего показывать в пустом поле, — а в метку белый уезжает
        /// пустотой: см. IconColorOrEmpty.
        /// </summary>
        private string _iconColor = DefaultIconColor;
        public string IconColor
        {
            get => _iconColor;
            set
            {
                this.RaiseAndSetIfChanged(ref _iconColor, value);
                // Набор встроенных значков показывается без кружка, в цвете
                // фигуры: выбирают там именно очертание.
                foreach (var option in Icons) option.SetColor(value);
                RaisePreviewChanged();
            }
        }

        /// <summary>
        /// Записать вид в реестр проекта и разнести по всем персонажам с
        /// этой же меткой. По умолчанию выключено: правка метки у одного
        /// персонажа не должна менять её у остальных — «Ранен» с каплей у
        /// одного и с крестом у другого это законно.
        /// </summary>
        private bool _applyToAll;
        public bool ApplyToAll
        {
            get => _applyToAll;
            set => this.RaiseAndSetIfChanged(ref _applyToAll, value);
        }

        /// <summary>Рисовать кружок под фигурой.</summary>
        private bool _showBackdrop = true;
        public bool ShowBackdrop
        {
            get => _showBackdrop;
            set
            {
                this.RaiseAndSetIfChanged(ref _showBackdrop, value);
                RaisePreviewChanged();
            }
        }

        public const string DefaultIconColor = "#FFFFFF";

        /// <summary>
        /// Белый — вид по умолчанию, и в метке он хранится пустотой: иначе
        /// метка, у которой цвет фигуры никто не трогал, отличалась бы от
        /// метки, где белый выбрали руками.
        /// </summary>
        public string? IconColorOrEmpty =>
            string.Equals(_iconColor, DefaultIconColor, StringComparison.OrdinalIgnoreCase)
                ? null
                : _iconColor;

        private bool _dim;
        public bool Dim { get => _dim; set => this.RaiseAndSetIfChanged(ref _dim, value); }

        private bool _showOnCard = true;
        public bool ShowOnCard { get => _showOnCard; set => this.RaiseAndSetIfChanged(ref _showOnCard, value); }

        private string _description = string.Empty;
        public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }

        /// <summary>
        /// Своя картинка вместо встроенного значка. Ключ значка при этом
        /// не теряется: убрал картинку — вернулся прежний значок.
        /// </summary>
        private string? _iconImage;
        public string? IconImage
        {
            get => _iconImage;
            set
            {
                this.RaiseAndSetIfChanged(ref _iconImage, value);
                this.RaisePropertyChanged(nameof(HasCustomIcon));
                RaisePreviewChanged();
            }
        }

        public bool HasCustomIcon => !string.IsNullOrWhiteSpace(_iconImage);

        public ObservableCollection<LabelIconOption> Icons { get; } = new();

        /// <summary>
        /// Метка в текущем состоянии черновика — для превью. Превью рисуется
        /// тем же контролом, что и значок на карточке, поэтому показывает
        /// ровно то, что получится: и цвет фигуры, и подложку, и свою
        /// картинку, включая перекрашенный вектор.
        /// </summary>
        public CharacterLabel Preview => new()
        {
            Name = Name,
            Icon = Icon,
            Color = Color,
            IconImage = IconImage,
            IconColor = IconColorOrEmpty,
            ShowBackdrop = ShowBackdrop
        };

        private void RaisePreviewChanged() => this.RaisePropertyChanged(nameof(Preview));
    }

    /// <summary>
    /// Окно создания/редактирования метки персонажа: по центру модуля, со
    /// скримом — как окно настроек карточки. Применение — колбэком, чтобы
    /// окно не знало о вьюмоделях вкладок.
    /// </summary>
    public partial class LabelEditorOverlay : UserControl
    {
        private static readonly string[] IconSet =
        {
            CharacterLabelIcons.Dot,
            CharacterLabelIcons.Cross,
            CharacterLabelIcons.Skull,
            CharacterLabelIcons.Drop,
            CharacterLabelIcons.Star,
            CharacterLabelIcons.Crown,
            CharacterLabelIcons.Heart,
            CharacterLabelIcons.Flag,
            CharacterLabelIcons.Lock,
            CharacterLabelIcons.Bolt,
            CharacterLabelIcons.Eye,
            CharacterLabelIcons.Shield,
            CharacterLabelIcons.Moon,
            CharacterLabelIcons.Check
        };

        // Растровые форматы плюс вектор. Вектор перекрашивается в цвет метки,
        // растр идёт как есть: перекрашивать чужой герб программа не берётся.
        private static readonly FilePickerFileType IconFileType = new("Картинки значка")
        {
            Patterns = new[]
            {
                "*.png", "*.jpg", "*.jpeg", "*.webp",
                "*.bmp", "*.gif", "*.ico", "*.svg"
            }
        };

        private static readonly ILogger _logger = Log.ForContext<LabelEditorOverlay>();

        private LabelEditorDraft? _draft;
        private CharacterLabel? _original;
        private Action<CharacterLabel, bool>? _apply;

        public LabelEditorOverlay()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Показать редактор. label == null — создание новой метки;
        /// иначе — правка существующей (Id и Order сохраняются).
        /// Колбэк apply вызывается по OK с готовой меткой и признаком
        /// «сделать вид общим для всех персонажей с этой меткой».
        /// </summary>
        public void ShowFor(CharacterLabel? label, Action<CharacterLabel, bool> apply)
        {
            _original = label;
            _apply = apply;
            _draft = new LabelEditorDraft
            {
                Name = label?.Name ?? string.Empty,
                Icon = label?.Icon ?? CharacterLabelIcons.Dot,
                Color = label?.Color ?? "#607D8B",
                Dim = label?.Effect == CharacterLabelEffect.Dim,
                ShowOnCard = label?.ShowOnCard ?? true,
                Description = label?.Description ?? string.Empty,
                IconImage = label?.IconImage,
                IconColor = string.IsNullOrWhiteSpace(label?.IconColor)
                    ? LabelEditorDraft.DefaultIconColor
                    : label!.IconColor!,
                ShowBackdrop = label?.ShowBackdrop ?? true
            };

            foreach (var key in IconSet)
            {
                var option = new LabelIconOption(key) { IsSelected = key == _draft.Icon };
                option.SetColor(_draft.IconColor);
                _draft.Icons.Add(option);
            }

            DataContext = _draft;
            IsVisible = true;
        }

        private void CloseOverlay()
        {
            IsVisible = false;
            DataContext = null;
            _draft = null;
            _original = null;
            _apply = null;
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            if (_draft is not null && _apply is not null
                && !string.IsNullOrWhiteSpace(_draft.Name))
            {
                var result = new CharacterLabel
                {
                    Id = _original?.Id ?? Guid.NewGuid().ToString(),
                    Name = _draft.Name.Trim(),
                    Icon = _draft.Icon,
                    Color = _draft.Color,
                    Effect = _draft.Dim ? CharacterLabelEffect.Dim : CharacterLabelEffect.None,
                    ShowOnCard = _draft.ShowOnCard,
                    Order = _original?.Order ?? int.MaxValue,
                    Description = _draft.Description.Trim(),
                    IconImage = _draft.IconImage,
                    IconColor = _draft.IconColorOrEmpty,
                    ShowBackdrop = _draft.ShowBackdrop
                };
                _apply(result, _draft.ApplyToAll);
            }
            CloseOverlay();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => CloseOverlay();

        /// <summary>
        /// Сервис хранения картинок. Ставится модулем при инициализации —
        /// окно живёт поверх модуля и своих зависимостей не получает.
        /// </summary>
        public static Interfaces.ICharacterAvatarService? AvatarService { get; set; }

        // Своя картинка вместо встроенного значка: герб дома, эмблема клуба,
        // нарисованный автором знак. Встроенный набор из тридцати иконок всё
        // равно не покроет чужую выдумку.
        private async void OnPickIconImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_draft == null || AvatarService == null) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Картинка метки",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { IconFileType }
                });

                if (files == null || files.Count == 0) return;

                await using var stream = await files[0].OpenReadAsync();
                using var buffer = new System.IO.MemoryStream();
                await stream.CopyToAsync(buffer);

                // Значок сохраняется отдельным методом от аватара: у значков
                // шире список форматов, и вектор проходит только здесь.
                var imageRef = await AvatarService.SaveIconToProjectAsync(buffer.ToArray(), files[0].Name);
                if (!string.IsNullOrEmpty(imageRef)) _draft.IconImage = imageRef;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Label icon pick failed");
            }
        }

        // Убрать картинку — вернётся встроенный значок, ключ которого всё это
        // время сохранялся.
        private void OnClearIconImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_draft != null) _draft.IconImage = null;
        }

        // Скрим блокирует модуль, но окно не закрывает — как в остальных
        // оверлеях модуля.
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        // Выбор значка: черновик запоминает ключ, выделение перерисовывается.
        private void OnIconClick(object? sender, RoutedEventArgs e)
        {
            if (_draft is null) return;
            if (sender is not Control c || c.DataContext is not LabelIconOption option) return;

            _draft.Icon = option.Key;
            foreach (var icon in _draft.Icons)
                icon.IsSelected = icon.Key == option.Key;
            e.Handled = true;
        }
    }
}
