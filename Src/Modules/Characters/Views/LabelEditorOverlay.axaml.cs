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

        public LabelIconOption(string key) => Key = key;
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
        public string Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }

        private string _color = "#607D8B";
        public string Color { get => _color; set => this.RaiseAndSetIfChanged(ref _color, value); }

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
            }
        }

        public bool HasCustomIcon => !string.IsNullOrWhiteSpace(_iconImage);

        public ObservableCollection<LabelIconOption> Icons { get; } = new();
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
            CharacterLabelIcons.Crown
        };

        private static readonly ILogger _logger = Log.ForContext<LabelEditorOverlay>();

        private LabelEditorDraft? _draft;
        private CharacterLabel? _original;
        private Action<CharacterLabel>? _apply;

        public LabelEditorOverlay()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Показать редактор. label == null — создание новой метки;
        /// иначе — правка существующей (Id и Order сохраняются).
        /// Колбэк apply вызывается по OK с готовой меткой.
        /// </summary>
        public void ShowFor(CharacterLabel? label, Action<CharacterLabel> apply)
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
                IconImage = label?.IconImage
            };

            foreach (var key in IconSet)
                _draft.Icons.Add(new LabelIconOption(key) { IsSelected = key == _draft.Icon });

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
                    IconImage = _draft.IconImage
                };
                _apply(result);
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
                    FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
                });

                if (files == null || files.Count == 0) return;

                await using var stream = await files[0].OpenReadAsync();
                using var buffer = new System.IO.MemoryStream();
                await stream.CopyToAsync(buffer);

                var imageRef = await AvatarService.SaveToProjectAsync(buffer.ToArray(), files[0].Name);
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
