using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.ViewModels.Avatars;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.Views.Avatars
{
    /// <summary>
    /// Выбор аватара по центру модуля, со скримом — как редактор цвета и окно
    /// настроек карточки. Замена отдельному системному окну
    /// CharacterAvatarPickerWindow: хостится в CharactersModuleView, результат
    /// выбора (ссылка на аватар или null при отмене) отдаётся через ShowAsync.
    /// </summary>
    public partial class CharacterAvatarPickerOverlay : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerOverlay>();

        private static readonly FilePickerFileType ImageFileType = new(CharactersStrings.FilePicker_ImagesFilter)
        {
            Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
            MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" }
        };

        private ICharacterAvatarService? _avatarService;
        private TaskCompletionSource<string?>? _tcs;
        private string? _selectedAvatarRef;
        private Action? _deleteAvatarAction;

        public CharacterAvatarPickerOverlay()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Открывает оверлей и возвращает выбранную ссылку на аватар
        /// (null — отмена или удаление). Повторный вызов при уже открытом
        /// оверлее возвращает задачу текущего показа. deleteAvatarAction —
        /// действие удаления текущего аватара персонажа: если передано,
        /// в нижней панели появляется кнопка удаления; действие выполняется
        /// вызвавшей стороной, оверлей после него закрывается с null.
        /// </summary>
        public Task<string?> ShowAsync(
            ICharacterAvatarService avatarService,
            string characterId,
            Action? deleteAvatarAction = null)
        {
            if (_tcs != null) return _tcs.Task;

            _avatarService = avatarService;
            _selectedAvatarRef = null;
            _deleteAvatarAction = deleteAvatarAction;
            _tcs = new TaskCompletionSource<string?>();

            var deleteButton = this.FindControl<Button>("DeleteAvatarButton");
            if (deleteButton != null) deleteButton.IsVisible = deleteAvatarAction != null;

            var vm = new CharacterAvatarPickerViewModel(avatarService, characterId);
            vm.AvatarSelected += ref_ => _selectedAvatarRef = ref_;
            vm.CloseRequested += CloseOverlay;
            vm.OpenManagerRequested += OpenManager;

            vm.RequestFilePicker = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = CharactersStrings.FilePicker_SelectImageTitle,
                        AllowMultiple = false,
                        FileTypeFilter = new[] { ImageFileType }
                    });
                if (files.Count == 0) return null;
                var file = files[0];
                await using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return (ms.ToArray(), file.Name);
            };

            DataContext = vm;
            IsVisible = true;
            return _tcs.Task;
        }

        private void CloseOverlay()
        {
            CloseManager();
            IsVisible = false;
            DataContext = null;
            _avatarService = null;
            _deleteAvatarAction = null;

            var tcs = _tcs;
            _tcs = null;
            tcs?.TrySetResult(_selectedAvatarRef);
        }

        // Удаление текущего аватара: действие выполняет вызвавшая сторона,
        // оверлей закрывается без выбора (null).
        private void OnDeleteAvatarClick(object? sender, RoutedEventArgs e)
        {
            _deleteAvatarAction?.Invoke();
            _selectedAvatarRef = null;
            CloseOverlay();
        }

        private void OpenManager()
        {
            var slot = this.FindControl<ContentControl>("OverlaySlot");
            if (slot == null || slot.IsVisible || _avatarService == null) return;

            var managerVm = new CharacterAvatarPackManagerViewModel(_avatarService);
            managerVm.CloseRequested += CloseManagerAndRefresh;

            var overlay = new CharacterAvatarPackManagerOverlay { DataContext = managerVm };
            slot.Content = overlay;
            slot.IsVisible = true;
        }

        private void CloseManager()
        {
            var slot = this.FindControl<ContentControl>("OverlaySlot");
            if (slot == null) return;
            slot.IsVisible = false;
            slot.Content = null;
        }

        private void CloseManagerAndRefresh()
        {
            CloseManager();

            // Обновить пикер после закрытия менеджера
            if (DataContext is CharacterAvatarPickerViewModel vm)
                vm.Refresh();
        }

        // Скрим блокирует модуль, но оверлей не закрывает (как в редакторе цвета).
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    }
}
