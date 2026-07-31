using Avalonia.Media.Imaging;
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
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels.Tabs
{
    public class CharacterBasicsTabViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterBasicsTabViewModel>();

        private readonly ICharacterService _characterService;
        private readonly ICharacterAnketaService? _anketaService;
        private readonly string _characterId;

        /// <summary>
        /// Состав карточки изменился: подключён или отключён набор полей.
        /// Карточка пересобирает вкладку параметров — поля живут там, а состав
        /// задаётся здесь, потому что это свойство ядра, а не значений.
        /// </summary>
        public event Action? AnketasChanged;

        /// <summary>Наборы, которые можно подключить.</summary>
        public ObservableCollection<CharacterAnketa> AvailableAnketas { get; } = new();

        /// <summary>Наборы, из которых составлена карточка.</summary>
        public ObservableCollection<CharacterAnketa> AttachedAnketas { get; } = new();

        public bool HasAttachedAnketas => AttachedAnketas.Count > 0;
        public bool CanAttachAnketa => _anketaService != null && AvailableAnketas.Count > 0;

        /// <summary>
        /// В проекте есть хоть один набор. Пока их нет вовсе, раздел
        /// не показывается: пустой блок про механику, которой человек
        /// не пользуется, только отнимает внимание.
        /// </summary>
        public bool HasAnySets => AttachedAnketas.Count > 0 || AvailableAnketas.Count > 0;

        public ICharacterAvatarService? AvatarService { get; }
        public string CharacterId => _characterId;

        // Дополнительные имена персонажа: всё, кроме отображаемого. Отображаемое
        // живёт в Name и правится в шапке карточки — дублировать его чипом
        // означало бы два места правки одного значения.
        public ObservableCollection<CharacterNameEntry> AlternateNames { get; } = new();

        // Aliases и ActiveStatuses из вьюмодели убраны: первое стало дублем
        // списка имён, второе — мёртвым остатком механики статусов, которую
        // заменили метки. В самой модели оба поля остались нетронутыми,
        // старые проекты читаются и сохраняются как прежде.
        public ObservableCollection<string> Tags { get; } = new();

        /// <summary>
        /// Теги, уже заведённые в проекте. Подсказываются при вводе: без этого
        /// на трёх сотнях персонажей «второстепенный», «второстипенный»
        /// и «Второстепенный» становятся тремя разными тегами, и фильтр
        /// перестаёт работать.
        /// </summary>
        public ObservableCollection<string> KnownTags { get; } = new();

        /// <summary>
        /// Имена меток, уже заведённых в проекте. Ввод по совпадению имени
        /// подхватывает саму метку со значком, цветом и эффектом: «Ранен»
        /// должен выглядеть одинаково у всех, кто ранен.
        /// </summary>
        public ObservableCollection<string> KnownLabelNames { get; } = new();

        /// <summary>
        /// Картинки персонажа. Не пикер аватаров: тот про выбор одного значка,
        /// а здесь эскизы, референсы и арт, на которые смотрят, когда пишут
        /// сцену. Любую можно сделать аватаром.
        /// </summary>
        public ObservableCollection<CharacterGalleryItemViewModel> Gallery { get; } = new();

        /// <summary>Есть ли настоящие картинки — плитка добавления не в счёт.</summary>
        public bool HasGallery => Gallery.Any(g => !g.IsAddTile);

        /// <summary>
        /// Держит плитку «добавить» последней в сетке. Вызывается после любого
        /// изменения галереи.
        /// </summary>
        private void EnsureAddTile()
        {
            var existing = Gallery.FirstOrDefault(g => g.IsAddTile);
            if (existing != null) Gallery.Remove(existing);

            Gallery.Add(new CharacterGalleryItemViewModel());

            this.RaisePropertyChanged(nameof(HasGallery));
        }

        /// <summary>
        /// Групповые обращения: «все из этой папки зовут её так». Средняя
        /// ступень каскада между личным правилом из связи и общим обращением.
        /// </summary>
        public ObservableCollection<CharacterGroupAddress> GroupAddresses { get; } = new();

        public bool HasGroupAddresses => GroupAddresses.Count > 0;

        /// <summary>Папки проекта — из них выбирается группа обращающихся.</summary>
        public ObservableCollection<CharacterFolder> AddressFolders { get; } = new();

        public bool CanAddGroupAddress => AddressFolders.Count > 0;

        /// <summary>
        /// Папки задаёт вьюмодель модуля: карточка о них не знает, а отдельный
        /// справочник групп заводить незачем — «друзья Алины» это и есть папка.
        /// </summary>
        public void SetAddressFolders(IEnumerable<CharacterFolder> folders)
        {
            AddressFolders.Clear();
            foreach (var folder in folders) AddressFolders.Add(folder);

            this.RaisePropertyChanged(nameof(CanAddGroupAddress));
        }

        public void AddGroupAddress(string folderId, string input)
        {
            if (string.IsNullOrWhiteSpace(folderId)) return;

            var trimmed = input?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;

            var (value, occasion) = SplitNameAndNote(trimmed);
            if (string.IsNullOrEmpty(value)) return;

            GroupAddresses.Add(new CharacterGroupAddress
            {
                FolderId = folderId,
                Value = value,
                Occasion = occasion
            });

            this.RaisePropertyChanged(nameof(HasGroupAddresses));
        }

        public void RemoveGroupAddress(string id)
        {
            var item = GroupAddresses.FirstOrDefault(g => g.Id == id);
            if (item == null) return;

            GroupAddresses.Remove(item);
            this.RaisePropertyChanged(nameof(HasGroupAddresses));
        }
        public ObservableCollection<CharacterLabel> Labels { get; } = new();

        public ReactiveCommand<Unit, Unit> OpenPickerCommand { get; }
        public ReactiveCommand<Unit, Unit> DeleteAvatarCommand { get; }
        public ReactiveCommand<string, Unit> AddNameCommand { get; }
        public ReactiveCommand<string, Unit> RemoveTagCommand { get; }
        public ReactiveCommand<string, Unit> AddTagCommand { get; }
        public ReactiveCommand<string, Unit> AddLabelCommand { get; }
        public ReactiveCommand<string, Unit> RemoveLabelCommand { get; }

        // «Применить кольцо ко всем» из редактора цвета. Обработчик вешает
        // вьюмодель модуля — у неё есть доступ ко всем персонажам и стеку Undo.
        public Action<bool>? OnApplyRingToAll { get; set; }
        public ReactiveCommand<bool, Unit> ApplyRingToAllCommand { get; }

        // Встроенная метка «Мёртв»: чекбокс в форме добавляет/убирает её
        // в коллекции меток. Изменение коллекции поднимает IsDead — подписка
        // в конструкторе, чтобы удаление метки крестиком снимало галочку.
        public bool IsDead
        {
            get => Labels.Any(l => l.Id == CharacterBuiltinLabels.DeadId);
            set
            {
                var existing = Labels.FirstOrDefault(l => l.Id == CharacterBuiltinLabels.DeadId);
                if (value && existing == null)
                    Labels.Add(CharacterBuiltinLabels.CreateDead(
                        Writersword.Src.Modules.Characters.Resources.CharactersStrings.Label_Dead));
                else if (!value && existing != null)
                    Labels.Remove(existing);
            }
        }

        /// <summary>
        /// Метка, чей значок выводится бейджем поверх аватара в карточке:
        /// первая по порядку пользователя из тех, что помечены к показу.
        /// Тот же принцип, что на карточках списков — смысл несёт объект.
        /// </summary>
        public CharacterLabel? CardBadge =>
            Labels.Where(l => l.ShowOnCard).OrderBy(l => l.Order).FirstOrDefault();

        public bool HasCardBadge => CardBadge != null;

        public Func<Task<string?>>? RequestPickerOpen { get; set; }

        // Немедленное сохранение в обход задержки автосейва: вью вызывает
        // RequestImmediateSave по Enter в поле имени, карточка подписана
        // на событие и сохраняет персонажа сразу.
        public event Action? ImmediateSaveRequested;
        public void RequestImmediateSave() => ImmediateSaveRequested?.Invoke();

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                this.RaiseAndSetIfChanged(ref _name, value);
                this.RaisePropertyChanged(nameof(AvatarGlyph));
            }
        }

        private string _shortDescription = string.Empty;
        public string ShortDescription { get => _shortDescription; set => this.RaiseAndSetIfChanged(ref _shortDescription, value); }

        private string _note = string.Empty;
        public string Note { get => _note; set => this.RaiseAndSetIfChanged(ref _note, value); }

        /// <summary>
        /// Как зовут те, у кого нет своего правила. Личные варианты живут
        /// в связях: обращение принадлежит отношениям, а не персонажу.
        /// </summary>
        private string _defaultAddress = string.Empty;
        public string DefaultAddress { get => _defaultAddress; set => this.RaiseAndSetIfChanged(ref _defaultAddress, value); }

        // Пометка отображаемого имени («после перехода», «в детстве»).
        // Интерфейса у неё пока нет, но при смене основного имени она
        // переезжает вместе со своим значением и не теряется.
        private string _primaryNameNote = string.Empty;

        private string _color = "#607D8B";
        public string Color { get => _color; set => this.RaiseAndSetIfChanged(ref _color, value); }

        private string _fallbackIcon = "?";
        public string FallbackIcon
        {
            get => _fallbackIcon;
            set
            {
                this.RaiseAndSetIfChanged(ref _fallbackIcon, value);
                this.RaisePropertyChanged(nameof(AvatarGlyph));
            }
        }

        // Символ в круге аватара без фото: заданная иконка или первая буква
        // имени. Поля ввода иконки в форме нет — символ выводится сам.
        public string AvatarGlyph => CharacterGlyph.Resolve(_fallbackIcon, _name);

        private string? _avatarPath;
        public string? AvatarPath
        {
            get => _avatarPath;
            set { this.RaiseAndSetIfChanged(ref _avatarPath, value); ReloadBitmap(); }
        }

        private Bitmap? _avatarBitmap;
        public Bitmap? AvatarBitmap
        {
            get => _avatarBitmap;
            private set { _avatarBitmap?.Dispose(); this.RaiseAndSetIfChanged(ref _avatarBitmap, value); }
        }

        private bool _avatarRing;
        public bool AvatarRing { get => _avatarRing; set => this.RaiseAndSetIfChanged(ref _avatarRing, value); }

        // Закладка-ленточка карточки группы — редактируется из редактора цвета,
        // как и кольцо; показывается только у групповых персонажей.
        private bool _groupBookmark;
        public bool GroupBookmark { get => _groupBookmark; set => this.RaiseAndSetIfChanged(ref _groupBookmark, value); }

        private CharacterImportanceLevel _importanceLevel = CharacterImportanceLevel.Secondary;
        public CharacterImportanceLevel ImportanceLevel
        {
            get => _importanceLevel;
            set
            {
                this.RaiseAndSetIfChanged(ref _importanceLevel, value);
                this.RaisePropertyChanged(nameof(ImportanceLevelName));
                this.RaisePropertyChanged(nameof(ImportanceLevelHint));
            }
        }

        /// <summary>
        /// Название выбранной ступени словами. Стоит рядом с цифрами: сами по
        /// себе I, II и III ничего не говорят, а раскрывать их только подсказкой
        /// значит требовать наводить курсор ради простого вопроса.
        /// </summary>
        public string ImportanceLevelName => _importanceLevel switch
        {
            CharacterImportanceLevel.Primary => CharactersStrings.Importance_Primary,
            CharacterImportanceLevel.Secondary => CharactersStrings.Importance_Secondary,
            CharacterImportanceLevel.Tertiary => CharactersStrings.Importance_Tertiary,
            _ => CharactersStrings.Importance_Custom
        };

        /// <summary>Строка о том, что выбранная ступень значит.</summary>
        public string ImportanceLevelHint => _importanceLevel switch
        {
            CharacterImportanceLevel.Primary => CharactersStrings.Importance_PrimaryHint,
            CharacterImportanceLevel.Secondary => CharactersStrings.Importance_SecondaryHint,
            CharacterImportanceLevel.Tertiary => CharactersStrings.Importance_TertiaryHint,
            _ => CharactersStrings.Importance_CustomHint
        };

        private string _customImportanceLabel = string.Empty;
        public string CustomImportanceLabel { get => _customImportanceLabel; set => this.RaiseAndSetIfChanged(ref _customImportanceLabel, value); }

        private string _narrativeStartPoint = string.Empty;
        public string NarrativeStartPoint { get => _narrativeStartPoint; set => this.RaiseAndSetIfChanged(ref _narrativeStartPoint, value); }

        private string _narrativeEndPoint = string.Empty;
        public string NarrativeEndPoint { get => _narrativeEndPoint; set => this.RaiseAndSetIfChanged(ref _narrativeEndPoint, value); }

        private bool _isCollective;
        public bool IsCollective { get => _isCollective; set => this.RaiseAndSetIfChanged(ref _isCollective, value); }

        private string _populationNote = string.Empty;
        public string PopulationNote { get => _populationNote; set => this.RaiseAndSetIfChanged(ref _populationNote, value); }

        public CharacterBasicsTabViewModel(
            ICharacterService characterService,
            Character character,
            ICharacterAvatarService? avatarService = null,
            ICharacterAnketaService? anketaService = null)
        {
            _characterService = characterService;
            _characterId = character.Id;
            AvatarService = avatarService;
            _anketaService = anketaService;

            LoadFrom(character);
            ReloadKnownTags();
            ReloadKnownLabels();
            ReloadAnketas(character);

            OpenPickerCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (RequestPickerOpen == null) return;
                var selectedRef = await RequestPickerOpen();
                if (selectedRef != null) ApplyAvatarRef(selectedRef);
            });

            DeleteAvatarCommand = ReactiveCommand.Create(() =>
            {
                AvatarService?.DeleteAvatar(_avatarPath);
                AvatarPath = null;
                var c = _characterService.GetById(_characterId);
                if (c != null) { c.AvatarPath = null; _characterService.Update(c); }
            });

            AddNameCommand = ReactiveCommand.Create<string>(input =>
            {
                var trimmed = input?.Trim();
                if (string.IsNullOrEmpty(trimmed)) return;

                // Имя и пометку можно ввести одной строкой: «Диана — после
                // перехода». Отдельного редактора под пометку нет намеренно —
                // ввод остаётся потоковым, без ухода в диалог на каждое имя.
                var (value, note) = SplitNameAndNote(trimmed);
                if (string.IsNullOrEmpty(value)) return;

                if (string.Equals(value, Name, StringComparison.CurrentCultureIgnoreCase)) return;
                if (AlternateNames.Any(n => string.Equals(n.Value, value, StringComparison.CurrentCultureIgnoreCase))) return;

                AlternateNames.Add(new CharacterNameEntry { Value = value, Note = note });
            });
            RemoveTagCommand = ReactiveCommand.Create<string>(t => Tags.Remove(t));
            AddTagCommand = ReactiveCommand.Create<string>(t =>
            {
                var tag = t?.Trim();
                if (!string.IsNullOrEmpty(tag) && !Tags.Contains(tag)) Tags.Add(tag);
            });
            AddLabelCommand = ReactiveCommand.Create<string>(name =>
            {
                var trimmed = name?.Trim();
                if (string.IsNullOrEmpty(trimmed)) return;
                if (Labels.Any(l => string.Equals(l.Name, trimmed, StringComparison.CurrentCultureIgnoreCase))) return;

                // Метка с таким именем уже есть в проекте — берём её целиком,
                // со значком, цветом и эффектом. Иначе «Ранен» у одного
                // персонажа был бы с каплей крови, а у другого — безымянным
                // кружком по умолчанию.
                var known = _characterService.GetAllLabels()
                    .FirstOrDefault(l => string.Equals(l.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));

                if (known != null)
                {
                    Labels.Add(new CharacterLabel
                    {
                        // У встроенных меток Id общий для всех персонажей —
                        // по нему они опознаются как встроенные.
                        Id = known.IsBuiltIn ? known.Id : Guid.NewGuid().ToString(),
                        Name = known.Name,
                        Icon = known.Icon,
                        IconImage = known.IconImage,
                        Color = known.Color,
                        Effect = known.Effect,
                        ShowOnCard = known.ShowOnCard,
                        Description = known.Description,
                        Order = Labels.Count
                    });
                    return;
                }

                Labels.Add(new CharacterLabel
                {
                    Name = trimmed,
                    Order = Labels.Count
                });
            });
            RemoveLabelCommand = ReactiveCommand.Create<string>(id =>
            {
                var label = Labels.FirstOrDefault(l => l.Id == id);
                if (label != null) Labels.Remove(label);
            });
            ApplyRingToAllCommand = ReactiveCommand.Create<bool>(v => OnApplyRingToAll?.Invoke(v));

            // Любое изменение набора меток пересчитывает производные: признак
            // «мёртв» (от него зависит показ кнопки быстрой отметки) и бейдж
            // поверх аватара.
            Labels.CollectionChanged += (_, _) =>
            {
                this.RaisePropertyChanged(nameof(IsDead));
                this.RaisePropertyChanged(nameof(CardBadge));
                this.RaisePropertyChanged(nameof(HasCardBadge));
            };
        }

        /// <summary>
        /// Применить метку из редактора: существующая заменяется по Id (замена
        /// элемента коллекции перерисовывает чип и триггерит автосейв), новая
        /// добавляется в конец. Порядок перенумеровывается.
        /// </summary>
        public void UpsertLabel(CharacterLabel label)
        {
            var index = -1;
            for (int i = 0; i < Labels.Count; i++)
                if (Labels[i].Id == label.Id) { index = i; break; }

            if (index >= 0) Labels[index] = label;
            else Labels.Add(label);

            RenumberLabels();
        }

        /// <summary>
        /// Разбор строки ввода на имя и пометку. Разделителем считается тире
        /// с пробелами по краям: дефис внутри слова («Жан-Люк») именем и
        /// остаётся, а «Диана — после перехода» разъезжается на две части.
        /// </summary>
        private static (string Value, string Note) SplitNameAndNote(string input)
        {
            var separators = new[] { " — ", " – ", " - " };

            foreach (var separator in separators)
            {
                var index = input.IndexOf(separator, StringComparison.Ordinal);
                if (index <= 0) continue;

                var value = input.Substring(0, index).Trim();
                var note = input.Substring(index + separator.Length).Trim();
                return (value, note);
            }

            return (input, string.Empty);
        }

        /// <summary>
        /// Пересобрать список известных тегов проекта. Вызывается при открытии
        /// карточки: тег, заведённый у другого персонажа минуту назад, должен
        /// подсказаться здесь.
        /// </summary>
        public void ReloadKnownTags()
        {
            // Теги открытой карточки берём из вьюмодели, а не из сервиса:
            // в модель персонажа они попадут только после автосохранения,
            // а подсказаться должны сразу.
            var known = _characterService.GetAllTags()
                .Concat(Tags)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            KnownTags.Clear();
            foreach (var tag in known) KnownTags.Add(tag);
        }

        /// <summary>
        /// Пересобрать списки наборов: доступные и подключённые к карточке.
        /// Неизвестные идентификаторы пропускаются молча — набор мог приехать
        /// с чужим проектом и не найтись в библиотеке, данные при этом
        /// остаются в модели.
        /// </summary>
        private void ReloadAnketas(Character character)
        {
            AvailableAnketas.Clear();
            AttachedAnketas.Clear();

            if (_anketaService == null)
            {
                this.RaisePropertyChanged(nameof(HasAttachedAnketas));
                this.RaisePropertyChanged(nameof(CanAttachAnketa));
                this.RaisePropertyChanged(nameof(HasAnySets));
                return;
            }

            var attachedIds = character.AttachedAnketaIds;

            foreach (var anketa in _anketaService.GetAll())
            {
                if (attachedIds.Contains(anketa.Id)) AttachedAnketas.Add(anketa);
                else AvailableAnketas.Add(anketa);
            }

            this.RaisePropertyChanged(nameof(HasAttachedAnketas));
            this.RaisePropertyChanged(nameof(CanAttachAnketa));
            this.RaisePropertyChanged(nameof(HasAnySets));
        }

        /// <summary>Подключить набор к карточке.</summary>
        public void AttachAnketa(string anketaId)
        {
            if (_anketaService == null) return;

            var anketa = _anketaService.GetById(anketaId);
            if (anketa == null) return;

            _characterService.ApplyAnketa(_characterId, anketa, false);

            var updated = _characterService.GetById(_characterId);
            if (updated != null) ReloadAnketas(updated);

            AnketasChanged?.Invoke();
        }

        /// <summary>
        /// Отключить набор. Значения полей остаются: отключение — про состав
        /// карточки, а не про удаление написанного.
        /// </summary>
        public void DetachAnketa(string anketaId)
        {
            _characterService.DetachAnketa(_characterId, anketaId);

            var updated = _characterService.GetById(_characterId);
            if (updated != null) ReloadAnketas(updated);

            AnketasChanged?.Invoke();
        }

        /// <summary>
        /// Пересобрать подсказки меток. Метки самой карточки тоже попадают
        /// в список: удалил по ошибке — вернёшь вводом имени, а не заведением
        /// новой похожей.
        /// </summary>
        public void ReloadKnownLabels()
        {
            var known = _characterService.GetAllLabels()
                .Select(l => l.Name)
                .Concat(Labels.Select(l => l.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            KnownLabelNames.Clear();
            foreach (var name in known) KnownLabelNames.Add(name);
        }

        /// <summary>
        /// Добавить картинку в галерею. Файл кладётся в проект тем же путём,
        /// что и аватары, — галерея уезжает вместе с проектом.
        /// </summary>
        public async Task AddGalleryImageAsync(byte[] imageData, string fileName)
        {
            if (AvatarService == null) return;

            var imageRef = await AvatarService.SaveToProjectAsync(imageData, fileName);
            if (string.IsNullOrEmpty(imageRef)) return;

            // Новая картинка встаёт перед плиткой добавления, а не после неё.
            var addTile = Gallery.FirstOrDefault(g => g.IsAddTile);
            var index = addTile != null ? Gallery.IndexOf(addTile) : Gallery.Count;

            Gallery.Insert(index, new CharacterGalleryItemViewModel(imageRef, AvatarService));

            this.RaisePropertyChanged(nameof(HasGallery));
            RequestImmediateSave();
        }

        /// <summary>
        /// Убрать картинку из галереи. Файл удаляется, только если он не стоит
        /// аватаром: иначе аватар остался бы битой ссылкой.
        /// </summary>
        public void RemoveGalleryImage(string imageRef)
        {
            var item = Gallery.FirstOrDefault(g => g.ImageRef == imageRef);
            if (item == null) return;

            Gallery.Remove(item);
            item.Dispose();

            if (!string.Equals(AvatarPath, imageRef, StringComparison.Ordinal))
                AvatarService?.DeleteAvatar(imageRef);

            this.RaisePropertyChanged(nameof(HasGallery));
            RequestImmediateSave();
        }

        /// <summary>
        /// Поставить картинку из галереи аватаром — через контекстное меню,
        /// а не щелчком: аватар слишком легко сменить случайно, водя мышью
        /// по галерее.
        ///
        /// Действие отменяемое: прежний аватар возвращается общим Ctrl+Z
        /// модуля. Смена аватара — вещь, которую делают на пробу.
        /// </summary>
        public void UseAsAvatar(string imageRef)
        {
            if (string.IsNullOrEmpty(imageRef)) return;
            if (string.Equals(AvatarPath, imageRef, StringComparison.Ordinal)) return;

            var previous = AvatarPath;

            if (PushUndoableAvatarChange != null)
            {
                PushUndoableAvatarChange(previous, imageRef);
                return;
            }

            ApplyAvatarRef(imageRef);
            RequestImmediateSave();
        }

        /// <summary>
        /// Кладёт смену аватара в общий стек отмены модуля. Ставится карточкой:
        /// стек живёт во вьюмодели модуля, а вкладка о нём не знает.
        /// </summary>
        public Action<string?, string?>? PushUndoableAvatarChange { get; set; }

        /// <summary>Применить аватар без записи в стек отмены — вызов из отмены.</summary>
        public void ApplyAvatarSilently(string? imageRef)
        {
            AvatarPath = imageRef;

            var character = _characterService.GetById(_characterId);
            if (character != null)
            {
                character.AvatarPath = imageRef;
                _characterService.Update(character);
            }

            RequestImmediateSave();
        }

        // ── перенос картинок галереи ──────────────────────────────────────
        // Тот же порядок работы, что у карточек персонажей в списке: на время
        // переноса картинка из сетки убирается, вместо неё встаёт копия-место,
        // и она ездит по сетке вслед за курсором. Соседи при этом двигаются
        // сами, а вью доигрывает их перемещение переходом.

        private CharacterGalleryItemViewModel? _galleryPlaceholder;
        private CharacterGalleryItemViewModel? _galleryDragItem;
        private int _galleryDragOriginalIndex = -1;

        /// <summary>Сколько в галерее картинок без плитки добавления.</summary>
        public int GalleryImageCount => Gallery.Count(g => !g.IsAddTile);

        /// <summary>Где сейчас стоит место вставки, или -1.</summary>
        public int GalleryPlaceholderIndex =>
            _galleryPlaceholder == null ? -1 : Gallery.IndexOf(_galleryPlaceholder);

        /// <summary>Убрать картинку из сетки и поставить на её место копию.</summary>
        public bool BeginGalleryDrag(string imageRef)
        {
            if (_galleryPlaceholder != null) return false;

            var item = Gallery.FirstOrDefault(g => !g.IsAddTile && g.ImageRef == imageRef);
            if (item == null) return false;

            _galleryDragItem = item;
            _galleryDragOriginalIndex = Gallery.IndexOf(item);
            _galleryPlaceholder = new CharacterGalleryItemViewModel(item) { IsPlaceholder = true };

            Gallery[_galleryDragOriginalIndex] = _galleryPlaceholder;
            return true;
        }

        /// <summary>
        /// Передвинуть место вставки. Перестановка идёт через Move, а не через
        /// пару удаление-вставка: так список сохраняет элементы плиток, и их
        /// переходы не обрываются на каждом шаге.
        /// </summary>
        public void UpdateGalleryDrag(int targetIndex)
        {
            if (_galleryPlaceholder == null) return;

            var current = Gallery.IndexOf(_galleryPlaceholder);
            if (current < 0) return;

            var last = GalleryImageCount - 1;
            var dest = Math.Clamp(targetIndex, 0, Math.Max(0, last));
            if (dest == current) return;

            Gallery.Move(current, dest);
        }

        /// <summary>Вернуть картинку на место вставки и сохранить порядок.</summary>
        public void CommitGalleryDrag()
        {
            if (_galleryPlaceholder == null || _galleryDragItem == null)
            {
                ClearGalleryDragState();
                return;
            }

            var index = Gallery.IndexOf(_galleryPlaceholder);
            if (index < 0) index = Math.Min(_galleryDragOriginalIndex, Gallery.Count);

            Gallery[index] = _galleryDragItem;
            _galleryPlaceholder.Dispose();

            var moved = index != _galleryDragOriginalIndex;
            ClearGalleryDragState();

            if (moved) RequestImmediateSave();
        }

        /// <summary>Перенос отменён — картинка возвращается туда, где стояла.</summary>
        public void CancelGalleryDrag()
        {
            if (_galleryPlaceholder != null && _galleryDragItem != null)
            {
                var index = Gallery.IndexOf(_galleryPlaceholder);
                if (index >= 0) Gallery.RemoveAt(index);
                _galleryPlaceholder.Dispose();

                var back = Math.Clamp(_galleryDragOriginalIndex, 0, Gallery.Count);
                Gallery.Insert(back, _galleryDragItem);
            }

            ClearGalleryDragState();
        }

        private void ClearGalleryDragState()
        {
            _galleryPlaceholder = null;
            _galleryDragItem = null;
            _galleryDragOriginalIndex = -1;
        }

        /// <summary>Убрать имя из списка дополнительных.</summary>
        public void RemoveName(string id)
        {
            var entry = AlternateNames.FirstOrDefault(n => n.Id == id);
            if (entry != null) AlternateNames.Remove(entry);
        }

        /// <summary>
        /// Сделать выбранное имя отображаемым. Прежнее отображаемое не
        /// теряется — занимает освободившееся место в списке вместе со своей
        /// пометкой, поэтому порядок остальных имён не сбивается.
        /// </summary>
        public void MakePrimaryName(string id)
        {
            var index = -1;
            for (int i = 0; i < AlternateNames.Count; i++)
                if (AlternateNames[i].Id == id) { index = i; break; }
            if (index < 0) return;

            var picked = AlternateNames[index];
            var previousValue = Name;
            var previousNote = _primaryNameNote;

            Name = picked.Value;
            _primaryNameNote = picked.Note;

            if (!string.IsNullOrWhiteSpace(previousValue))
                AlternateNames[index] = new CharacterNameEntry
                {
                    Value = previousValue,
                    Note = previousNote
                };
            else
                AlternateNames.RemoveAt(index);
        }

        /// <summary>
        /// Переставить метку на место другой — перетаскиванием. Стрелки в чипе
        /// остаются: на трёх-четырёх метках они быстрее, а перетаскивание
        /// выигрывает, когда меток десяток.
        /// </summary>
        public void MoveLabelTo(string id, string targetId)
        {
            if (string.Equals(id, targetId, StringComparison.Ordinal)) return;

            var from = -1;
            var to = -1;
            for (int i = 0; i < Labels.Count; i++)
            {
                if (Labels[i].Id == id) from = i;
                if (Labels[i].Id == targetId) to = i;
            }

            if (from < 0 || to < 0) return;

            Labels.Move(from, to);
            RenumberLabels();
        }

        /// <summary>Сдвиг метки в порядке показа: delta -1 — влево, +1 — вправо.</summary>
        public void MoveLabel(string id, int delta)
        {
            var index = -1;
            for (int i = 0; i < Labels.Count; i++)
                if (Labels[i].Id == id) { index = i; break; }
            if (index < 0) return;

            var target = index + delta;
            if (target < 0 || target >= Labels.Count) return;

            Labels.Move(index, target);
            RenumberLabels();
        }

        // Порядок в коллекции — источник истины; Order в моделях приводится
        // к нему после каждого изменения, чтобы карточки списка сортировали
        // метки так же, как форма.
        private void RenumberLabels()
        {
            for (int i = 0; i < Labels.Count; i++)
                Labels[i].Order = i;
        }

        public async Task SetAvatarFromBytesAsync(byte[] imageData, string fileName)
        {
            if (AvatarService == null) return;
            var avatarRef = await AvatarService.SaveToProjectAsync(imageData, fileName);
            if (avatarRef != null) ApplyAvatarRef(avatarRef);
        }

        public void ApplyAvatarRef(string avatarRef)
        {
            AvatarPath = avatarRef;
            var c = _characterService.GetById(_characterId);
            if (c != null) { c.AvatarPath = avatarRef; _characterService.Update(c); }
        }

        private void ReloadBitmap()
        {
            if (AvatarService == null || string.IsNullOrEmpty(_avatarPath)) { AvatarBitmap = null; return; }
            try { AvatarBitmap = AvatarService.LoadBitmap(_avatarPath); }
            catch (Exception ex) { _logger.Error(ex, "Reload bitmap failed"); AvatarBitmap = null; }
        }

        private void LoadFrom(Character c)
        {
            _name = c.Name; _shortDescription = c.ShortDescription; _note = c.Note;
            _defaultAddress = c.DefaultAddress;

            GroupAddresses.Clear();
            foreach (var group in c.GroupAddresses ?? new List<CharacterGroupAddress>())
                GroupAddresses.Add(group);
            _color = c.Color; _fallbackIcon = c.FallbackIcon;
            _avatarPath = c.AvatarPath; _importanceLevel = c.ImportanceLevel;
            _customImportanceLabel = c.CustomImportanceLabel;
            _narrativeStartPoint = c.NarrativeStartPoint; _narrativeEndPoint = c.NarrativeEndPoint;
            _isCollective = c.IsCollective; _populationNote = c.PopulationNote;
            _avatarRing = c.AvatarRing; _groupBookmark = c.GroupBookmark;

            // Список имён приводится к рабочему виду и здесь: карточка может
            // открыться для только что созданного персонажа, который через
            // загрузку проекта не проходил.
            CharacterNames.Normalize(c);
            _name = c.Name;
            _primaryNameNote = c.Names.Count > 0 ? c.Names[0].Note : string.Empty;

            AlternateNames.Clear();
            foreach (var entry in c.Names.Skip(1)) AlternateNames.Add(entry);

            Tags.Clear(); foreach (var t in c.Tags) Tags.Add(t);

            foreach (var item in Gallery) item.Dispose();
            Gallery.Clear();
            foreach (var imageRef in c.Gallery ?? new List<string>())
                Gallery.Add(new CharacterGalleryItemViewModel(imageRef, AvatarService));
            EnsureAddTile();

            Labels.Clear();
            foreach (var l in c.Labels.OrderBy(l => l.Order))
            {
                CharacterBuiltinLabels.NormalizeBuiltIn(l);
                Labels.Add(l);
            }

            if (AvatarService != null && !string.IsNullOrEmpty(_avatarPath))
                try { _avatarBitmap = AvatarService.LoadBitmap(_avatarPath); }
                catch (Exception ex) { _logger.Error(ex, "Initial avatar load failed"); }
        }

        public void ApplyTo(Character character)
        {
            character.Name = Name; character.ShortDescription = ShortDescription;
            character.Note = Note;
            character.DefaultAddress = DefaultAddress;
            character.GroupAddresses = GroupAddresses.ToList();
            character.Color = Color; character.FallbackIcon = FallbackIcon;
            character.AvatarPath = AvatarPath; character.ImportanceLevel = ImportanceLevel;
            character.CustomImportanceLabel = CustomImportanceLabel;
            character.NarrativeStartPoint = NarrativeStartPoint;
            character.NarrativeEndPoint = NarrativeEndPoint;
            character.IsCollective = IsCollective; character.PopulationNote = PopulationNote;
            character.AvatarRing = AvatarRing; character.GroupBookmark = GroupBookmark;
            // Отображаемое имя — первая запись списка, остальные следом.
            // Aliases продолжают заполняться теми же значениями: старый код
            // и старые проекты читают их и не замечают появления списка.
            var names = new List<CharacterNameEntry>();
            if (!string.IsNullOrWhiteSpace(Name))
                names.Add(new CharacterNameEntry { Value = Name, Note = _primaryNameNote });
            names.AddRange(AlternateNames);

            character.Names = names;
            character.Aliases = AlternateNames.Select(n => n.Value).ToList();
            character.Tags = Tags.ToList();
            character.Gallery = Gallery
                .Where(g => !g.IsAddTile)
                .Select(g => g.ImageRef)
                .ToList();
            // ActiveStatuses не трогаем: вьюмодель их больше не ведёт, а поле
            // в модели остаётся как загрузилось — старые проекты ничего
            // не теряют при сохранении.
            character.Labels = Labels.ToList();
        }
    }
}