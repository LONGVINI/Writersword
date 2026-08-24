using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Avatars;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.Views.Avatars
{
    /// <summary>
    /// Выбор аватара по центру модуля, со скримом — как редактор цвета и окно
    /// настроек карточки. Замена отдельному системному окну
    /// CharacterAvatarPickerWindow: хостится в CharactersModuleView, результат
    /// выбора (ссылка на аватар или null при отмене) отдаётся через ShowAsync.
    ///
    /// Окно целиком принимает брошенные картинки: файл, отпущенный где угодно
    /// над панелью, проходит обрезку и становится аватаром персонажа.
    /// </summary>
    public partial class CharacterAvatarPickerOverlay : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerOverlay>();

        private static readonly FilePickerFileType ImageFileType = new(CharactersStrings.FilePicker_ImagesFilter)
        {
            Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
            MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" }
        };

        // Те же расширения, что принимает служба аватарок. Список нужен, чтобы
        // отсеять брошенные папки и посторонние файлы до чтения с диска.
        private static readonly string[] DroppableExtensions =
            { ".jpg", ".jpeg", ".png", ".webp" };

        private ICharacterAvatarService? _avatarService;
        private TaskCompletionSource<string?>? _tcs;
        private string? _selectedAvatarRef;
        private Action? _deleteAvatarAction;
        private string? _currentAvatarRef;

        // Вью-модель карточки, для которой выбирают аватарку. Нужна окну
        // обрезки: оно показывает справа саму карточку и правит её цвет.
        // Здесь она только хранится и передаётся дальше — выбор аватарки её
        // не трогает.
        private object? _cardContext;

        public CharacterAvatarPickerOverlay()
        {
            InitializeComponent();

            // Панель не должна вылезать за модуль: при сжатом окне она иначе
            // обрезается по краям вместе с нижними кнопками. Тот же приём, что
            // у редактора цвета — середина окна прокручивается.
            this.GetObservable(BoundsProperty).Subscribe(b =>
            {
                if (b.Width <= 0) return;
                ApplyPanelMetrics(b.Width, b.Height);
            });
        }

        private void ApplyPanelMetrics(double width, double height)
        {
            var panel = this.FindControl<Border>("PickerPanel");
            if (panel is null) return;

            panel.MaxHeight = Math.Max(240, height - 48);
            panel.MaxWidth = Math.Min(600, Math.Max(160, width - 48));
            panel.Width = Math.Min(600, Math.Max(160, width - 48));
        }

        /// <summary>
        /// Открывает оверлей и возвращает выбранную ссылку на аватар
        /// (null — отмена или удаление). Повторный вызов при уже открытом
        /// оверлее возвращает задачу текущего показа. deleteAvatarAction —
        /// действие удаления текущего аватара персонажа: если передано,
        /// в нижней панели появляется кнопка удаления; действие выполняется
        /// вызвавшей стороной, оверлей после него закрывается с null.
        ///
        /// currentAvatarRef — аватар, который стоит у персонажа сейчас. Если
        /// он есть, в нижней панели появляется обрезка текущего: кадр живёт в
        /// ссылке персонажа, поэтому переснять его можно, не трогая файл и не
        /// затрагивая других персонажей с той же картинкой.
        /// </summary>
        public Task<string?> ShowAsync(
            ICharacterAvatarService avatarService,
            string characterId,
            Action? deleteAvatarAction = null,
            string? currentAvatarRef = null,
            object? cardContext = null)
        {
            if (_tcs != null) return _tcs.Task;

            _avatarService = avatarService;
            _selectedAvatarRef = null;
            _deleteAvatarAction = deleteAvatarAction;
            _currentAvatarRef = currentAvatarRef;
            _cardContext = cardContext;
            _tcs = new TaskCompletionSource<string?>();

            var deleteButton = this.FindControl<Button>("DeleteAvatarButton");
            if (deleteButton != null) deleteButton.IsVisible = deleteAvatarAction != null;

            var recropButton = this.FindControl<Button>("RecropButton");
            if (recropButton != null)
                recropButton.IsVisible = !string.IsNullOrEmpty(currentAvatarRef);

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

            vm.RequestCropForBytes = (data, initial) => CropBytesAsync(data, initial);
            vm.RequestCropForRef = CropStoredAsync;

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
            _currentAvatarRef = null;
            _cardContext = null;

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

        /// <summary>
        /// Переснять кадр текущего аватара персонажа. Файл остаётся общим —
        /// меняется только кадр в его ссылке, поэтому та же картинка у другого
        /// персонажа остаётся обрезанной по-своему.
        /// </summary>
        private async void OnRecropCurrentClick(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentAvatarRef)) return;

            var crops = await CropStoredAsync(_currentAvatarRef);
            if (crops == null) return;

            _selectedAvatarRef = CharacterAvatarRef.Combine(_currentAvatarRef, crops.Circle, crops.Strip);
            CloseOverlay();
        }

        // ── Обрезка ───────────────────────────────────────────────────────

        /// <summary>
        /// Окно обрезки живёт на уровне модуля, а не внутри этой панели: оно
        /// шире её и внутри было бы срезано по краю. Порядок наложения задан
        /// в CharactersModuleView через ZIndex.
        /// </summary>
        private CharacterAvatarCropOverlay? FindCropOverlay()
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            return host?.FindControl<CharacterAvatarCropOverlay>("AvatarCropOverlayControl");
        }

        private async Task<CharacterAvatarCropPair?> CropBytesAsync(byte[] data, CharacterAvatarCrop? initial)
        {
            var overlay = FindCropOverlay();

            // Окна обрезки в разметке может не оказаться, если пикер показан
            // вне модуля. Полный кадр в этом случае честнее отказа: картинка
            // встанет целиком, а подрезать её можно будет позже.
            if (overlay == null) return new CharacterAvatarCropPair(CharacterAvatarCrop.Full, null);

            Bitmap? bitmap = null;
            try
            {
                using var ms = new MemoryStream(data);
                bitmap = new Bitmap(ms);
                return await overlay.ShowAsync(bitmap, initial, null, _cardContext);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "CropBytesAsync failed");
                return new CharacterAvatarCropPair(CharacterAvatarCrop.Full, null);
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        private async Task<CharacterAvatarCropPair?> CropStoredAsync(string avatarRef)
        {
            if (_avatarService == null) return null;

            var overlay = FindCropOverlay();
            if (overlay == null) return null;

            // Битмап берётся без кадра: окно показывает исходник целиком и
            // накладывает на него рамку, а прошлый кадр задаёт её начальное
            // положение.
            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            var bytes = _avatarService.LoadAvatarBytes(baseRef);
            if (bytes == null) return null;

            Bitmap? bitmap = null;
            try
            {
                using var ms = new MemoryStream(bytes);
                bitmap = new Bitmap(ms);
                // Оба кадра ссылки уезжают в окно: правят один, а второй всё
                // это время виден в превью и возвращается нетронутым.
                return await overlay.ShowAsync(
                    bitmap,
                    CharacterAvatarRef.CropOf(avatarRef),
                    null,
                    _cardContext,
                    CharacterAvatarRef.StripCropOf(avatarRef));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "CropStoredAsync failed for {Ref}", avatarRef);
                return null;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        // ── Приём брошенных файлов ────────────────────────────────────────

        private void OnPickerDragOver(object? sender, DragEventArgs e)
        {
            var accepts = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropTarget(accepts);
            e.Handled = true;
        }

        private void OnPickerDragLeave(object? sender, DragEventArgs e)
        {
            SetDropTarget(false);
            e.Handled = true;
        }

        private async void OnPickerDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            SetDropTarget(false);

            if (DataContext is not CharacterAvatarPickerViewModel vm) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            // Аватар у персонажа один, поэтому из брошенной пачки берётся
            // первая пригодная картинка. Остальные молча пропускаются: открыть
            // подряд пять окон обрезки ради одного аватара — не помощь.
            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;
                if (!IsDroppableImage(storageFile.Name)) continue;

                try
                {
                    await using var stream = await storageFile.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);

                    await vm.HandleImageBytesAsync(buffer.ToArray(), storageFile.Name);
                }
                catch (Exception ex)
                {
                    // Бросить могут что угодно — папку, ярлык, недоступный файл.
                    _logger.Error(ex, "Avatar picker drop failed: {Name}", storageFile.Name);
                }

                return;
            }
        }

        internal static bool IsDroppableImage(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return DroppableExtensions.Contains(ext);
        }

        private void SetDropTarget(bool value)
        {
            if (DataContext is CharacterAvatarPickerViewModel vm)
                vm.IsDropTarget = value;
        }

        // ── Папки ─────────────────────────────────────────────────────────

        /// <summary>
        /// Менеджер папок тоже живёт на уровне модуля. Внутри панели пикера
        /// его держать нельзя: панель уже, обрезает содержимое по краю, и её
        /// собственный скролл конфликтует со скроллом менеджера.
        /// </summary>
        private CharacterAvatarPackManagerOverlay? FindManagerOverlay()
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            return host?.FindControl<CharacterAvatarPackManagerOverlay>("AvatarPackManagerOverlayControl");
        }

        private void OpenManager()
        {
            var overlay = FindManagerOverlay();
            if (overlay == null || overlay.IsVisible || _avatarService == null) return;

            var managerVm = new CharacterAvatarPackManagerViewModel(_avatarService);
            managerVm.CloseRequested += CloseManagerAndRefresh;

            overlay.DataContext = managerVm;
            overlay.IsVisible = true;
        }

        private void CloseManager()
        {
            var overlay = FindManagerOverlay();
            if (overlay == null) return;
            overlay.IsVisible = false;
            overlay.DataContext = null;
        }

        private void CloseManagerAndRefresh()
        {
            CloseManager();

            // Менеджер мог завести, удалить, переименовать или перенести папку —
            // список разделов пикера после него собирается заново.
            if (DataContext is CharacterAvatarPickerViewModel vm) vm.Refresh();
        }

        // Скрим блокирует модуль, но окно не закрывает (как в редакторе цвета).
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    }
}
