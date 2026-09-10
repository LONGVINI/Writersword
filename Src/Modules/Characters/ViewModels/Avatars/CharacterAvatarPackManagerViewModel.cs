using Avalonia.Threading;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

        /// <summary>
        /// Сообщить показу, что миниатюра уже здесь.
        ///
        /// Готовую миниатюру отдаёт кеш службы — прямо в этом чтении свойства,
        /// без всякого ожидания. Молча так делать нельзя: тот, кто смотрит на
        /// признак «миниатюра готова», спросил его раньше и получил «нет», а
        /// второго повода спросить у него уже не будет. Так под каждой готовой
        /// картинкой оставалась лежать заглушка — на фотографии незаметная, а
        /// сквозь PNG с прозрачным фоном видная насквозь.
        ///
        /// Через очередь, а не сразу: оповещать об изменении прямо посреди
        /// чтения свойства значит дёргать привязку, которая это чтение и
        /// затеяла.
        /// </summary>
        private void NotifyThumbnailReady() =>
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => this.RaisePropertyChanged(nameof(HasThumbnail)),
                Avalonia.Threading.DispatcherPriority.Background);

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
                    else NotifyThumbnailReady();
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

        private bool _isSelected;
        /// <summary>
        /// Картинка выбрана щелчком — её и уберёт Delete. Выделение одно на
        /// всё окно: выбранная папка и выбранная картинка не соперничают,
        /// целью считается последнее нажатое (см. DeleteSelection у окна).
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
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

            // Удаление без переспроса: вернуть картинку можно клавишами Ctrl+Z,
            // и лишнее нажатие на каждое удаление не нужно.
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
    /// Папка в ленте менеджера. Показывается плиткой — обложкой, значком
    /// области и подписью, — а не вкладкой: папок бывает три десятка, они
    /// узнаются в лицо по картинке, и ряд одинаковых прямоугольников с текстом
    /// читается дольше, чем ряд разных картинок.
    /// </summary>
    public class CharacterAvatarPackManagerPackViewModel : ReactiveObject
    {
        private readonly ICharacterAvatarService _service;

        private Avalonia.Media.Imaging.Bitmap? _cover;
        private string? _coverRef;
        private bool _coverRequested;

        public string PackId { get; }
        public bool IsBuiltIn { get; }
        public bool IsUserPack => !IsBuiltIn;

        /// <summary>
        /// Где папка лежит прямо сейчас. Служебные вызовы — правка описания,
        /// порядок картинок, удаление — идут по нему, а не по выбранной в окне
        /// области: пока окно открыто, папка не переехала.
        /// </summary>
        public CharacterAvatarPackScope Scope { get; }

        private CharacterAvatarPackScope _effectiveScope;
        /// <summary>
        /// Область, выбранная в окне. Пока окно открыто, она может отличаться
        /// от <see cref="Scope"/>: переезд назначен, но не выполнен, и показ
        /// обязан говорить о том, что будет, — иначе переключатель щёлкает
        /// впустую.
        /// </summary>
        public CharacterAvatarPackScope EffectiveScope
        {
            get => _effectiveScope;
            set
            {
                if (_effectiveScope == value) return;
                _effectiveScope = value;
                this.RaisePropertyChanged(nameof(EffectiveScope));
                this.RaisePropertyChanged(nameof(IsLocal));
                this.RaisePropertyChanged(nameof(IsGlobalUser));
                this.RaisePropertyChanged(nameof(ScopeLabel));
                this.RaisePropertyChanged(nameof(ChipDescription));
                this.RaisePropertyChanged(nameof(HasPendingMove));
                this.RaisePropertyChanged(nameof(HasPendingAction));
            }
        }

        /// <summary>Переезд назначен, но ещё не выполнен.</summary>
        public bool HasPendingMove => IsUserPack && !IsLibrary && EffectiveScope != Scope;

        private bool _hasPendingCopy;
        /// <summary>
        /// Копия папки в проект назначена, но ещё не сделана. Как и переезд,
        /// откладывается до закрытия окна: копирование сотни фотографий по
        /// случайному нажатию должно отменяться, а не выполняться.
        /// </summary>
        public bool HasPendingCopy
        {
            get => _hasPendingCopy;
            set
            {
                if (_hasPendingCopy == value) return;
                _hasPendingCopy = value;
                this.RaisePropertyChanged(nameof(HasPendingCopy));
                this.RaisePropertyChanged(nameof(HasPendingAction));
                this.RaisePropertyChanged(nameof(ChipDescription));
            }
        }

        /// <summary>
        /// У папки есть назначенное, но не выполненное действие. Отметка на
        /// плитке одна на все такие случаи — что именно назначено, написано в
        /// подсказке папки.
        /// </summary>
        public bool HasPendingAction => HasPendingMove || HasPendingCopy;

        /// <summary>Локальная пользовательская папка — жёлтый значок, как у палитр.</summary>
        public bool IsLocal => IsUserPack && EffectiveScope == CharacterAvatarPackScope.Local;

        /// <summary>Глобальная пользовательская папка — синий значок.</summary>
        public bool IsGlobalUser => IsUserPack && EffectiveScope == CharacterAvatarPackScope.Global;

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
            set
            {
                this.RaiseAndSetIfChanged(ref _iconFileName, value);
                InvalidateCover();
            }
        }

        private bool _isDragging;
        /// <summary>
        /// Эту папку сейчас тащат. Устроено ровно как у картинок: плитка
        /// остаётся в ленте и ездит по ней вместо места вставки, а под курсором
        /// летит призрак.
        /// </summary>
        public bool IsDragging
        {
            get => _isDragging;
            set => this.RaiseAndSetIfChanged(ref _isDragging, value);
        }

        /// <summary>
        /// Картинка, которой папка показывается в ленте: назначенная обложка, а
        /// если её не назначали — первая картинка папки.
        /// </summary>
        public string? CoverRef
        {
            get
            {
                if (!string.IsNullOrEmpty(_iconFileName))
                {
                    var chosen = Items.FirstOrDefault(i =>
                        string.Equals(i.FileName, _iconFileName, StringComparison.OrdinalIgnoreCase));
                    if (chosen != null) return chosen.AvatarRef;
                }
                return Items.FirstOrDefault()?.AvatarRef;
            }
        }

        /// <summary>Обложка готова. Пока нет — плитка показывает значок папки.</summary>
        public bool HasCover => _cover != null;

        /// <summary>
        /// Сообщить показу, что обложка уже здесь. То же, что и у миниатюры
        /// картинки: готовую отдаёт кеш службы прямо в чтении свойства, и без
        /// оповещения значок папки так и оставался лежать под обложкой.
        /// У папок это было видно всегда — обложку к этому времени успевали
        /// построить плитки картинок, и путь через кеш срабатывал каждый раз.
        /// </summary>
        private void NotifyCoverReady() =>
            Dispatcher.UIThread.Post(
                () => this.RaisePropertyChanged(nameof(HasCover)),
                DispatcherPriority.Background);

        /// <summary>
        /// Обложка папки под плитку ленты. Берётся тем же размером, что и
        /// миниатюры картинок: обложка — это одна из них, и общий кеш службы
        /// отдаёт уже построенную, ничего не раскодируя заново.
        /// </summary>
        public Avalonia.Media.Imaging.Bitmap? Cover
        {
            get
            {
                var wanted = CoverRef;
                if (wanted == null) return null;

                if (!_coverRequested || !string.Equals(_coverRef, wanted, StringComparison.Ordinal))
                {
                    _coverRequested = true;
                    _coverRef = wanted;
                    _cover = _service.TryGetThumbnail(wanted, 96);

                    if (_cover == null) RequestCover(wanted);
                    else NotifyCoverReady();
                }
                return _cover;
            }
        }

        private async void RequestCover(string avatarRef)
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            try { bitmap = await _service.LoadThumbnailAsync(avatarRef, 96); }
            catch { }

            if (bitmap == null) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // За время чтения обложку могли сменить — тогда пришедшая
                // картинка уже не та, и ставить её нельзя.
                if (!string.Equals(_coverRef, avatarRef, StringComparison.Ordinal)) return;

                _cover = bitmap;
                this.RaisePropertyChanged(nameof(Cover));
                this.RaisePropertyChanged(nameof(HasCover));
            });
        }

        /// <summary>
        /// Обложка могла смениться: назначили другую, убрали картинку, добавили
        /// первую. Перечитываем только тогда, когда изменилась сама ссылка —
        /// иначе каждое движение в папке гасило бы плитку и строило ту же
        /// картинку заново.
        /// </summary>
        private void InvalidateCover()
        {
            var wanted = CoverRef;
            if (string.Equals(wanted, _coverRef, StringComparison.Ordinal)) return;

            _coverRequested = false;
            _coverRef = null;
            _cover = null;
            this.RaisePropertyChanged(nameof(Cover));
            this.RaisePropertyChanged(nameof(HasCover));
        }

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

        /// <summary>
        /// Переставить обложку: имя файла и отметки на плитках. Отдельным
        /// методом, потому что то же самое нужно и при пересборке списка —
        /// правки окна ещё не дошли до хранилища, и заново собранная папка
        /// иначе показала бы старую обложку.
        /// </summary>
        public void ApplyCoverFlags(string? iconFileName)
        {
            IconFileName = iconFileName;

            foreach (var item in Items)
                item.IsCover = !string.IsNullOrEmpty(iconFileName)
                    && string.Equals(item.FileName, iconFileName, StringComparison.OrdinalIgnoreCase);
        }

        public string ScopeLabel => IsBuiltIn
            ? CharacterAvatarScopeText.BuiltIn
            : CharacterAvatarScopeText.Label(EffectiveScope);

        public string ChipDescription
        {
            get
            {
                if (IsBuiltIn)
                    return "Встроенная папка из поставки. Её картинки можно брать, но менять и удалять нельзя.";

                if (IsLibrary)
                    return "Склад несгруппированных картинок. Существует всегда, переносу и удалению не подлежит.";

                var where = IsLocal
                    ? "Папка лежит в файле проекта и уедет вместе с ним."
                    : "Папка лежит в данных приложения и видна во всех проектах.";

                if (HasPendingMove)
                    where += " Переезд назначен и произойдёт после закрытия окна.";

                if (HasPendingCopy)
                    where += " Копия в проект назначена и ляжет туда после закрытия окна.";

                return where;
            }
        }

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

        /// <summary>
        /// Папку можно убрать. Встроенные и «Мои аватарки» не убираются:
        /// первые лежат в ресурсах сборки, вторая — общая свалка, которой
        /// некуда деться.
        /// </summary>
        public bool CanDeletePack => IsUserPack && !IsLibrary;

        /// <summary>
        /// Крестик на самой плитке папки. Кнопка в верхней панели никуда не
        /// делась, но до неё через всё окно тянуться курсором, а папка — вот
        /// она, под рукой.
        /// </summary>
        public ReactiveCommand<Unit, Unit>? DeletePackCommand { get; private set; }

        public CharacterAvatarPackManagerPackViewModel(
            CharacterAvatarPackInfo pack,
            ICharacterAvatarService svc,
            Action<string> onDeleteItem,
            Action<string> onSetCover,
            Func<string, bool>? isRemoved = null,
            Action<string>? onDeletePack = null)
        {
            PackId = pack.Id;
            IsBuiltIn = pack.Source == CharacterAvatarPackSource.BuiltIn;
            Scope = pack.Scope;
            _effectiveScope = pack.Scope;
            IsLibrary = pack.Id == "__library__";
            _iconFileName = pack.IconFileName;
            _service = svc;

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

            // Число картинок и обложка стоят на самой плитке папки: убрали
            // картинку — плитка обязана это показать, не дожидаясь пересборки
            // всей ленты.
            Items.CollectionChanged += OnItemsChanged;

            // Крестик заводится только у того, что вообще убирается: у
            // встроенной папки его не будет и нажать нечего.
            if (onDeletePack != null && CanDeletePack)
            {
                var id = PackId;
                DeletePackCommand = ReactiveCommand.Create(() => onDeletePack(id));
            }
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            this.RaisePropertyChanged(nameof(ItemCount));
            this.RaisePropertyChanged(nameof(IsEmpty));
            InvalidateCover();
        }

        public void Dispose()
        {
            Items.CollectionChanged -= OnItemsChanged;
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
    ///
    /// Ни одно действие окна не трогает хранилище сразу. Имя, обложка, порядок
    /// картинок, порядок папок, удаления и перенос между областями копятся в
    /// сеансе окна, откатываются через Ctrl+Z и уходят на диск при закрытии.
    /// Перенос — самое дорогое из них: он копирует папку целиком и стирает
    /// исходник, поэтому выполняется после закрытия окна, по одной папке за
    /// заход, с паузой между ними и на самом низком приоритете очереди — окно
    /// к этому времени уже закрыто, и торопиться некуда.
    /// </summary>
    public class CharacterAvatarPackManagerViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPackManagerViewModel>();
        private readonly ICharacterAvatarService _avatarService;

        // Переименование дебаунсится: иначе каждая нажатая буква переписывала бы
        // pack.json, а для локальной папки — ещё и сбрасывала архив проекта.
        private DispatcherTimer? _renameDebounce;
        private const int RenameDebounceMs = 600;

        // Сообщение об ошибке гаснет само. Постоянной строки состояния внизу
        // окна больше нет: подсказки про Ctrl+Z занимали её всё время работы, а
        // читать там нечего — окно и так показывает результат каждого действия.
        private DispatcherTimer? _statusTimer;
        private const int StatusHideMs = 6000;

        // Пауза между переносами папок после закрытия окна. Перенос читает и
        // пишет всё содержимое папки; идущие подряд без передышки, они на
        // несколько секунд занимают диск целиком.
        private const int MoveGapMs = 400;

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

                // Выбрали папку — целью удаления стала она, а выделение с
                // картинки снимается. Две подсвеченные плитки сразу и клавиша
                // Delete между ними — это загадка, а не выбор.
                SelectItem(null);
                _deleteTarget = DeleteTarget.Pack;

                _suppressRename = true;
                SelectedPackName = _selectedPack?.DisplayName ?? string.Empty;
                _suppressRename = false;

                RaiseSelectionFlags();
            }
        }

        // ── Что уберёт Delete ─────────────────────────────────────────────
        //
        // Целей две — папка и картинка внутри неё, — и они лежат в одном окне
        // одна над другой. Клавиша при этом одна, поэтому цель определяет
        // последнее нажатое: щёлкнул папку — Delete уберёт папку, щёлкнул
        // картинку — картинку. Ровно так же ведут себя проводник и любой
        // список: выделение и есть ответ на вопрос «что именно».

        private enum DeleteTarget { None, Pack, Item }

        private DeleteTarget _deleteTarget = DeleteTarget.None;

        private CharacterAvatarPackManagerItemViewModel? _selectedItem;

        /// <summary>Выбранная картинка — та, что подсвечена рамкой.</summary>
        public CharacterAvatarPackManagerItemViewModel? SelectedItem => _selectedItem;

        /// <summary>
        /// Выбрать картинку щелчком. Повторный щелчок по той же снимает
        /// выделение: иначе рамку некуда деть, кроме как выбрать соседнюю.
        /// </summary>
        public void SelectItem(CharacterAvatarPackManagerItemViewModel? item)
        {
            if (item != null && ReferenceEquals(item, _selectedItem)) item = null;

            if (_selectedItem != null) _selectedItem.IsSelected = false;
            _selectedItem = item;
            if (_selectedItem != null) _selectedItem.IsSelected = true;

            if (item != null) _deleteTarget = DeleteTarget.Item;

            this.RaisePropertyChanged(nameof(SelectedItem));
        }

        /// <summary>
        /// Убрать выделенное — папку или картинку, смотря что выбрали
        /// последним. Отдаёт false, когда убирать нечего: по этому признаку
        /// Delete остаётся необработанным и достаётся тому, кому он нужнее.
        ///
        /// Убранное возвращается тем же Ctrl+Z, что и всё остальное в этом
        /// окне: ни картинка, ни папка не трогаются на диске, пока окно
        /// открыто.
        /// </summary>
        public bool DeleteSelection()
        {
            if (_deleteTarget == DeleteTarget.Item)
            {
                var item = _selectedItem;
                if (item is null || !item.CanModify) return false;

                // Выделение снимается до удаления: плитка сейчас уйдёт из
                // списка, и держать ссылку на ушедшую незачем.
                SelectItem(null);
                item.DeleteCommand.Execute().Subscribe();
                return true;
            }

            if (_deleteTarget == DeleteTarget.Pack)
            {
                var pack = SelectedPack;
                if (pack?.CanDeletePack != true) return false;

                RemovePack(pack);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Убрать папку. Один путь на все три способа: кнопку в верхней
        /// панели, крестик на самой плитке и клавишу Delete.
        ///
        /// Папка при этом только исчезает из окна — с диска её снимет
        /// ApplyChanges при закрытии, а до тех пор Ctrl+Z возвращает её на
        /// место вместе со всем содержимым.
        /// </summary>
        /// <summary>
        /// Убрать папку по опознавателю — крестиком с её собственной плитки.
        /// Плитка при этом сначала становится выбранной: убирают ту, на
        /// которую нажали, и после отмены выбранной остаётся она же.
        /// </summary>
        private void DeletePackById(string packId)
        {
            var pack = Packs.FirstOrDefault(p => p.PackId == packId);
            if (pack is null) return;

            SelectedPack = pack;
            RemovePack(pack);
        }

        private void RemovePack(CharacterAvatarPackManagerPackViewModel? pack)
        {
            if (pack?.IsUserPack != true || pack.IsLibrary) return;

            var id = pack.PackId;
            var scope = pack.Scope;
            _removedPacks[id] = scope;

            PushStep(
                undo: () => { _removedPacks.Remove(id); Refresh(id); },
                redo: () => { _removedPacks[id] = scope; Refresh(); });

            Refresh();
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

        // Показ области не спрашивает разрешения на переезд: у встроенной папки
        // и у склада сторона тоже должна гореть — по ней видно, где они лежат.
        // Нажать её нельзя, но это дело кнопок, а не подписи.
        public bool SelectedIsLocal => SelectedPack?.EffectiveScope == CharacterAvatarPackScope.Local;
        public bool SelectedIsGlobal => SelectedPack?.EffectiveScope == CharacterAvatarPackScope.Global;
        public bool CanMakeLocal => CanMoveScope && SelectedPack?.EffectiveScope == CharacterAvatarPackScope.Global;
        public bool CanMakeGlobal => CanMoveScope && SelectedPack?.EffectiveScope == CharacterAvatarPackScope.Local;

        /// <summary>
        /// Общую папку можно положить копией в проект. У папки, которая уже в
        /// проекте, копировать нечего — она там и лежит; у той, которой копия
        /// уже назначена, второй раз назначать нечего — отменяется она через
        /// Ctrl+Z, а не повторным нажатием.
        /// </summary>
        public bool CanCopyToProject => CanMakeLocal && SelectedPack?.HasPendingCopy != true;

        public bool SelectedPackIsEmpty => SelectedPack?.IsEmpty == true;

        private string _selectedPackName = string.Empty;
        /// <summary>
        /// Имя выбранной папки, правится прямо в поле над лентой. Поле одно на
        /// всё окно: второго, «для имени новой папки», больше нет — новая папка
        /// заводится с именем по умолчанию и тут же встаёт в это же поле под
        /// правку. Держать два поля с одинаковой подписью и разным смыслом
        /// значило бы каждый раз выбирать, в какое из них писать.
        ///
        /// Запись в хранилище идёт с задержкой — см. _renameDebounce.
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

        private string _statusMessage = string.Empty;
        /// <summary>
        /// Что-то не получилось. Только это: подсказки об отмене отсюда убраны —
        /// они висели под окном всё время работы и объясняли то, что и так
        /// написано на кнопках.
        /// </summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                this.RaiseAndSetIfChanged(ref _statusMessage, value);
                ScheduleStatusHide();
            }
        }

        private void ScheduleStatusHide()
        {
            if (_statusTimer == null)
            {
                _statusTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(StatusHideMs)
                };
                _statusTimer.Tick += (_, _) =>
                {
                    _statusTimer!.Stop();
                    if (!string.IsNullOrEmpty(_statusMessage))
                    {
                        _statusMessage = string.Empty;
                        this.RaisePropertyChanged(nameof(StatusMessage));
                    }
                };
            }

            _statusTimer.Stop();
            if (!string.IsNullOrEmpty(_statusMessage)) _statusTimer.Start();
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
        public ReactiveCommand<Unit, Unit> AddImagesCommand { get; }

        /// <summary>
        /// Папка заведена. Окно ставит курсор в поле имени и выделяет его
        /// целиком: имя у новой папки временное, и следующее, что с ней делают,
        /// — переименовывают.
        /// </summary>
        public event Action? PackCreated;

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

            CreatePackCommand = ReactiveCommand.Create(CreatePack);

            DeletePackCommand = ReactiveCommand.Create(() => RemovePack(SelectedPack));

            SelectPackCommand = ReactiveCommand.Create<string>(id =>
                SelectedPack = Packs.FirstOrDefault(p => p.PackId == id));

            ImportPackCommand = ReactiveCommand.CreateFromTask(ImportAsync);
            ExportPackCommand = ReactiveCommand.CreateFromTask(ExportAsync);
            MakeLocalCommand = ReactiveCommand.Create(() => RequestScope(CharacterAvatarPackScope.Local));
            MakeGlobalCommand = ReactiveCommand.Create(() => RequestScope(CharacterAvatarPackScope.Global));
            CopyToProjectCommand = ReactiveCommand.Create(RequestCopyToProject);

            AddImagesCommand = ReactiveCommand.CreateFromTask(AddImagesAsync);

            Refresh();
        }

        /// <summary>
        /// Завести папку.
        ///
        /// Имя даётся временное, а сама папка тут же становится выбранной — и,
        /// значит, встаёт в поле имени, которое окно отдаёт под правку. Так же
        /// заводят папку в проводнике: сначала папка, потом имя. Спрашивать имя
        /// заранее отдельным полем значило бы держать два поля с одной подписью
        /// и разным смыслом.
        ///
        /// Область берётся у выбранной папки: новую заводят рядом с той, с
        /// которой сейчас работают. У встроенной папки и склада своей области
        /// нет — тогда общая.
        /// </summary>
        public void CreatePack()
        {
            var scope = CanEditPack && SelectedPack != null
                ? SelectedPack.EffectiveScope
                : CharacterAvatarPackScope.Global;

            var pack = _avatarService.CreatePack(NextPackName(), scope);
            if (pack == null)
            {
                StatusMessage = "Папку в проекте можно завести только при открытом проекте.";
                return;
            }

            StatusMessage = string.Empty;

            // Новая папка встаёт первой — так её и записала служба. Порядок
            // этого сеанса, если его успели переставить руками, пересобирается
            // с новой папкой во главе, иначе она ушла бы в хвост.
            if (_pendingPackOrder != null)
            {
                _pendingPackOrder.RemoveAll(id => string.Equals(id, pack.Id, StringComparison.Ordinal));
                _pendingPackOrder.Insert(0, pack.Id);
            }

            Refresh(pack.Id);
            PackCreated?.Invoke();
        }

        /// <summary>
        /// Имя для новой папки: «Новая папка», а если такая уже есть — со
        /// счётчиком. Совпадающие имена разрешены (папки различаются не именем),
        /// но две одинаковых подписи подряд в ленте читались бы как одна папка,
        /// показанная дважды.
        /// </summary>
        private string NextPackName()
        {
            const string baseName = "Новая папка";

            var used = new HashSet<string>(
                Packs.Select(p => p.DisplayName), StringComparer.OrdinalIgnoreCase);

            if (!used.Contains(baseName)) return baseName;

            for (var n = 2; ; n++)
            {
                var candidate = $"{baseName} {n}";
                if (!used.Contains(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Записать имя не дожидаясь задержки. Зовётся по Enter в поле: нажатие
        /// означает «я закончил», и ждать после него ещё полсекунды незачем.
        /// </summary>
        public void CommitRenameNow()
        {
            _renameDebounce?.Stop();
            CommitRename();
        }

        public void Refresh(string? selectPackId = null)
        {
            var previousId = selectPackId ?? SelectedPack?.PackId;

            // Плитки сейчас пересоберутся заново, и выбранная картинка станет
            // ссылкой на выброшенную вью-модель: Delete по ней убирал бы то,
            // чего на экране уже нет.
            var previousTarget = _deleteTarget;
            SelectItem(null);

            foreach (var pack in Packs) pack.Dispose();
            Packs.Clear();

            // Порядок папок хранит служба: он один и для этой ленты, и для
            // разделов выбора аватарки, и перестановка в окне должна значить
            // одно и то же в обоих местах.
            var all = _avatarService.GetAllPacks();

            foreach (var pack in all)
            {
                if (_removedPacks.ContainsKey(pack.Id)) continue;

                var vm = new CharacterAvatarPackManagerPackViewModel(
                    pack, _avatarService, DeleteItem, SetCover, _removedItems.Contains,
                    onDeletePack: DeletePackById);

                // Правки этого сеанса ещё не в хранилище — накладываем их на
                // свежесобранную папку, иначе она покажет старое имя и обложку.
                if (_pendingMeta.TryGetValue(pack.Id, out var meta))
                {
                    vm.DisplayName = meta.Name;
                    vm.ApplyCoverFlags(meta.Icon);
                }

                if (_pendingScopeMoves.TryGetValue(pack.Id, out var target))
                    vm.EffectiveScope = target;

                if (_pendingCopies.Contains(pack.Id))
                    vm.HasPendingCopy = true;

                if (_pendingOrder.TryGetValue(pack.Id, out var order))
                    vm.ApplyItemOrder(order);

                Packs.Add(vm);
            }

            if (_pendingPackOrder != null) ApplyPackOrder(_pendingPackOrder);

            SelectedPack = Packs.FirstOrDefault(p => p.PackId == previousId) ?? Packs.FirstOrDefault();

            // Цель удаления пересборка не меняет. Строкой выше папка выбрана
            // заново — тем же присваиванием, что и щелчком по ней, — и цель
            // от этого стала бы папкой. Это ровно тот случай, когда следующий
            // Delete убрал бы папку целиком вместо ещё одной картинки: убрали
            // картинку, список пересобрался, нажали Delete снова — и папки
            // нет. Целью после пересборки остаётся то, что выбирал человек, а
            // ушедшая картинка не оставляет цели вовсе.
            _deleteTarget = previousTarget == DeleteTarget.Item
                ? DeleteTarget.None
                : previousTarget;
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

            ApplyMeta(id, after);
            PushStep(
                undo: () => ApplyMeta(id, before),
                redo: () => ApplyMeta(id, after));
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

            ApplyMeta(id, after);
            PushStep(
                undo: () => ApplyMeta(id, before),
                redo: () => ApplyMeta(id, after));
        }

        /// <summary>
        /// Свойства папки с учётом ещё не записанных правок этого сеанса.
        /// Область берётся настоящая, а не выбранная в окне: правку имени
        /// пишут туда, где папка лежит сейчас, — переезд состоится позже и
        /// заберёт имя с собой.
        /// </summary>
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
            (CharacterAvatarPackScope Scope, string Name, string? Icon) meta)
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
        }

        // ── Область хранения ──────────────────────────────────────────────

        /// <summary>
        /// Назначить папке другую область. Сам переезд — копия всего
        /// содержимого и удаление исходника — откладывается до закрытия окна:
        /// щёлкать переключателем туда-сюда пришлось бы ценой двух переносов
        /// подряд, а до закрытия ни один из них ещё не окончателен.
        /// </summary>
        private void RequestScope(CharacterAvatarPackScope target)
        {
            var pack = SelectedPack;
            if (pack?.IsUserPack != true || pack.IsLibrary) return;

            var id = pack.PackId;
            var before = pack.EffectiveScope;
            if (before == target) return;

            ApplyScope(id, target);
            PushStep(
                undo: () => ApplyScope(id, before),
                redo: () => ApplyScope(id, target));
        }

        /// <summary>
        /// Записать выбранную область в сеанс окна. Совпала с настоящей —
        /// переезда нет вовсе: так отмена возвращает папку в исходное
        /// состояние, а не назначает обратный переезд.
        /// </summary>
        private void ApplyScope(string packId, CharacterAvatarPackScope target)
        {
            var pack = Packs.FirstOrDefault(p => p.PackId == packId);
            var actual = pack?.Scope ?? target;

            if (target == actual) _pendingScopeMoves.Remove(packId);
            else _pendingScopeMoves[packId] = target;

            if (pack != null) pack.EffectiveScope = target;

            RaiseSelectionFlags();
        }

        /// <summary>
        /// Назначить копию общей папки в проект, оставив её и в общих.
        ///
        /// Это то, что нужно при передаче проекта другому человеку: у него нет
        /// ни ваших папок, ни библиотеки, и аватарки из них у него не покажутся.
        /// Копия в архиве проекта уезжает вместе с ним, а исходник продолжает
        /// работать во всех остальных ваших проектах — в отличие от переноса,
        /// после которого в общих папки не остаётся.
        ///
        /// Копия — это чтение всех картинок папки и запись их второй раз внутрь
        /// файла проекта; на папке в полтысячи фотографий это надолго и заметно
        /// прибавляет проекту веса. Раньше всё это начиналось прямо по нажатию,
        /// и случайно задетая кнопка означала копию, которую оставалось только
        /// искать и удалять руками. Теперь нажатие лишь назначает действие:
        /// Ctrl+Z снимает его, а сама укладка идёт после закрытия окна.
        /// </summary>
        private void RequestCopyToProject()
        {
            var pack = SelectedPack;
            if (pack?.IsUserPack != true || pack.IsLibrary) return;
            if (pack.EffectiveScope == CharacterAvatarPackScope.Local) return;

            var id = pack.PackId;
            if (_pendingCopies.Contains(id)) return;

            ApplyCopyFlag(id, true);
            PushStep(
                undo: () => ApplyCopyFlag(id, false),
                redo: () => ApplyCopyFlag(id, true));
        }

        private void ApplyCopyFlag(string packId, bool pending)
        {
            if (pending) _pendingCopies.Add(packId);
            else _pendingCopies.Remove(packId);

            var pack = Packs.FirstOrDefault(p => p.PackId == packId);
            if (pack != null) pack.HasPendingCopy = pending;

            RaiseSelectionFlags();
        }

        // ── Своя история окна ─────────────────────────────────────────────
        //
        // Пока окно открыто, ни одно действие не трогает хранилище: удаление
        // снимает показ, переезд назначается, имя и порядок живут в сеансе.
        // Насовсем всё уходит при закрытии окна. Отсюда и Ctrl+Z: вернуть — это
        // снять пометку, а не восстанавливать файл.
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

        // Назначенные переезды: ключ — папка, значение — куда она поедет после
        // закрытия окна.
        private readonly Dictionary<string, CharacterAvatarPackScope> _pendingScopeMoves =
            new(StringComparer.Ordinal);

        // Назначенные копии в проект — опознавателями общих папок.
        private readonly HashSet<string> _pendingCopies = new(StringComparer.Ordinal);

        // Переставленный, но ещё не записанный порядок картинок: ключ — папка,
        // значение — имена файлов сверху вниз. Живёт по тем же правилам, что и
        // прочие правки окна: пока окно открыто, в хранилище ничего не уходит,
        // Ctrl+Z возвращает, закрытие применяет.
        private readonly Dictionary<string, List<string>> _pendingOrder =
            new(StringComparer.Ordinal);

        // Порядок самих папок — опознавателями, слева направо. null означает,
        // что папки в этом сеансе не переставляли и записывать при закрытии
        // нечего.
        private List<string>? _pendingPackOrder;

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
        /// Довести до хранилища всё, что окно накопило за сеанс: имена, обложки,
        /// удаления, порядок и назначенные переезды. Зовётся при закрытии — до
        /// этого момента ни один файл не тронут и любой шаг откатывается через
        /// Ctrl+Z.
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

            CommitPackOrderToStorage();

            _pendingMeta.Clear();
            _pendingOrder.Clear();
            _pendingPackOrder = null;
            _removedPacks.Clear();
            _removedItems.Clear();
            _undo.Clear();
            _redo.Clear();
            RaiseHistoryFlags();

            // Копии и переезды — последними и уже после закрытия окна:
            // см. StartPendingTransfers.
            StartPendingTransfers();
        }

        /// <summary>
        /// Записать порядок папок. К показанным добавляются те, которых сейчас
        /// нет ни в одной области: локальные папки принадлежат открытому
        /// проекту, и порядок папок соседнего проекта стирать нечего ради.
        /// </summary>
        private void CommitPackOrderToStorage()
        {
            if (_pendingPackOrder == null) return;

            try
            {
                var merged = Packs
                    .Select(p => p.PackId)
                    .Where(id => !_removedPacks.ContainsKey(id))
                    .ToList();

                foreach (var id in _avatarService.GetPackOrder())
                    if (!merged.Any(x => string.Equals(x, id, StringComparison.Ordinal)))
                        merged.Add(id);

                _avatarService.SetPackOrder(merged);
            }
            catch (Exception ex) { _logger.Error(ex, "ApplyChanges: pack order"); }
        }

        /// <summary>
        /// Выполнить назначенные копии и переезды после того, как окно
        /// закрылось.
        ///
        /// И то и другое — это чтение всех картинок папки и запись их на новом
        /// месте (переезд вдобавок стирает исходник); три папки подряд занимают
        /// диск на несколько секунд. Поэтому очередь идёт по одной папке за
        /// заход, с паузой между ними и на самом низком приоритете очереди
        /// сообщений: окно уже закрыто, никто не ждёт, а работа программы под
        /// этим делом останавливаться не должна.
        ///
        /// Копии идут первыми: переезд меняет опознаватель папки, и копия,
        /// назначенная той же папке, после него искала бы уже несуществующую.
        /// </summary>
        private void StartPendingTransfers()
        {
            var copies = _pendingCopies.ToList();
            _pendingCopies.Clear();

            var moves = _pendingScopeMoves
                .Select(pair => (Id: pair.Key, Target: pair.Value))
                .ToList();
            _pendingScopeMoves.Clear();

            if (copies.Count == 0 && moves.Count == 0) return;

            Dispatcher.UIThread.Post(async () =>
            {
                foreach (var id in copies)
                {
                    try
                    {
                        var copied = await _avatarService.CopyPackToScopeAsync(
                            id, CharacterAvatarPackScope.Local);

                        if (copied == null)
                            _logger.Error("Deferred copy: pack {Id} did not reach the project", id);
                    }
                    catch (Exception ex) { _logger.Error(ex, "Deferred copy failed: {Id}", id); }

                    await Task.Delay(MoveGapMs);
                }

                foreach (var (id, target) in moves)
                {
                    try { await MoveScopeAsync(id, target); }
                    catch (Exception ex) { _logger.Error(ex, "Deferred move failed: {Id}", id); }

                    await Task.Delay(MoveGapMs);
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Перенести папку и переставить её опознаватель в порядке папок: при
        /// переносе опознаватель меняется, и место в ленте иначе потерялось бы.
        /// </summary>
        private async Task MoveScopeAsync(string packId, CharacterAvatarPackScope target)
        {
            var order = _avatarService.GetPackOrder().ToList();
            var at = order.FindIndex(id => string.Equals(id, packId, StringComparison.Ordinal));

            var moved = await _avatarService.MovePackToScopeAsync(packId, target);
            if (moved == null)
            {
                _logger.Error("Deferred move: pack {Id} stayed where it was", packId);
                return;
            }

            var next = _avatarService.GetPackOrder().ToList();
            next.RemoveAll(id =>
                string.Equals(id, packId, StringComparison.Ordinal)
                || string.Equals(id, moved.Id, StringComparison.Ordinal));

            if (at < 0 || at > next.Count) next.Insert(0, moved.Id);
            else next.Insert(at, moved.Id);

            _avatarService.SetPackOrder(next);
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
        }

        private void SetPendingOrder(string packId, List<string>? pending, List<string> visible)
        {
            if (pending == null) _pendingOrder.Remove(packId);
            else _pendingOrder[packId] = new List<string>(pending);

            Packs.FirstOrDefault(p => p.PackId == packId)?.ApplyItemOrder(visible);
        }

        /// <summary>
        /// Записать новый порядок папок в сеанс окна. Лента уже переставлена
        /// перетаскиванием — здесь только запоминается, что и куда возвращать.
        /// </summary>
        public void CommitPackOrder(List<string> before, List<string> after)
        {
            if (before.SequenceEqual(after, StringComparer.Ordinal)) return;

            var restore = _pendingPackOrder != null
                ? new List<string>(_pendingPackOrder)
                : before;

            PushStep(
                undo: () => SetPendingPackOrder(restore),
                redo: () => SetPendingPackOrder(after));

            _pendingPackOrder = new List<string>(after);
        }

        private void SetPendingPackOrder(List<string> order)
        {
            _pendingPackOrder = new List<string>(order);
            ApplyPackOrder(order);
        }

        /// <summary>
        /// Разложить ленту по списку опознавателей. Двигаем существующие
        /// плитки: внутри каждой лежат прочитанные обложка и картинки, и
        /// пересборка ради перестановки читала бы с диска всё заново.
        /// </summary>
        private void ApplyPackOrder(IReadOnlyList<string> packIds)
        {
            var target = 0;
            foreach (var id in packIds)
            {
                var current = -1;
                for (var i = target; i < Packs.Count; i++)
                    if (string.Equals(Packs[i].PackId, id, StringComparison.Ordinal))
                    { current = i; break; }

                if (current < 0) continue;
                if (current != target) Packs.Move(current, target);
                target++;
            }
        }

        private void DeleteItem(string avatarRef)
        {
            var packId = SelectedPack?.PackId;

            _removedItems.Add(avatarRef);
            PushStep(
                undo: () => { _removedItems.Remove(avatarRef); Refresh(packId); },
                redo: () => { _removedItems.Add(avatarRef); Refresh(packId); });

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

            if (_pendingPackOrder != null)
            {
                _pendingPackOrder.RemoveAll(id => string.Equals(id, pack.Id, StringComparison.Ordinal));
                _pendingPackOrder.Insert(0, pack.Id);
            }

            Refresh(pack.Id);
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
