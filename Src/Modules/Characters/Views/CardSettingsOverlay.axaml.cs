using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ReactiveUI;
using Serilog;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels;
using Writersword.Modules.Characters.Views.Avatars;

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
        public bool IsCollective { get; init; }

        // ── Аватарка ──────────────────────────────────────────────────────
        //
        // Кадр и снятие живут здесь, а не кнопками вокруг самого портрета в
        // карточке: там вокруг кружка в 84 точки уже сидели цвет и
        // шестерёнка, и ещё две кнопки превращали портрет в пульт. В этом
        // окне и так собрано всё про вид карточки — вид аватара, кольцо,
        // толщина рамки, — и аватарке тут самое место.
        //
        // Правки, как и остальные в окне, живут в черновике: превью
        // показывает итог сразу, а к персонажу он уезжает по OK. «Отмена»
        // не оставляет следов — в том числе от подобранного кадра.

        private Avalonia.Media.Imaging.Bitmap? _avatarBitmap;
        public Avalonia.Media.Imaging.Bitmap? AvatarBitmap
        {
            get => _avatarBitmap;
            set => this.RaiseAndSetIfChanged(ref _avatarBitmap, value);
        }

        private string? _avatarRef;
        public string? AvatarRef
        {
            get => _avatarRef;
            set
            {
                this.RaiseAndSetIfChanged(ref _avatarRef, value);
                this.RaisePropertyChanged(nameof(HasAvatar));
            }
        }

        /// <summary>Кадрировать и убирать есть что.</summary>
        public bool HasAvatar => !string.IsNullOrEmpty(_avatarRef);

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
        private static readonly ILogger _logger = Log.ForContext<CardSettingsOverlay>();

        private CharacterListItemViewModel? _item;
        private CharactersViewModel? _owner;
        private CardSettingsDraft? _draft;
        private Action? _applied;

        /// <summary>Аватарка, с которой окно открыли, — с ней и сравниваем по OK.</summary>
        private string? _originalAvatarRef;

        /// <summary>
        /// Миниатюра, построенная самим окном после кадрирования. Картинка
        /// карточки окну не принадлежит и освобождать её нельзя, а эту —
        /// нужно, иначе каждый подобранный кадр оставлял бы за собой
        /// нераспущенный битмап.
        /// </summary>
        private Bitmap? _ownedPreview;

        public CardSettingsOverlay()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Показать настройки для карточки персонажа. Колбэк applied вызывается
        /// после применения черновика по OK — вызывающая сторона может
        /// синхронизировать своё состояние (карточка персонажа в редакторе).
        /// </summary>
        public void ShowFor(CharacterListItemViewModel item, CharactersViewModel? owner, Action? applied = null)
        {
            _item = item;
            _owner = owner;
            _applied = applied;
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
                AvatarStrip = item.AvatarStrip,
                AvatarRef = item.AvatarPath
            };
            _originalAvatarRef = item.AvatarPath;
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
            _applied = null;
            _originalAvatarRef = null;

            _ownedPreview?.Dispose();
            _ownedPreview = null;
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

                // Аватарка уходит общим путём модуля: тем же шагом, каким она
                // ставится из ленты и из окна выбора, — значит Ctrl+Z вернёт и
                // снятие, и подобранный кадр.
                if (!string.Equals(_draft.AvatarRef, _originalAvatarRef, StringComparison.Ordinal))
                    _owner?.PushAvatarChange(_item.Id, _originalAvatarRef, _draft.AvatarRef);

                _applied?.Invoke();
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

        // ── Аватарка ──────────────────────────────────────────────────────

        /// <summary>
        /// Подобрать кадр. Окно обрезки лежит выше этого по ZIndex и потому
        /// открывается прямо поверх, не закрывая настройки: вернувшись из
        /// него, человек остаётся там же, откуда ушёл.
        ///
        /// Файл при этом не копируется — кадр живёт в самой ссылке, и в
        /// черновик уезжает именно она.
        /// </summary>
        private async void OnCropAvatarClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            var draft = _draft;
            var service = _owner?.AvatarService;
            if (draft is null || service is null) return;

            var current = draft.AvatarRef;
            if (string.IsNullOrEmpty(current)) return;

            var host = this.FindAncestorOfType<CharactersModuleView>();
            var overlay = host?.FindControl<CharacterAvatarCropOverlay>("AvatarCropOverlayControl");
            if (overlay is null) return;

            var bytes = service.LoadAvatarBytes(CharacterAvatarRef.BaseOf(current));
            if (bytes is null) return;

            CharacterAvatarCropPair? crops;
            Bitmap? source = null;
            try
            {
                using var ms = new MemoryStream(bytes);
                source = new Bitmap(ms);

                // Окно открывается на той форме, что выбрана прямо здесь, в
                // черновике: переключили «Полоска» — кадр подбирается для неё.
                // Второй кадр уходит нетронутым и таким же возвращается.
                crops = await overlay.ShowAsync(
                    source,
                    CharacterAvatarRef.CropOf(current),
                    null,
                    _item,
                    CharacterAvatarRef.StripCropOf(current),
                    draft.AvatarStrip,
                    CharacterAvatarRef.RotationOf(current));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Card settings: crop failed for {Ref}", current);
                return;
            }
            finally
            {
                source?.Dispose();
            }

            if (crops is null) return;

            var combined = CharacterAvatarRef.Apply(current, crops);
            if (string.IsNullOrEmpty(combined)) return;

            draft.AvatarRef = combined;
            ShowPreviewOf(service, combined);
        }

        /// <summary>
        /// Убрать аватарку. В карточке она пропадает только по OK, а «Отмена»
        /// возвращает всё как было — здесь снятие такая же правка черновика,
        /// как кольцо или толщина рамки.
        /// </summary>
        private void OnClearAvatarClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_draft is null) return;

            _draft.AvatarRef = null;
            _draft.AvatarBitmap = null;

            _ownedPreview?.Dispose();
            _ownedPreview = null;
        }

        /// <summary>Показать в превью картинку по ссылке — уже с новым кадром.</summary>
        private void ShowPreviewOf(ICharacterAvatarService service, string avatarRef)
        {
            if (_draft is null) return;

            Bitmap? bitmap = null;
            try { bitmap = service.LoadBitmap(avatarRef); }
            catch (Exception ex) { _logger.Error(ex, "Card settings: preview failed for {Ref}", avatarRef); }

            _ownedPreview?.Dispose();
            _ownedPreview = bitmap;
            _draft.AvatarBitmap = bitmap;
        }
    }
}
