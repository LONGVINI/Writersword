using Avalonia.Threading;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels.Avatars
{
    /// <summary>
    /// Пункт списка «переложить в другую папку» у плитки картинки.
    ///
    /// Список строится для каждой плитки свой, с уже подставленной картинкой:
    /// иначе пришлось бы держать «выбранную для переноса» плитку отдельным
    /// состоянием и разбирать в два шага, что и куда переносят.
    /// </summary>
    public class CharacterAvatarPackMoveTargetViewModel
    {
        public string PackId { get; }
        public string DisplayName { get; }
        public CharacterAvatarPackScope Scope { get; }
        public bool IsLocal => Scope == CharacterAvatarPackScope.Local;
        public bool IsGlobal => Scope == CharacterAvatarPackScope.Global;
        public string ScopeLabel { get; }
        public ReactiveCommand<Unit, Unit> MoveCommand { get; }

        public CharacterAvatarPackMoveTargetViewModel(
            string packId,
            string displayName,
            CharacterAvatarPackScope scope,
            string avatarRef,
            Func<string, string, Task> onMove)
        {
            PackId = packId;
            DisplayName = displayName;
            Scope = scope;
            ScopeLabel = CharacterAvatarScopeText.Label(scope);

            MoveCommand = ReactiveCommand.CreateFromTask(() => onMove(avatarRef, packId));
        }
    }

    /// <summary>
    /// Подписи областей хранения. Собраны в одном месте: их показывают и пикер,
    /// и менеджер, и списки переноса, и разъехавшиеся формулировки читались бы
    /// как разные вещи.
    /// </summary>
    public static class CharacterAvatarScopeText
    {
        public static string Label(CharacterAvatarPackScope scope) =>
            scope == CharacterAvatarPackScope.Local
                ? Localized("AvatarPack_ScopeLocal", "в проекте")
                : Localized("AvatarPack_ScopeGlobal", "глобальная");

        public static string BuiltIn => Localized("AvatarPack_ScopeBuiltIn", "встроенная");

        public static string Localized(string key, string fallback)
        {
            try
            {
                return CharactersStrings.ResourceManager
                    .GetString(key, CharactersStrings.Culture) ?? fallback;
            }
            catch { return fallback; }
        }
    }

    /// <summary>
    /// Плитка картинки в менеджере папок. Умеет то, чего в пикере нет:
    /// удаление из хранилища с переспросом, перенос в другую папку и
    /// назначение обложкой.
    /// </summary>
    public class CharacterAvatarPackManagerItemViewModel : ReactiveObject
    {
        public string AvatarRef { get; }
        public string FileName { get; }
        public string ToolTip => System.IO.Path.GetFileNameWithoutExtension(FileName);
        public Avalonia.Media.Imaging.Bitmap? Thumbnail { get; }

        /// <summary>Картинку разрешено трогать: встроенные папки только читаются.</summary>
        public bool CanModify { get; }

        private bool _isCover;
        /// <summary>Этой картинкой папка показывается в списках.</summary>
        public bool IsCover
        {
            get => _isCover;
            set => this.RaiseAndSetIfChanged(ref _isCover, value);
        }

        public ObservableCollection<CharacterAvatarPackMoveTargetViewModel> MoveTargets { get; } = new();
        public bool HasMoveTargets => MoveTargets.Any();

        /// <summary>
        /// Сообщить о пересборке списка целей переноса. Сам список — коллекция
        /// с уведомлениями, а признак «цели есть» из неё выводится и об
        /// изменениях коллекции сам не узнаёт.
        /// </summary>
        public void NotifyMoveTargetsChanged() => this.RaisePropertyChanged(nameof(HasMoveTargets));

        private bool _isConfirmingDelete;
        public bool IsConfirmingDelete
        {
            get => _isConfirmingDelete;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isConfirmingDelete, value);
                this.RaisePropertyChanged(nameof(IsNotConfirmingDelete));
            }
        }

        public bool IsNotConfirmingDelete => !_isConfirmingDelete;

        public ReactiveCommand<Unit, Unit> RequestDeleteCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmDeleteCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelDeleteCommand { get; }
        public ReactiveCommand<Unit, Unit> SetAsCoverCommand { get; }

        public CharacterAvatarPackManagerItemViewModel(
            CharacterAvatarItem item,
            ICharacterAvatarService svc,
            bool canModify,
            bool isCover,
            Action<string> onDelete,
            Action<string> onSetCover)
        {
            AvatarRef = item.AvatarRef;
            FileName = item.FileName;
            CanModify = canModify && item.CanDelete;
            _isCover = isCover;

            RequestDeleteCommand = ReactiveCommand.Create(() =>
            {
                if (CanModify) IsConfirmingDelete = true;
            });

            ConfirmDeleteCommand = ReactiveCommand.Create(() =>
            {
                IsConfirmingDelete = false;
                if (CanModify) onDelete(AvatarRef);
            });

            // Тело в скобках, а не выражением: присваивание как выражение
            // отдаёт значение, и команда собралась бы как ReactiveCommand
            // с результатом bool вместо Unit.
            CancelDeleteCommand = ReactiveCommand.Create(() => { IsConfirmingDelete = false; });

            SetAsCoverCommand = ReactiveCommand.Create(() =>
            {
                if (CanModify) onSetCover(FileName);
            });

            try { Thumbnail = svc.LoadBitmap(item.AvatarRef); } catch { }
        }

        public void Dispose() => Thumbnail?.Dispose();
    }

    /// <summary>
    /// Папка в ленте менеджера.
    /// </summary>
    public class CharacterAvatarPackManagerPackViewModel : ReactiveObject
    {
        public string PackId { get; }
        public bool IsBuiltIn { get; }
        public bool IsUserPack => !IsBuiltIn;
        public CharacterAvatarPackScope Scope { get; }

        /// <summary>Локальная пользовательская папка — жёлтый значок, как у палитр.</summary>
        public bool IsLocal => IsUserPack && Scope == CharacterAvatarPackScope.Local;

        /// <summary>Глобальная пользовательская папка — синий значок.</summary>
        public bool IsGlobalUser => IsUserPack && Scope == CharacterAvatarPackScope.Global;

        /// <summary>
        /// Библиотеку нельзя ни удалить, ни переименовать, ни перенести: это не
        /// папка, а склад несгруппированных картинок, и он существует всегда.
        /// </summary>
        public bool IsLibrary { get; }

        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set
            {
                this.RaiseAndSetIfChanged(ref _displayName, value);
                this.RaisePropertyChanged(nameof(ChipDescription));
            }
        }

        private string? _iconFileName;
        /// <summary>Имя файла обложки. Пусто — папку показывает первая картинка.</summary>
        public string? IconFileName
        {
            get => _iconFileName;
            set => this.RaiseAndSetIfChanged(ref _iconFileName, value);
        }

        public string ScopeLabel => IsBuiltIn
            ? CharacterAvatarScopeText.BuiltIn
            : CharacterAvatarScopeText.Label(Scope);

        public string ChipDescription => IsBuiltIn
            ? "Встроенная папка из поставки. Её картинки можно брать, но менять и удалять нельзя."
            : IsLibrary
                ? "Склад несгруппированных картинок. Существует всегда, переносу и удалению не подлежит."
                : IsLocal
                    ? "Папка лежит в файле проекта и уедет вместе с ним."
                    : "Папка лежит в данных приложения и видна во всех проектах.";

        public ObservableCollection<CharacterAvatarPackManagerItemViewModel> Items { get; } = new();

        public int ItemCount => Items.Count;
        public bool IsEmpty => Items.Count == 0;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public CharacterAvatarPackManagerPackViewModel(
            CharacterAvatarPackInfo pack,
            ICharacterAvatarService svc,
            Action<string> onDeleteItem,
            Action<string> onSetCover)
        {
            PackId = pack.Id;
            IsBuiltIn = pack.Source == CharacterAvatarPackSource.BuiltIn;
            Scope = pack.Scope;
            IsLibrary = pack.Id == "__library__";
            _iconFileName = pack.IconFileName;

            if (IsBuiltIn)
            {
                _displayName = CharactersStrings.ResourceManager
                    .GetString(pack.LocalizationKey, CharactersStrings.Culture) ?? pack.Id;
            }
            else if (IsLibrary)
            {
                _displayName = CharactersStrings.ResourceManager
                    .GetString("AvatarPack_library") ?? "Мои аватарки";
            }
            else
            {
                _displayName = pack.Name ?? pack.Id;
            }

            var canModify = !IsBuiltIn;
            foreach (var item in pack.Items)
                Items.Add(new CharacterAvatarPackManagerItemViewModel(
                    item, svc, canModify,
                    isCover: !string.IsNullOrEmpty(pack.IconFileName)
                             && string.Equals(item.FileName, pack.IconFileName, StringComparison.OrdinalIgnoreCase),
                    onDelete: onDeleteItem,
                    onSetCover: onSetCover));
        }

        public void Dispose()
        {
            foreach (var item in Items) item.Dispose();
        }
    }

    /// <summary>
    /// Папки с аватарками.
    ///
    /// Деление на локальные и глобальные повторяет устройство палитр цветов:
    /// локальная папка лежит внутри архива проекта и уезжает вместе с ним,
    /// глобальная живёт в данных приложения и видна во всех проектах. Папка
    /// переносится между областями тем же сегментированным переключателем,
    /// содержимое переезжает с ней.
    /// </summary>
    public class CharacterAvatarPackManagerViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPackManagerViewModel>();
        private readonly ICharacterAvatarService _avatarService;

        // Переименование дебаунсится: иначе каждая нажатая буква переписывала бы
        // pack.json, а для локальной папки — ещё и сбрасывала архив проекта.
        private DispatcherTimer? _renameDebounce;
        private const int RenameDebounceMs = 600;

        // Имя в поле правки задаётся и самим кодом — при выборе другой папки.
        // Флаг отделяет такую подстановку от набора руками, иначе выбор папки
        // тут же запускал бы её переименование в то же самое имя.
        private bool _suppressRename;

        public ObservableCollection<CharacterAvatarPackManagerPackViewModel> Packs { get; } = new();
        public event Action? CloseRequested;

        private CharacterAvatarPackManagerPackViewModel? _selectedPack;
        public CharacterAvatarPackManagerPackViewModel? SelectedPack
        {
            get => _selectedPack;
            set
            {
                if (_selectedPack != null) _selectedPack.IsSelected = false;
                this.RaiseAndSetIfChanged(ref _selectedPack, value);
                if (_selectedPack != null) _selectedPack.IsSelected = true;

                _suppressRename = true;
                SelectedPackName = _selectedPack?.DisplayName ?? string.Empty;
                _suppressRename = false;

                IsConfirmingPackDelete = false;
                RaiseSelectionFlags();
                RebuildMoveTargets();
            }
        }

        private void RaiseSelectionFlags()
        {
            this.RaisePropertyChanged(nameof(HasSelectedPack));
            this.RaisePropertyChanged(nameof(CanEditPack));
            this.RaisePropertyChanged(nameof(CanMoveScope));
            this.RaisePropertyChanged(nameof(CanMakeLocal));
            this.RaisePropertyChanged(nameof(CanMakeGlobal));
            this.RaisePropertyChanged(nameof(SelectedIsLocal));
            this.RaisePropertyChanged(nameof(SelectedIsGlobal));
            this.RaisePropertyChanged(nameof(CanResetCover));
            this.RaisePropertyChanged(nameof(SelectedPackIsEmpty));
        }

        public bool HasSelectedPack => SelectedPack != null;

        /// <summary>Папку можно править: встроенные и библиотека — нет.</summary>
        public bool CanEditPack => SelectedPack?.IsUserPack == true && SelectedPack?.IsLibrary != true;

        public bool CanMoveScope => CanEditPack;
        public bool SelectedIsLocal => SelectedPack?.Scope == CharacterAvatarPackScope.Local;
        public bool SelectedIsGlobal => CanEditPack && SelectedPack?.Scope == CharacterAvatarPackScope.Global;
        public bool CanMakeLocal => CanMoveScope && SelectedPack?.Scope == CharacterAvatarPackScope.Global;
        public bool CanMakeGlobal => CanMoveScope && SelectedPack?.Scope == CharacterAvatarPackScope.Local;

        /// <summary>Обложка задана вручную — есть что сбрасывать.</summary>
        public bool CanResetCover => CanEditPack && !string.IsNullOrEmpty(SelectedPack?.IconFileName);

        public bool SelectedPackIsEmpty => SelectedPack?.IsEmpty == true;

        private string _selectedPackName = string.Empty;
        /// <summary>
        /// Имя выбранной папки, правится прямо в поле. Запись в хранилище идёт
        /// с задержкой — см. _renameDebounce.
        /// </summary>
        public string SelectedPackName
        {
            get => _selectedPackName;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedPackName, value);
                if (!_suppressRename) ScheduleRename();
            }
        }

        private string _newPackName = string.Empty;
        public string NewPackName
        {
            get => _newPackName;
            set => this.RaiseAndSetIfChanged(ref _newPackName, value);
        }

        private bool _newPackIsGlobal = true;
        /// <summary>
        /// Область для новой папки. По умолчанию глобальная: набор аватарок
        /// чаще собирают под все свои книги, а не под одну.
        /// </summary>
        public bool NewPackIsGlobal
        {
            get => _newPackIsGlobal;
            set
            {
                this.RaiseAndSetIfChanged(ref _newPackIsGlobal, value);
                this.RaisePropertyChanged(nameof(NewPackIsLocal));
            }
        }

        public bool NewPackIsLocal => !_newPackIsGlobal;

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private bool _isDropTarget;
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set => this.RaiseAndSetIfChanged(ref _isDropTarget, value);
        }

        private bool _isConfirmingPackDelete;
        /// <summary>
        /// Удаление папки переспрашивает прямо в панели действий: вместе с ней
        /// уходят все её картинки, и отменить это нечем.
        /// </summary>
        public bool IsConfirmingPackDelete
        {
            get => _isConfirmingPackDelete;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isConfirmingPackDelete, value);
                this.RaisePropertyChanged(nameof(IsNotConfirmingPackDelete));
            }
        }

        public bool IsNotConfirmingPackDelete => !_isConfirmingPackDelete;

        public ReactiveCommand<Unit, Unit> CloseCommand { get; }
        public ReactiveCommand<Unit, Unit> CreatePackCommand { get; }
        public ReactiveCommand<Unit, Unit> RequestDeletePackCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmDeletePackCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelDeletePackCommand { get; }
        public ReactiveCommand<string, Unit> SelectPackCommand { get; }
        public ReactiveCommand<Unit, Unit> ImportPackCommand { get; }
        public ReactiveCommand<Unit, Unit> ExportPackCommand { get; }
        public ReactiveCommand<Unit, Unit> MakeLocalCommand { get; }
        public ReactiveCommand<Unit, Unit> MakeGlobalCommand { get; }
        public ReactiveCommand<Unit, Unit> SetNewPackLocalCommand { get; }
        public ReactiveCommand<Unit, Unit> SetNewPackGlobalCommand { get; }
        public ReactiveCommand<Unit, Unit> AddImagesCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetCoverCommand { get; }

        public Func<Task<string?>>? RequestZipImportPicker { get; set; }
        public Func<string, Task<string?>>? RequestZipExportPicker { get; set; }

        /// <summary>Выбор картинок для добавления в папку. Возвращает пары «байты, имя».</summary>
        public Func<Task<IReadOnlyList<(byte[] data, string name)>>>? RequestImagePicker { get; set; }

        public CharacterAvatarPackManagerViewModel(ICharacterAvatarService avatarService)
        {
            _avatarService = avatarService;

            CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());

            CreatePackCommand = ReactiveCommand.Create(() =>
            {
                if (string.IsNullOrWhiteSpace(NewPackName))
                {
                    StatusMessage = "Впишите имя папки в поле слева.";
                    return;
                }

                var scope = NewPackIsGlobal
                    ? CharacterAvatarPackScope.Global
                    : CharacterAvatarPackScope.Local;

                var pack = _avatarService.CreatePack(NewPackName, scope);
                if (pack == null)
                {
                    StatusMessage = "Папку в проекте можно завести только при открытом проекте.";
                    return;
                }

                StatusMessage = string.Empty;
                NewPackName = string.Empty;
                Refresh(pack.Id);
            });

            RequestDeletePackCommand = ReactiveCommand.Create(() =>
            {
                if (CanEditPack) IsConfirmingPackDelete = true;
            });

            ConfirmDeletePackCommand = ReactiveCommand.Create(() =>
            {
                IsConfirmingPackDelete = false;
                var pack = SelectedPack;
                if (pack?.IsUserPack != true || pack.IsLibrary) return;
                _avatarService.DeletePack(pack.PackId, pack.Scope);
                Refresh();
            });

            CancelDeletePackCommand = ReactiveCommand.Create(() => { IsConfirmingPackDelete = false; });

            SelectPackCommand = ReactiveCommand.Create<string>(id =>
                SelectedPack = Packs.FirstOrDefault(p => p.PackId == id));

            ImportPackCommand = ReactiveCommand.CreateFromTask(ImportAsync);
            ExportPackCommand = ReactiveCommand.CreateFromTask(ExportAsync);
            MakeLocalCommand = ReactiveCommand.CreateFromTask(() => MoveScopeAsync(CharacterAvatarPackScope.Local));
            MakeGlobalCommand = ReactiveCommand.CreateFromTask(() => MoveScopeAsync(CharacterAvatarPackScope.Global));

            SetNewPackLocalCommand = ReactiveCommand.Create(() => { NewPackIsGlobal = false; });
            SetNewPackGlobalCommand = ReactiveCommand.Create(() => { NewPackIsGlobal = true; });

            AddImagesCommand = ReactiveCommand.CreateFromTask(AddImagesAsync);
            ResetCoverCommand = ReactiveCommand.Create(() => { SetCover(null); });

            Refresh();
        }

        public void Refresh(string? selectPackId = null)
        {
            var previousId = selectPackId ?? SelectedPack?.PackId;

            foreach (var pack in Packs) pack.Dispose();
            Packs.Clear();

            // Порядок: сначала папки проекта, затем глобальные, в конце
            // встроенные. Правится чаще всего то, что относится к текущей
            // книге, и оно должно быть под рукой.
            var all = _avatarService.GetAllPacks();

            foreach (var pack in Ordered(all))
                Packs.Add(new CharacterAvatarPackManagerPackViewModel(
                    pack, _avatarService, DeleteItem, SetCover));

            SelectedPack = Packs.FirstOrDefault(p => p.PackId == previousId) ?? Packs.FirstOrDefault();
        }

        private static IEnumerable<CharacterAvatarPackInfo> Ordered(IReadOnlyList<CharacterAvatarPackInfo> packs)
        {
            foreach (var pack in packs.Where(p => p.Source == CharacterAvatarPackSource.UserLocal))
                yield return pack;

            foreach (var pack in packs.Where(p => p.Source == CharacterAvatarPackSource.UserGlobal))
                yield return pack;

            foreach (var pack in packs.Where(p => p.Source == CharacterAvatarPackSource.BuiltIn))
                yield return pack;
        }

        /// <summary>
        /// Пересобрать списки «переложить в…» у плиток выбранной папки.
        /// Целями идут все пользовательские папки, кроме текущей: во встроенные
        /// класть нечего, они лежат в ресурсах сборки.
        /// </summary>
        private void RebuildMoveTargets()
        {
            var pack = SelectedPack;
            if (pack == null) return;

            var targets = Packs
                .Where(p => p.IsUserPack && p.PackId != pack.PackId)
                .ToList();

            foreach (var item in pack.Items)
            {
                item.MoveTargets.Clear();

                if (item.CanModify)
                    foreach (var target in targets)
                        item.MoveTargets.Add(new CharacterAvatarPackMoveTargetViewModel(
                            target.PackId, target.DisplayName, target.Scope,
                            item.AvatarRef, MoveItemAsync));

                item.NotifyMoveTargetsChanged();
            }
        }

        // ── Переименование ────────────────────────────────────────────────

        private void ScheduleRename()
        {
            if (!CanEditPack) return;

            if (_renameDebounce == null)
            {
                _renameDebounce = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(RenameDebounceMs)
                };
                _renameDebounce.Tick += (_, _) =>
                {
                    _renameDebounce!.Stop();
                    CommitRename();
                };
            }

            _renameDebounce.Stop();
            _renameDebounce.Start();
        }

        private void CommitRename()
        {
            var pack = SelectedPack;
            if (pack == null || !CanEditPack) return;

            var name = (_selectedPackName ?? string.Empty).Trim();
            if (name.Length == 0) return;
            if (string.Equals(name, pack.DisplayName, StringComparison.Ordinal)) return;

            try
            {
                _avatarService.UpdatePackMeta(pack.PackId, pack.Scope, name, pack.IconFileName);

                // Имя правится в самой ленте, без пересборки списка: пересборка
                // отобрала бы фокус у поля прямо посреди набора.
                pack.DisplayName = name;
                RebuildMoveTargets();
            }
            catch (Exception ex) { _logger.Error(ex, "CommitRename failed"); }
        }

        // ── Обложка ───────────────────────────────────────────────────────

        /// <summary>
        /// Назначить или снять обложку папки. null — снять: папка снова будет
        /// показываться первой своей картинкой.
        /// </summary>
        private void SetCover(string? fileName)
        {
            var pack = SelectedPack;
            if (pack == null || !CanEditPack) return;

            try
            {
                _avatarService.UpdatePackMeta(pack.PackId, pack.Scope, pack.DisplayName, fileName);
                pack.IconFileName = fileName;

                foreach (var item in pack.Items)
                    item.IsCover = fileName != null
                        && string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase);

                this.RaisePropertyChanged(nameof(CanResetCover));
                StatusMessage = fileName == null
                    ? "Обложка папки сброшена."
                    : "Обложка папки изменена.";
            }
            catch (Exception ex) { _logger.Error(ex, "SetCover failed"); }
        }

        // ── Содержимое ────────────────────────────────────────────────────

        private void DeleteItem(string avatarRef)
        {
            _avatarService.DeleteAvatar(avatarRef);
            _avatarService.RemoveRecentAvatar(avatarRef);
            StatusMessage = "Картинка удалена.";
            Refresh();
        }

        private async Task MoveItemAsync(string avatarRef, string targetPackId)
        {
            try
            {
                await _avatarService.MoveAvatarToPackAsync(avatarRef, targetPackId);

                // Ссылка картинки сменилась вместе с папкой — прежняя запись
                // «Недавних» указывает в пустоту.
                _avatarService.RemoveRecentAvatar(avatarRef);
                StatusMessage = "Картинка переложена.";
                Refresh();
            }
            catch (Exception ex) { _logger.Error(ex, "MoveItemAsync failed"); }
        }

        private async Task AddImagesAsync()
        {
            var pack = SelectedPack;
            if (pack == null || pack.IsBuiltIn || RequestImagePicker == null)
            {
                StatusMessage = "Выберите папку, куда положить картинки.";
                return;
            }

            var files = await RequestImagePicker();
            if (files == null || files.Count == 0) return;

            var added = 0;
            foreach (var (data, name) in files)
            {
                var saved = await _avatarService.SaveToPackAsync(data, name, pack.PackId);
                if (saved != null) added++;
            }

            StatusMessage = added == 0
                ? "Ни одну картинку сохранить не удалось."
                : string.Empty;
            Refresh(pack.PackId);
        }

        /// <summary>
        /// Положить брошенную картинку в выбранную папку. Обрезка здесь не
        /// спрашивается: в менеджере набирают запас картинок, а кадр выбирают
        /// в тот момент, когда аватарку ставят персонажу.
        /// </summary>
        public async Task HandleDroppedImageAsync(byte[] imageData, string fileName)
        {
            var pack = SelectedPack;
            if (pack == null || pack.IsBuiltIn)
            {
                StatusMessage = "Выберите папку, куда положить картинку.";
                return;
            }

            var saved = await _avatarService.SaveToPackAsync(imageData, fileName, pack.PackId);
            if (saved == null)
            {
                StatusMessage = "Не удалось сохранить картинку.";
                return;
            }

            StatusMessage = string.Empty;
            Refresh(pack.PackId);
        }

        /// <summary>Принять брошенный архив как новую глобальную папку.</summary>
        public async Task HandleDroppedZipAsync(string zipPath)
        {
            var pack = await _avatarService.ImportPackFromZipAsync(zipPath);
            if (pack == null)
            {
                StatusMessage = "Архив не удалось прочитать как папку с аватарками.";
                return;
            }

            StatusMessage = string.Empty;
            Refresh(pack.Id);
        }

        private async Task MoveScopeAsync(CharacterAvatarPackScope targetScope)
        {
            var pack = SelectedPack;
            if (pack?.IsUserPack != true || pack.IsLibrary) return;
            if (pack.Scope == targetScope) return;

            var moved = await _avatarService.MovePackToScopeAsync(pack.PackId, targetScope);
            if (moved == null)
            {
                StatusMessage = "Перенести папку не удалось. Для папки в проекте нужен открытый проект.";
                return;
            }

            StatusMessage = string.Empty;
            Refresh(moved.Id);
        }

        private async Task ImportAsync()
        {
            if (RequestZipImportPicker == null) return;
            var path = await RequestZipImportPicker();
            if (path == null) return;
            await HandleDroppedZipAsync(path);
        }

        private async Task ExportAsync()
        {
            if (SelectedPack == null || RequestZipExportPicker == null) return;
            var path = await RequestZipExportPicker(SelectedPack.DisplayName);
            if (path != null)
                await _avatarService.ExportPackToZipAsync(SelectedPack.PackId, path);
        }
    }
}
