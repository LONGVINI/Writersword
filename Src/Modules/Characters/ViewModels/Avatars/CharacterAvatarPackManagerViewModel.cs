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
    /// Выбранный на диске файл картинки: имя и отложенное чтение.
    ///
    /// Не байты: выборщик отдавал сразу содержимое всех выбранных файлов, и на
    /// пятистах фотографиях в памяти оказывалось всё выбранное разом, ещё до
    /// того как сохранится первая. Читается по одному, ровно перед записью.
    /// </summary>
    public sealed class CharacterPickedImage
    {
        private readonly Func<Task<byte[]?>> _read;

        public CharacterPickedImage(string name, Func<Task<byte[]?>> read)
        {
            Name = name;
            _read = read;
        }

        public string Name { get; }

        public Task<byte[]?> ReadAsync() => _read();
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
    /// удаление из хранилища, перенос в другую папку и назначение обложкой.
    /// </summary>
    public class CharacterAvatarPackManagerItemViewModel : ReactiveObject
    {
        private readonly ICharacterAvatarService _service;
        private Avalonia.Media.Imaging.Bitmap? _thumbnail;
        private bool _thumbnailRequested;

        /// <summary>Миниатюра готова. Пока нет — плитка показывает заглушку.</summary>
        public bool HasThumbnail => _thumbnail != null;

        public string AvatarRef { get; }
        public string FileName { get; }
        public string ToolTip => System.IO.Path.GetFileNameWithoutExtension(FileName);

        /// <summary>
        /// Миниатюра под плитку в шестьдесят восемь точек. Берётся при первом
        /// показе и в размер плитки, а не при создании и во весь предел службы:
        /// папка на пятьсот картинок раскодировалась при открытии целиком, и
        /// каждая картинка занимала мегабайт ради квадратика.
        /// </summary>
        public Avalonia.Media.Imaging.Bitmap? Thumbnail
        {
            get
            {
                if (!_thumbnailRequested)
                {
                    _thumbnailRequested = true;

                    // Уже построенную отдаём тем же кадром: иначе плитки,
                    // которые только что были на экране, мигали бы заглушкой
                    // при каждой прокрутке туда-обратно.
                    _thumbnail = _service.TryGetThumbnail(AvatarRef, 96);
                    if (_thumbnail == null) RequestThumbnail();
                }
                return _thumbnail;
            }
        }

        /// <summary>
        /// Построить миниатюру в стороне от UI-потока. Прокрутка реализует
        /// новые плитки прямо во время движения, и раскодирование на месте
        /// останавливало её на каждой новой строке.
        /// </summary>
        private async void RequestThumbnail()
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            try { bitmap = await _service.LoadThumbnailAsync(AvatarRef, 96); }
            catch { }

            if (bitmap == null) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _thumbnail = bitmap;
                this.RaisePropertyChanged(nameof(Thumbnail));
                this.RaisePropertyChanged(nameof(HasThumbnail));
            });
        }

        /// <summary>Картинку разрешено трогать: встроенные папки только читаются.</summary>
        public bool CanModify { get; }

        private bool _isCover;
        /// <summary>Этой картинкой папка показывается в списках.</summary>
        public bool IsCover
        {
            get => _isCover;
            set
            {
                this.RaiseAndSetIfChanged(ref _isCover, value);
                this.RaisePropertyChanged(nameof(CanSetCover));
            }
        }

        private bool _isDragging;
        /// <summary>
        /// Эту плитку сейчас тащат. Сама плитка остаётся в списке и ездит по
        /// нему вместо места вставки — отдельного пустого места заводить не
        /// нужно, а под курсором летит призрак.
        /// </summary>
        public bool IsDragging
        {
            get => _isDragging;
            set => this.RaiseAndSetIfChanged(ref _isDragging, value);
        }

        /// <summary>
        /// Звёздочку «сделать обложкой» показываем только там, где она что-то
        /// меняет. На самой обложке уже стоит отметка, и вторая звёздочка рядом
        /// с ней читалась как две разные.
        /// </summary>
        public bool CanSetCover => CanModify && !IsCover;

        public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
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
            _service = svc;

            // Удаление без переспроса: вернуть картинку можно кнопкой в строке
            // состояния, и лишнее нажатие на каждое удаление не нужно.
            DeleteCommand = ReactiveCommand.Create(() =>
            {
                if (CanModify) onDelete(AvatarRef);
            });

            SetAsCoverCommand = ReactiveCommand.Create(() =>
            {
                if (CanModify) onSetCover(FileName);
            });

        }

        /// <summary>
        /// Миниатюра принадлежит службе и уничтожению не подлежит: её же
        /// показывают другие ленты. Метод оставлен, потому что папка убирает
        /// свои плитки списком и не должна знать, есть ли им что освобождать.
        /// </summary>
        public void Dispose() { }
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

        /// <summary>
        /// Переставить обложку: имя файла и отметки на плитках. Отдельным
        /// методом, потому что то же самое нужно и при пересборке списка —
        /// правки окна ещё не дошли до хранилища, и заново собранная папка
        /// иначе показала бы старую обложку.
        /// </summary>
        /// <summary>
        /// Разложить плитки по списку имён файлов. Двигаем существующие плитки,
        /// а не строим их заново: у плитки внутри лежит уже прочитанная
        /// картинка, и пересборка ради перестановки читала бы с диска всю
        /// папку. Плитки, которых в списке нет, остаются в конце.
        /// </summary>
        public void ApplyItemOrder(IReadOnlyList<string> fileNames)
        {
            var target = 0;
            foreach (var name in fileNames)
            {
                var current = -1;
                for (var i = target; i < Items.Count; i++)
                    if (string.Equals(Items[i].FileName, name, StringComparison.OrdinalIgnoreCase))
                    { current = i; break; }

                if (current < 0) continue;
                if (current != target) Items.Move(current, target);
                target++;
            }
        }

        public void ApplyCoverFlags(string? iconFileName)
        {
            IconFileName = iconFileName;

            foreach (var item in Items)
                item.IsCover = !string.IsNullOrEmpty(iconFileName)
                    && string.Equals(item.FileName, iconFileName, StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Картинки этой папки можно переставлять. Встроенные паки лежат в
        /// ресурсах сборки: порядок им записывать некуда.
        /// </summary>
        public bool CanReorder => IsUserPack;

        public CharacterAvatarPackManagerPackViewModel(
            CharacterAvatarPackInfo pack,
            ICharacterAvatarService svc,
            Action<string> onDeleteItem,
            Action<string> onSetCover,
            Func<string, bool>? isRemoved = null)
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
            {
                // Убранное в этом сеансе окна в список не попадает: файл ещё на
                // месте, но показывать его нельзя — иначе Ctrl+Z нечего было бы
                // возвращать, картинка и так на виду.
                if (isRemoved != null && isRemoved(item.AvatarRef)) continue;

                Items.Add(new CharacterAvatarPackManagerItemViewModel(
                    item, svc, canModify,
                    isCover: !string.IsNullOrEmpty(pack.IconFileName)
                             && string.Equals(item.FileName, pack.IconFileName, StringComparison.OrdinalIgnoreCase),
                    onDelete: onDeleteItem,
                    onSetCover: onSetCover));
            }
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

                RaiseSelectionFlags();
            }
        }

        private void RaiseSelectionFlags()
        {
            this.RaisePropertyChanged(nameof(HasSelectedPack));
            this.RaisePropertyChanged(nameof(CanEditPack));
            this.RaisePropertyChanged(nameof(CanMoveScope));
            this.RaisePropertyChanged(nameof(CanMakeLocal));
            this.RaisePropertyChanged(nameof(CanMakeGlobal));
            this.RaisePropertyChanged(nameof(CanCopyToProject));
            this.RaisePropertyChanged(nameof(SelectedIsLocal));
            this.RaisePropertyChanged(nameof(SelectedIsGlobal));
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

        /// <summary>
        /// Общую папку можно положить копией в проект. У папки, которая уже в
        /// проекте, копировать нечего — она там и лежит.
        /// </summary>
        public bool CanCopyToProject => CanMakeLocal;

        /// <summary>Обложка задана вручную — есть что сбрасывать.</summary>

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

        public ReactiveCommand<Unit, Unit> CloseCommand { get; }
        public ReactiveCommand<Unit, Unit> CreatePackCommand { get; }
        public ReactiveCommand<Unit, Unit> DeletePackCommand { get; }
        public ReactiveCommand<string, Unit> SelectPackCommand { get; }
        public ReactiveCommand<Unit, Unit> ImportPackCommand { get; }
        public ReactiveCommand<Unit, Unit> ExportPackCommand { get; }
        public ReactiveCommand<Unit, Unit> MakeLocalCommand { get; }
        public ReactiveCommand<Unit, Unit> MakeGlobalCommand { get; }
        public ReactiveCommand<Unit, Unit> CopyToProjectCommand { get; }
        public ReactiveCommand<Unit, Unit> SetNewPackLocalCommand { get; }
        public ReactiveCommand<Unit, Unit> SetNewPackGlobalCommand { get; }
        public ReactiveCommand<Unit, Unit> AddImagesCommand { get; }

        public Func<Task<string?>>? RequestZipImportPicker { get; set; }
        public Func<string, Task<string?>>? RequestZipExportPicker { get; set; }

        /// <summary>Выбор картинок для добавления в папку. Возвращает пары «байты, имя».</summary>
        public Func<Task<IReadOnlyList<CharacterPickedImage>>>? RequestImagePicker { get; set; }

        public CharacterAvatarPackManagerViewModel(ICharacterAvatarService avatarService)
        {
            _avatarService = avatarService;

            CloseCommand = ReactiveCommand.Create(() =>
            {
                ApplyChanges();
                CloseRequested?.Invoke();
            });

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

            DeletePackCommand = ReactiveCommand.Create(() =>
            {
                var pack = SelectedPack;
                if (pack?.IsUserPack != true || pack.IsLibrary) return;

                var id = pack.PackId;
                var scope = pack.Scope;
                _removedPacks[id] = scope;

                PushStep(
                    undo: () => { _removedPacks.Remove(id); Refresh(id); },
                    redo: () => { _removedPacks[id] = scope; Refresh(); });

                StatusMessage = "Папка убрана. Ctrl+Z — вернуть.";
                Refresh();
            });

            SelectPackCommand = ReactiveCommand.Create<string>(id =>
                SelectedPack = Packs.FirstOrDefault(p => p.PackId == id));

            ImportPackCommand = ReactiveCommand.CreateFromTask(ImportAsync);
            ExportPackCommand = ReactiveCommand.CreateFromTask(ExportAsync);
            MakeLocalCommand = ReactiveCommand.CreateFromTask(() => MoveScopeAsync(CharacterAvatarPackScope.Local));
            MakeGlobalCommand = ReactiveCommand.CreateFromTask(() => MoveScopeAsync(CharacterAvatarPackScope.Global));
            CopyToProjectCommand = ReactiveCommand.CreateFromTask(CopyToProjectAsync);

            SetNewPackLocalCommand = ReactiveCommand.Create(() => { NewPackIsGlobal = false; });
            SetNewPackGlobalCommand = ReactiveCommand.Create(() => { NewPackIsGlobal = true; });

            AddImagesCommand = ReactiveCommand.CreateFromTask(AddImagesAsync);

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
            {
                if (_removedPacks.ContainsKey(pack.Id)) continue;

                var vm = new CharacterAvatarPackManagerPackViewModel(
                    pack, _avatarService, DeleteItem, SetCover, _removedItems.Contains);

                // Правки этого сеанса ещё не в хранилище — накладываем их на
                // свежесобранную папку, иначе она покажет старое имя и обложку.
                if (_pendingMeta.TryGetValue(pack.Id, out var meta))
                {
                    vm.DisplayName = meta.Name;
                    vm.ApplyCoverFlags(meta.Icon);
                }

                if (_pendingOrder.TryGetValue(pack.Id, out var order))
                    vm.ApplyItemOrder(order);

                Packs.Add(vm);
            }

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

            var id = pack.PackId;
            var before = MetaOf(pack);
            var after = (before.Scope, name, before.Icon);
            const string message = "Имя папки изменено. Ctrl+Z — вернуть.";

            ApplyMeta(id, after, message);
            PushStep(
                undo: () => ApplyMeta(id, before, message),
                redo: () => ApplyMeta(id, after, message));
        }

        // ── Обложка ───────────────────────────────────────────────────────

        /// <summary>
        /// Назначить обложку папки — картинку, которой папка показывается в
        /// списках. Отдельной кнопки «сбросить» нет: сброс возвращал папку к
        /// показу первой своей картинки, то есть к состоянию, в котором она и
        /// так живёт, пока обложку не выбрали. Кнопка почти всегда была
        /// выключена и объяснить себя не могла.
        /// </summary>
        private void SetCover(string? fileName)
        {
            var pack = SelectedPack;
            if (pack == null || !CanEditPack) return;

            var before = MetaOf(pack);
            if (string.Equals(before.Icon, fileName, StringComparison.OrdinalIgnoreCase)) return;

            var id = pack.PackId;
            var after = (before.Scope, before.Name, fileName);
            var message = fileName == null
                ? "Обложка снята. Ctrl+Z — вернуть."
                : "Обложка папки изменена. Ctrl+Z — вернуть.";

            ApplyMeta(id, after, message);
            PushStep(
                undo: () => ApplyMeta(id, before, message),
                redo: () => ApplyMeta(id, after, message));
        }

        /// <summary>Свойства папки с учётом ещё не записанных правок этого сеанса.</summary>
        private (CharacterAvatarPackScope Scope, string Name, string? Icon) MetaOf(
            CharacterAvatarPackManagerPackViewModel pack)
            => _pendingMeta.TryGetValue(pack.PackId, out var meta)
                ? meta
                : (pack.Scope, pack.DisplayName, pack.IconFileName);

        /// <summary>
        /// Поставить папке имя и обложку. В хранилище они уйдут при закрытии
        /// окна, сейчас правится только показ.
        ///
        /// Папка ищется по идентификатору, а не берётся ссылкой: пересборка
        /// списка (любое удаление картинки её делает) заводит новые объекты, и
        /// отмена, помнящая старый, правила бы то, чего уже нет на экране.
        /// </summary>
        private void ApplyMeta(
            string packId,
            (CharacterAvatarPackScope Scope, string Name, string? Icon) meta,
            string message)
        {
            _pendingMeta[packId] = meta;

            var pack = Packs.FirstOrDefault(p => p.PackId == packId);
            if (pack != null)
            {
                pack.DisplayName = meta.Name;
                pack.ApplyCoverFlags(meta.Icon);

                // Поле над лентой показывает имя выбранной папки — при откате
                // оно должно поехать вместе с ней, но не считаться новой правкой.
                if (ReferenceEquals(pack, SelectedPack))
                {
                    _suppressRename = true;
                    SelectedPackName = meta.Name;
                    _suppressRename = false;
                }
            }

            StatusMessage = message;
        }

        // ── Содержимое ────────────────────────────────────────────────────

        // ── Своя история окна ─────────────────────────────────────────────
        //
        // Пока окно открыто, удаление ничего не стирает: картинка или папка
        // просто перестаёт показываться, а файл лежит на месте. Насовсем всё
        // уходит при закрытии окна. Отсюда и Ctrl+Z: вернуть — это снять
        // пометку, а не восстанавливать файл.
        //
        // История своя, отдельная от истории модуля: снаружи окна отменять эти
        // шаги нечем и незачем — при закрытии они перестают быть обратимыми.
        // Так же устроен и редактор цвета.
        private readonly Stack<(Action Undo, Action Redo)> _undo = new();
        private readonly Stack<(Action Undo, Action Redo)> _redo = new();

        private readonly HashSet<string> _removedItems =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CharacterAvatarPackScope> _removedPacks =
            new(StringComparer.Ordinal);

        // Имя и обложка папки — тоже не сразу в хранилище. Обе правки живут
        // здесь до закрытия окна: так они откатываются мгновенно, а список
        // папок, пересобранный по ходу дела, показывает их, а не то, что пока
        // лежит на диске.
        private readonly Dictionary<string, (CharacterAvatarPackScope Scope, string Name, string? Icon)> _pendingMeta =
            new(StringComparer.Ordinal);

        // Переставленный, но ещё не записанный порядок картинок: ключ — папка,
        // значение — имена файлов сверху вниз. Живёт по тем же правилам, что и
        // прочие правки окна: пока окно открыто, в хранилище ничего не уходит,
        // Ctrl+Z возвращает, закрытие применяет.
        private readonly Dictionary<string, List<string>> _pendingOrder =
            new(StringComparer.Ordinal);

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        private void PushStep(Action undo, Action redo)
        {
            _undo.Push((undo, redo));
            _redo.Clear();
            RaiseHistoryFlags();
        }

        private void RaiseHistoryFlags()
        {
            this.RaisePropertyChanged(nameof(CanUndo));
            this.RaisePropertyChanged(nameof(CanRedo));
        }

        /// <summary>Отменить последний шаг окна. false — отменять нечего.</summary>
        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            var step = _undo.Pop();
            step.Undo();
            _redo.Push(step);
            RaiseHistoryFlags();
            return true;
        }

        /// <summary>Повторить отменённое. false — повторять нечего.</summary>
        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            var step = _redo.Pop();
            step.Redo();
            _undo.Push(step);
            RaiseHistoryFlags();
            return true;
        }

        /// <summary>
        /// Довести до хранилища всё, что окно накопило за сеанс: имена, обложки
        /// и удаления. Зовётся при закрытии — до этого момента ни один файл не
        /// тронут и любой шаг откатывается через Ctrl+Z.
        /// </summary>
        public void ApplyChanges()
        {
            // Сначала свойства, потом удаления: правку имени у папки, которую
            // тут же удаляют, писать в хранилище незачем.
            foreach (var (packId, meta) in _pendingMeta)
            {
                if (_removedPacks.ContainsKey(packId)) continue;
                try { _avatarService.UpdatePackMeta(packId, meta.Scope, meta.Name, meta.Icon); }
                catch (Exception ex) { _logger.Error(ex, "ApplyChanges: meta {Id}", packId); }
            }

            foreach (var (packId, scope) in _removedPacks)
            {
                try { _avatarService.DeletePack(packId, scope); }
                catch (Exception ex) { _logger.Error(ex, "ApplyChanges: pack {Id}", packId); }
            }

            foreach (var avatarRef in _removedItems)
            {
                try
                {
                    _avatarService.DeleteAvatar(avatarRef);
                    _avatarService.RemoveRecentAvatar(avatarRef);
                }
                catch (Exception ex) { _logger.Error(ex, "ApplyChanges: item {Ref}", avatarRef); }
            }

            // Порядок пишется последним: до этого из папки могли убрать
            // картинку, и записанный раньше список назвал бы уже стёртый файл.
            foreach (var (packId, order) in _pendingOrder)
            {
                if (_removedPacks.ContainsKey(packId)) continue;
                try
                {
                    var scope = _pendingMeta.TryGetValue(packId, out var meta)
                        ? meta.Scope
                        : Packs.FirstOrDefault(p => p.PackId == packId)?.Scope
                          ?? CharacterAvatarPackScope.Global;

                    _avatarService.SetPackItemOrder(
                        packId, scope, order.Where(f => !IsRemovedFile(packId, f)).ToList());
                }
                catch (Exception ex) { _logger.Error(ex, "ApplyChanges: order {Id}", packId); }
            }

            _pendingMeta.Clear();
            _pendingOrder.Clear();
            _removedPacks.Clear();
            _removedItems.Clear();
            _undo.Clear();
            _redo.Clear();
            RaiseHistoryFlags();
        }

        /// <summary>
        /// Имя файла принадлежит картинке, убранной в этом сеансе окна. Ссылка
        /// картинки строится из области, папки и имени файла, поэтому её
        /// хватает, чтобы отличить убранную от одноимённой в соседней папке.
        /// </summary>
        private bool IsRemovedFile(string packId, string fileName)
        {
            var pack = Packs.FirstOrDefault(p => p.PackId == packId);
            if (pack == null) return false;

            return !pack.Items.Any(i =>
                string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Записать новый порядок картинок папки в сеанс окна. Список уже
        /// переставлен в самой папке — перетаскивание двигало плитки по ходу
        /// дела, — поэтому пересборка тут не нужна: она только погасила бы и
        /// заново прочитала все картинки ради того, что уже на экране.
        /// </summary>
        public void CommitItemOrder(string packId, List<string> before, List<string> after)
        {
            if (string.IsNullOrEmpty(packId)) return;
            if (before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase)) return;

            var had = _pendingOrder.TryGetValue(packId, out var previous);
            var restore = had ? new List<string>(previous!) : before;

            PushStep(
                undo: () => { SetPendingOrder(packId, had ? restore : null, before); },
                redo: () => { SetPendingOrder(packId, after, after); });

            _pendingOrder[packId] = new List<string>(after);
            StatusMessage = "Порядок изменён. Ctrl+Z — вернуть.";
        }

        private void SetPendingOrder(string packId, List<string>? pending, List<string> visible)
        {
            if (pending == null) _pendingOrder.Remove(packId);
            else _pendingOrder[packId] = new List<string>(pending);

            Packs.FirstOrDefault(p => p.PackId == packId)?.ApplyItemOrder(visible);
        }

        private void DeleteItem(string avatarRef)
        {
            var packId = SelectedPack?.PackId;

            _removedItems.Add(avatarRef);
            PushStep(
                undo: () => { _removedItems.Remove(avatarRef); Refresh(packId); },
                redo: () => { _removedItems.Add(avatarRef); Refresh(packId); });

            StatusMessage = "Картинка убрана. Ctrl+Z — вернуть.";
            Refresh(packId);
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
            foreach (var file in files)
            {
                // Читаем и тут же отдаём на запись: следующая картинка берётся
                // только после того, как предыдущая улеглась и её байты стали
                // не нужны.
                var data = await file.ReadAsync();
                if (data == null) continue;

                var saved = await _avatarService.SaveToPackAsync(data, file.Name, pack.PackId);
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

        /// <summary>
        /// Положить копию общей папки в проект, оставив её и в общих.
        ///
        /// Это то, что нужно при передаче проекта другому человеку: у него нет
        /// ни ваших папок, ни библиотеки, и аватарки из них у него не покажутся.
        /// Копия в архиве проекта уезжает вместе с ним, а исходник продолжает
        /// работать во всех остальных ваших проектах — в отличие от переноса,
        /// после которого в общих папки не остаётся.
        /// </summary>
        private async Task CopyToProjectAsync()
        {
            var pack = SelectedPack;
            if (pack?.IsUserPack != true || pack.IsLibrary) return;
            if (pack.Scope == CharacterAvatarPackScope.Local) return;

            var copied = await _avatarService.CopyPackToScopeAsync(
                pack.PackId, CharacterAvatarPackScope.Local);

            if (copied == null)
            {
                StatusMessage = "Положить копию в проект не удалось. Нужен открытый проект.";
                return;
            }

            StatusMessage = "Копия папки лежит в проекте — она уедет вместе с ним.";
            Refresh(copied.Id);
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
