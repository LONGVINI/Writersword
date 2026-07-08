using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReactiveUI;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Views
{
    /// <summary>
    /// Черновик настроек карточки: все правки в окне живут здесь и отражаются
    /// только в превью. К реальной карточке применяются по OK.
    /// </summary>
    public sealed class CardSettingsDraft : ReactiveObject
    {
        public string Color { get; init; } = "#607D8B";
        public string Name { get; init; } = string.Empty;
        public string FallbackIcon { get; init; } = "?";
        public Avalonia.Media.Imaging.Bitmap? AvatarBitmap { get; init; }
        public bool IsCollective { get; init; }

        private bool _ring;
        public bool Ring
        {
            get => _ring;
            set
            {
                this.RaiseAndSetIfChanged(ref _ring, value);
                this.RaisePropertyChanged(nameof(ShowRing));
            }
        }

        private bool _bookmark;
        public bool Bookmark
        {
            get => _bookmark;
            set
            {
                this.RaiseAndSetIfChanged(ref _bookmark, value);
                this.RaisePropertyChanged(nameof(ShowBookmark));
            }
        }

        private double _thickness = 2;
        public double Thickness
        {
            get => _thickness;
            set
            {
                this.RaiseAndSetIfChanged(ref _thickness, value);
                this.RaisePropertyChanged(nameof(BorderThickness));
            }
        }

        private bool _avatarStrip;
        public bool AvatarStrip
        {
            get => _avatarStrip;
            set
            {
                this.RaiseAndSetIfChanged(ref _avatarStrip, value);
                this.RaisePropertyChanged(nameof(ShowRing));
            }
        }

        // Кольцо — атрибут круглого аватара; у полоски его нет.
        public bool ShowRing => _ring && !_avatarStrip;
        public bool ShowBookmark => IsCollective && _bookmark;
        public Avalonia.Thickness BorderThickness => new(_thickness);
    }

    /// <summary>
    /// Окно настроек карточки персонажа: по центру модуля, со скримом — как
    /// редактор цвета. Правки видны в превью (черновик) и применяются к
    /// карточке только по OK; переключатели «Ко всем» раскатывают значение на
    /// все карточки тоже при OK. «Отмена»/крестик закрывают без изменений.
    /// </summary>
    public partial class CardSettingsOverlay : UserControl
    {
        private CharacterListItemViewModel? _item;
        private CharactersViewModel? _owner;
        private CardSettingsDraft? _draft;

        public CardSettingsOverlay()
        {
            InitializeComponent();
        }

        /// <summary>Показать настройки для карточки персонажа.</summary>
        public void ShowFor(CharacterListItemViewModel item, CharactersViewModel? owner)
        {
            _item = item;
            _owner = owner;
            _draft = new CardSettingsDraft
            {
                Color = item.Color,
                Name = item.Name,
                FallbackIcon = item.FallbackIcon,
                AvatarBitmap = item.AvatarBitmap,
                IsCollective = item.IsCollective,
                Ring = item.AvatarRing,
                Bookmark = item.GroupBookmark,
                Thickness = item.FrameThickness,
                AvatarStrip = item.AvatarStrip
            };
            DataContext = _draft;

            // Переключатели «Ко всем» каждый раз начинают выключенными.
            SetToggle("RingAllToggle", false);
            SetToggle("BookmarkAllToggle", false);
            SetToggle("ThicknessAllToggle", false);

            IsVisible = true;
        }

        private void SetToggle(string name, bool value)
        {
            var t = this.FindControl<ToggleButton>(name);
            if (t is not null) t.IsChecked = value;
        }

        private bool GetToggle(string name) =>
            this.FindControl<ToggleButton>(name)?.IsChecked == true;

        private void CloseOverlay()
        {
            IsVisible = false;
            DataContext = null;
            _draft = null;
            _item = null;
            _owner = null;
        }

        // OK: черновик применяется к карточке; взведённые «Ко всем» раскатывают
        // значения на остальные карточки.
        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            if (_item is not null && _draft is not null)
            {
                _item.AvatarRing = _draft.Ring;
                _item.GroupBookmark = _draft.Bookmark;
                _item.FrameThickness = _draft.Thickness;
                _item.AvatarStrip = _draft.AvatarStrip;

                if (GetToggle("RingAllToggle"))
                    _item.ApplyRingToAllCommand.Execute(_draft.Ring).Subscribe();
                if (GetToggle("BookmarkAllToggle"))
                    _owner?.ApplyBookmarkToAllGroups(_draft.Bookmark);
                if (GetToggle("ThicknessAllToggle"))
                    _owner?.ApplyFrameThicknessToAll(_draft.Thickness);
            }
            CloseOverlay();
        }

        // Отмена/крестик: карточка не менялась — просто закрываем.
        private void OnCancelClick(object? sender, RoutedEventArgs e) => CloseOverlay();

        // Скрим блокирует модуль, но окно не закрывает (как в редакторе цвета).
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        private void OnViewCircleClick(object? sender, RoutedEventArgs e)
        {
            if (_draft is not null) _draft.AvatarStrip = false;
        }

        private void OnViewStripClick(object? sender, RoutedEventArgs e)
        {
            if (_draft is not null) _draft.AvatarStrip = true;
        }
    }
}
