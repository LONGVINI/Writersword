using Avalonia.Input;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables.Fluent;
using System.Threading;
using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.Characters.Actions;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Modules.Characters.Services;
using Writersword.Modules.Characters.ViewModels.Inspector;
using Writersword.Modules.Characters.ViewModels.Onboarding;
using Writersword.Modules.Characters.ViewModels.Templates;
using Writersword.Modules.Common;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels
{
    public class CharactersViewModel : ReactiveObject, IUndoableModule, System.IDisposable
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersViewModel>();
        private readonly ICharacterService _characterService;
        private readonly IRelationshipService _relationshipService;
        private readonly ICharacterAnketaService _anketaService;
        private readonly UndoRedoStack _undoRedoStack = new(maxSteps: 100);
        private readonly CharactersTrashService _trash;
        private readonly ICharacterAvatarService? _avatarService;
        private CancellationTokenSource? _refreshCts;
        private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        // Режим сравнения версий (восстановление после несохранённой сессии):
        // данные модуля нельзя изменять, пока пользователь не выбрал версию.
        // Устанавливается модулем из Context.IsInCompareMode. Сейчас блокирует
        // перетаскивание карточек в списке.
        private bool _isReadOnly;
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
        }

        public CharactersTrashService Trash => _trash;
        public ICharacterAvatarService? AvatarService => _avatarService;
        public ICharacterService CharacterService => _characterService;

        // Задаётся из CharactersListView code-behind.
        // Вызывается для каждого созданного CharacterListItemViewModel.
        public Action<CharacterListItemViewModel>? BindAvatarPickerCallback { get; set; }

        // Задаётся из CharactersListView code-behind. Вызывается после создания
        // персонажа или группы (folderId, characterId), чтобы список прокрутился
        // к новой карточке: папка может находиться за пределами видимой области,
        // а её элементы — быть ещё не реализованными раскладкой.
        public Action<string?, string>? ScrollToCharacterCallback { get; set; }

        private string _undoToastMessage = string.Empty;
        public string UndoToastMessage
        {
            get => _undoToastMessage;
            private set => this.RaiseAndSetIfChanged(ref _undoToastMessage, value);
        }

        public void ShowUndoToast(string message) => UndoToastMessage = message;
        public void HideUndoToast() => UndoToastMessage = string.Empty;

        // ── IUndoableModule ────────────────────────────────────────────────
        public bool CanUndo => _undoRedoStack.CanUndo;
        public bool CanRedo => _undoRedoStack.CanRedo;
        public string? UndoDescription => _undoRedoStack.UndoDescription;
        public string? RedoDescription => _undoRedoStack.RedoDescription;
        public void Undo() { if (IsReadOnly) return; _undoRedoStack.Undo(); }
        public void Redo() { if (IsReadOnly) return; _undoRedoStack.Redo(); }
        public void PushCommand(IUndoableCommand command)
        {
            // Идёт сбор пачки — команда ждёт в ней, а не ложится в историю
            // отдельным шагом.
            if (_undoBatch is not null) { _undoBatch.Add(command); return; }
            _undoRedoStack.Push(command);
        }

        // ── Пачка правок ───────────────────────────────────────────────────
        //
        // Боковая панель правит все выбранные карточки разом, и каждая кладёт
        // свою команду. Без сбора в пачку Ctrl+Z снимал бы правку по одной
        // карточке за нажатие: выбрал десять, поменял цвет один раз — и жми
        // десять раз обратно.
        //
        // Вложенные пачки не заводятся: сбор уже идёт — значит внешняя пачка
        // всё и соберёт, а внутренняя только раздробила бы шаг обратно.

        private List<IUndoableCommand>? _undoBatch;

        /// <summary>
        /// Начать сбор пачки. Возвращённый объект закрывает её при
        /// освобождении, поэтому вызывать полагается через using.
        /// </summary>
        public IDisposable BeginUndoBatch(string description)
        {
            if (_undoBatch is not null) return new UndoBatchScope(null);

            _undoBatch = new List<IUndoableCommand>();
            return new UndoBatchScope(() => EndUndoBatch(description));
        }

        private void EndUndoBatch(string description)
        {
            var batch = _undoBatch;
            _undoBatch = null;

            if (batch is null || batch.Count == 0) return;

            // Одна команда — она и есть шаг. Заворачивать её в связку значило бы
            // подменить её собственное описание в подсказке отмены.
            if (batch.Count == 1) { _undoRedoStack.Push(batch[0]); return; }

            _undoRedoStack.Push(new Actions.BatchCommand(description, batch));
        }

        private sealed class UndoBatchScope : IDisposable
        {
            private Action? _close;

            public UndoBatchScope(Action? close) => _close = close;

            public void Dispose()
            {
                var close = _close;
                _close = null;
                close?.Invoke();
            }
        }

        private static readonly IReadOnlyList<KeyGesture> _blockedGestures = new[]
        {
            new KeyGesture(Key.Z, KeyModifiers.Control),
            new KeyGesture(Key.Y, KeyModifiers.Control)
        };
        public IReadOnlyList<KeyGesture> BlockedNativeGestures => _blockedGestures;

        private int _mainTabIndex = 0;
        public int MainTabIndex
        {
            get => _mainTabIndex;
            set
            {
                this.RaiseAndSetIfChanged(ref _mainTabIndex, value);
                this.RaisePropertyChanged(nameof(IsTab0Active));
                this.RaisePropertyChanged(nameof(IsTab1Active));
                this.RaisePropertyChanged(nameof(IsTab2Active));
                this.RaisePropertyChanged(nameof(IsTab3Active));
            }
        }
        public bool IsTab0Active => _mainTabIndex == 0;
        public bool IsTab1Active => _mainTabIndex == 1;
        public bool IsTab2Active => _mainTabIndex == 2;
        public bool IsTab3Active => _mainTabIndex == 3;

        public ReactiveCommand<string, Unit> SwitchMainTabCommand { get; }
        public ReactiveCommand<Unit, Unit> GoToCharactersCommand { get; }
        public ReactiveCommand<Unit, Unit> GoToEditCommand { get; }
        public ReactiveCommand<Unit, Unit> GoToRelationshipsCommand { get; }
        public ReactiveCommand<Unit, Unit> GoToTemplatesCommand { get; }
        public ReactiveCommand<Unit, Unit> FilterPrimaryCommand { get; }
        public ReactiveCommand<Unit, Unit> FilterSecondaryCommand { get; }
        public ReactiveCommand<Unit, Unit> FilterTertiaryCommand { get; }
        public ReactiveCommand<Unit, Unit> FilterCollectiveCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearImportanceFilterCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateFolderCommand { get; }
        public ReactiveCommand<string, Unit> DeleteFolderCommand { get; }
        public ReactiveCommand<string, Unit> ConfirmDeleteFolderCommand { get; }
        public ReactiveCommand<string, Unit> ToggleFolderCommand { get; }
        public event Action<string, string>? FolderDeleteRequested;

        public CharactersTemplatesViewModel TemplatesViewModel { get; }
        public CharactersGraphViewModel GraphViewModel { get; }

        private bool _showOnboarding;
        public bool ShowOnboarding
        {
            get => _showOnboarding;
            set => this.RaiseAndSetIfChanged(ref _showOnboarding, value);
        }
        public CharactersOnboardingViewModel OnboardingViewModel { get; }

        public ObservableCollection<string> ActiveTemplateIds { get; } = new();
        public ObservableCollection<CharacterListItemViewModel> FilteredCharacters { get; } = new();
        public ObservableCollection<CharacterFolderViewModel> Folders { get; } = new();
        // ── Выделение карточек и боковая панель оформления ─────────────────
        //
        // Выделение живёт только в представлении: в модель персонажа оно не
        // попадает и проект им не пачкается. Панель правит всё выделенное
        // разом — потому в ней и нет переключателей «применить ко всем»,
        // которые были нужны окну настроек одной карточки.

        public ObservableCollection<CharacterListItemViewModel> SelectedCards { get; } = new();

        // Панель создаётся при первом обращении, а не в конструкторе: до
        // первого выделения она не нужна, а конструктор модуля и так тяжёлый.
        private CharacterInspectorViewModel? _inspector;
        public CharacterInspectorViewModel Inspector =>
            _inspector ??= new CharacterInspectorViewModel(this);

        public bool HasSelection => SelectedCards.Count > 0;

        private bool _isInspectorOpen;
        public bool IsInspectorOpen
        {
            get => _isInspectorOpen;
            set
            {
                this.RaiseAndSetIfChanged(ref _isInspectorOpen, value);
                this.RaisePropertyChanged(nameof(EffectiveInspectorWidth));
            }
        }

        // Ширина панели, которую человек тянет полоской на краю. Живёт в
        // модуле, а не в самой панели: панель прячется вместе со своей шириной,
        // а вернуться она должна той же, какой её оставили.
        //
        // Стартовое значение поднято с прежних 268 — на нём поля панели
        // (особенно превью карточки в цветопикере и лента быстрых аватарок)
        // ощутимо теснее, чем нужно по умолчанию. Подгонка под фактическую
        // ширину модуля (в нём может стоять не один документ рядом) — отдельная
        // задача поверх этой правки: она нужна не всегда, а тянуть панель
        // по-прежнему можно вручную полоской.
        private double _inspectorWidth = 300.0;
        public double InspectorWidth
        {
            get => _inspectorWidth;
            set
            {
                var clamped = Math.Max(InspectorMinWidth, Math.Min(InspectorMaxWidth, value));
                this.RaiseAndSetIfChanged(ref _inspectorWidth, clamped);
                this.RaisePropertyChanged(nameof(EffectiveInspectorWidth));
            }
        }

        public const double InspectorMinWidth = 210.0;
        public const double InspectorMaxWidth = 520.0;

        /// <summary>
        /// Ширина, которую фактически получает колонка панели: сама
        /// InspectorWidth, пока панель открыта, и ноль, когда закрыта.
        /// Через это свойство (а не через IsVisible) панель выезжает и
        /// уезжает плавно — колонка остаётся в раскладке всё время, а
        /// анимируется только её ширина. См. Transitions на панели в
        /// CharactersListView.axaml.
        /// </summary>
        public double EffectiveInspectorWidth => IsInspectorOpen ? InspectorWidth : 0.0;

        /// <summary>
        /// Выбрать карточку. additive — добавить к уже выбранным (Ctrl или
        /// Shift), иначе выбор заменяется целиком. Повторный additive-клик по
        /// уже выбранной снимает с неё выделение.
        /// </summary>
        public void SelectCard(CharacterListItemViewModel item, bool additive)
        {
            if (item is null) return;

            // Повторный щелчок по единственной выбранной карточке закрывает
            // панель и снимает выбор. Выключателя у панели нет: открыть её
            // можно только щелчком по карточке, закрыть — крестиком в её углу
            // или вот этим повторным щелчком, и оба пути обязаны оставлять
            // список в одном и том же состоянии.
            if (!additive && SelectedCards.Count == 1 && ReferenceEquals(SelectedCards[0], item))
            {
                ClearSelection();
                return;
            }

            if (additive)
            {
                if (SelectedCards.Contains(item))
                {
                    item.IsCardSelected = false;
                    SelectedCards.Remove(item);
                }
                else
                {
                    item.IsCardSelected = true;
                    SelectedCards.Add(item);
                }
            }
            else
            {
                foreach (var previous in SelectedCards) previous.IsCardSelected = false;
                SelectedCards.Clear();
                item.IsCardSelected = true;
                SelectedCards.Add(item);
            }

            if (SelectedCards.Count > 0) IsInspectorOpen = true;
            RaiseSelectionChanged();
        }

        /// <summary>
        /// Папка, в которой сейчас лежит персонаж. Панель показывает её в
        /// списке групп; сама принадлежность хранится не у персонажа, а
        /// списком идентификаторов у папки, поэтому искать приходится обходом.
        /// </summary>
        public string? FindFolderIdOf(string characterId)
        {
            foreach (var folder in Folders)
                foreach (var card in folder.Characters)
                    if (card.Id == characterId) return folder.FolderId;
            return null;
        }

        /// <summary>Снять выделение со всех карточек и закрыть панель.</summary>
        public void ClearSelection()
        {
            if (SelectedCards.Count == 0)
            {
                IsInspectorOpen = false;
                return;
            }

            foreach (var previous in SelectedCards) previous.IsCardSelected = false;
            SelectedCards.Clear();
            IsInspectorOpen = false;
            RaiseSelectionChanged();
        }

        /// <summary>
        /// Пересобрать выделение после пересборки списка. Фильтры создают
        /// вью-модели карточек заново, и выделение осталось бы держать
        /// объекты, которых на экране больше нет.
        ///
        /// Карточки ищутся по идентификатору персонажа, а не по ссылке:
        /// переименование само зовёт ApplyFilters, и без этого поиска панель
        /// закрывалась бы от каждой правки имени в ней же.
        /// </summary>
        public void PruneSelection()
        {
            if (SelectedCards.Count == 0) return;

            var alive = new Dictionary<string, CharacterListItemViewModel>();
            foreach (var folder in Folders)
                foreach (var card in folder.Characters)
                    alive[card.Id] = card;

            var changed = false;
            for (var i = SelectedCards.Count - 1; i >= 0; i--)
            {
                var card = SelectedCards[i];
                if (alive.TryGetValue(card.Id, out var fresh))
                {
                    if (ReferenceEquals(fresh, card)) continue;
                    card.IsCardSelected = false;
                    fresh.IsCardSelected = true;
                    SelectedCards[i] = fresh;
                    changed = true;
                }
                else
                {
                    card.IsCardSelected = false;
                    SelectedCards.RemoveAt(i);
                    changed = true;
                }
            }

            if (!changed) return;
            if (SelectedCards.Count == 0) IsInspectorOpen = false;
            RaiseSelectionChanged();
        }

        private void RaiseSelectionChanged()
        {
            this.RaisePropertyChanged(nameof(HasSelection));
            _inspector?.OnSelectionChanged();
        }

        public ObservableCollection<string> AvailableTags { get; } = new();
        public ObservableCollection<string> ActiveTagFilters { get; } = new();

        // ── Плоский список для вкладки «Редактор» ──────────────────────────
        // Заголовки папок и персонажи лежат в одной коллекции вперемешку, чтобы
        // один виртуализированный ItemsRepeater реализовывал только видимые строки.
        // Вложенные списки (папка → персонажи) виртуализировать нельзя: внутренний
        // список получает бесконечную высоту и реализует все строки сразу — вкладка
        // фризила при каждом открытии, пока не построит весь список. Здесь строки
        // добавляются инкрементально, ровно как идёт прогрессивная загрузка
        // персонажей, поэтому список наполняется плавно и открытие мгновенное.
        public ObservableCollection<object> EditorRows { get; } = new();

        private readonly Dictionary<CharacterFolderViewModel, IDisposable> _editorFolderSubs = new();
        private bool _editorRowsHooked;

        private void HookEditorRows()
        {
            if (_editorRowsHooked) return;
            _editorRowsHooked = true;

            Folders.CollectionChanged += OnFoldersChangedForEditor;
            RebuildEditorRows();
        }

        private void OnFoldersChangedForEditor(object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Штатный сценарий загрузки — добавление папки в конец списка —
            // обрабатываем точечно. Любые структурные изменения (Clear при
            // пересборке, удаление, переупорядочивание) редки → полный пересбор.
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && e.NewItems != null)
            {
                foreach (CharacterFolderViewModel folder in e.NewItems)
                {
                    EditorRows.Add(folder);
                    SubscribeFolderForEditor(folder);
                    if (folder.IsExpanded)
                        foreach (var ch in folder.Characters)
                            EditorRows.Add(ch);
                }
                return;
            }

            RebuildEditorRows();
        }

        private void SubscribeFolderForEditor(CharacterFolderViewModel folder)
        {
            if (_editorFolderSubs.ContainsKey(folder)) return;

            void OnFolderProp(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(CharacterFolderViewModel.IsExpanded))
                    RebuildFolderBlock(folder);
            }
            void OnChars(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
                => OnFolderCharactersChanged(folder, e);

            folder.PropertyChanged += OnFolderProp;
            folder.Characters.CollectionChanged += OnChars;

            _editorFolderSubs[folder] = System.Reactive.Disposables.Disposable.Create(() =>
            {
                folder.PropertyChanged -= OnFolderProp;
                folder.Characters.CollectionChanged -= OnChars;
            });
        }

        private void OnFolderCharactersChanged(CharacterFolderViewModel folder,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (!folder.IsExpanded) return;

            // Прогрессивная загрузка добавляет персонажей в конец папки по одному —
            // вставляем ровно на своё место, без пересбора всего списка.
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && e.NewItems != null && e.NewStartingIndex >= 0)
            {
                int header = EditorRows.IndexOf(folder);
                if (header < 0) { RebuildEditorRows(); return; }

                int insertAt = header + 1 + e.NewStartingIndex;
                foreach (var item in e.NewItems)
                {
                    if (insertAt > EditorRows.Count) insertAt = EditorRows.Count;
                    EditorRows.Insert(insertAt, item!);
                    insertAt++;
                }
                return;
            }

            // Reset/Remove/Replace/Move — точечно пересобираем блок этой папки.
            RebuildFolderBlock(folder);
        }

        // Убирает строки-персонажи данной папки из EditorRows и (если раскрыта)
        // вставляет актуальные заново. Заголовок папки остаётся на месте.
        private void RebuildFolderBlock(CharacterFolderViewModel folder)
        {
            int header = EditorRows.IndexOf(folder);
            if (header < 0) { RebuildEditorRows(); return; }

            int i = header + 1;
            while (i < EditorRows.Count && EditorRows[i] is not CharacterFolderViewModel)
                EditorRows.RemoveAt(i);

            if (folder.IsExpanded)
            {
                int insertAt = header + 1;
                foreach (var ch in folder.Characters)
                {
                    EditorRows.Insert(insertAt, ch);
                    insertAt++;
                }
            }
        }

        private void RebuildEditorRows()
        {
            foreach (var sub in _editorFolderSubs.Values) sub.Dispose();
            _editorFolderSubs.Clear();

            EditorRows.Clear();
            foreach (var folder in Folders)
            {
                SubscribeFolderForEditor(folder);
                EditorRows.Add(folder);
                if (folder.IsExpanded)
                    foreach (var ch in folder.Characters)
                        EditorRows.Add(ch);
            }
        }

        private string _searchQuery = string.Empty;
        public string SearchQuery
        {
            get => _searchQuery;
            set { this.RaiseAndSetIfChanged(ref _searchQuery, value); ApplyFilters(); }
        }

        private CharacterImportanceLevel? _filterImportance;
        public CharacterImportanceLevel? FilterImportance
        {
            get => _filterImportance;
            set { this.RaiseAndSetIfChanged(ref _filterImportance, value); ApplyFilters(); }
        }

        private bool _filterCollectiveOnly;
        public bool FilterCollectiveOnly
        {
            get => _filterCollectiveOnly;
            set { this.RaiseAndSetIfChanged(ref _filterCollectiveOnly, value); ApplyFilters(); }
        }

        // ── режим отображения и размер карточек ───────────────────────────

        private double _containerWidth = 600.0;
        private double _cardWidth = 148.0;
        private double _cardTopHeight = 60.0;
        private double _cardNameHeight = 40.0;
        private double _cardIconSize = 30.0;
        private int _cardsPerRow = 4;

        private CharactersViewMode _viewMode = CharactersViewMode.GridMedium;
        public CharactersViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                var changed = _viewMode != value;

                this.RaiseAndSetIfChanged(ref _viewMode, value);
                this.RaisePropertyChanged(nameof(IsListMode));
                this.RaisePropertyChanged(nameof(IsGridMode));
                this.RaisePropertyChanged(nameof(ViewModeIndex));
                RecalculateCardDimensions();

                // Смена размера пересобирает карточки заново, а не растягивает
                // существующие. От размера зависит не только ширина: своя
                // высота, свой размер аватарки, кнопок и значков меток, а в
                // режиме списка ещё и вся раскладка карточки. Пересчёт этих
                // величин доезжает до уже построенных карточек не полностью —
                // часть привязок читает их один раз при создании.
                //
                // Пересборка идёт тем же прогрессивным путём, что и при входе
                // в модуль, поэтому список не подвисает даже на сотнях
                // карточек.
                if (changed) RefreshAll();
            }
        }
        public bool IsListMode => _viewMode == CharactersViewMode.List;
        public bool IsGridMode => _viewMode != CharactersViewMode.List;

        // Индекс для ComboBox: 0=List 1=Small 2=Medium 3=Large 4=Huge
        public int ViewModeIndex
        {
            get => _viewMode switch
            {
                CharactersViewMode.List => 0,
                CharactersViewMode.GridSmall => 1,
                CharactersViewMode.Grid or
                CharactersViewMode.GridMedium => 2,
                CharactersViewMode.GridLarge => 3,
                CharactersViewMode.GridHuge => 4,
                _ => 2
            };
            set => ViewMode = value switch
            {
                0 => CharactersViewMode.List,
                1 => CharactersViewMode.GridSmall,
                2 => CharactersViewMode.GridMedium,
                3 => CharactersViewMode.GridLarge,
                4 => CharactersViewMode.GridHuge,
                _ => CharactersViewMode.GridMedium
            };
        }

        // CardWidth используется только для ghost и для расчётов drag.
        // Сами карточки в сетке ширину не биндят — UniformGrid растягивает их сам.
        public double CardWidth => _cardWidth;
        public double CardTopHeight => _cardTopHeight;
        // Круг чуть меньше панели — остаётся отступ сверху и снизу.
        // Масштабируется вместе с высотой верхней панели, без верхнего потолка.
        public double CardAvatarSize => Math.Max(40, _cardTopHeight - 12);
        // Кольцо чуть больше аватара — рисуется снаружи картинки.
        public double CardRingSize => CardAvatarSize + 8;
        // Значки меток идут по дуге вокруг аватарки и растут вместе с ней:
        // при мелких карточках значок в шестнадцать точек закрывал бы пол-лица,
        // при крупных — терялся бы. Нижний предел оставлен, чтобы фигура
        // внутри значка не выродилась в пятно.
        public double CardLabelIconSize => Math.Max(12, CardAvatarSize * 0.3);
        // Дуга проходит по краю аватарки: значок садится на неё серединой и
        // краем заходит на картинку — так же, как бейдж состояния.
        public double CardLabelArcRadius => CardAvatarSize / 2 + CardLabelIconSize * 0.15;
        public double CardNameHeight => _cardNameHeight;
        public double CardTotalHeight => _cardTopHeight + _cardNameHeight;
        public double CardIconFontSize => _cardIconSize;

        // Размеры кнопок взаимодействия, пропорциональные ширине карточки (baseline 148).
        public double CardActionIconSize => Math.Max(11, Math.Round(_cardWidth * 11.0 / 148.0));

        // Кнопки левого угла (цвет, настройки) выводятся из размера правых, а
        // не задаются своей пропорцией. Правая кнопка — это значок плюс поля
        // по четыре точки с каждой стороны; раньше левая считалась отдельно и
        // выходила на пару точек шире, отчего левый угол занимал заметно
        // больше места, чем правый.
        public double CardColorButtonSize => CardActionIconSize + 8;

        // Отступ значка «Мёртв» от правого края: он встаёт слева от кнопок
        // правки, а не под ними.
        //
        // Занятое кнопками место зависит от раскладки. В строке списка
        // CardControlsOrientation горизонтальная: кнопки стоят парой — две
        // ширины плюс просвет между ними, а поле панели равно правой части
        // CardEditBtnsMargin. В плитке раскладка вертикальная: кнопки сложены
        // столбиком и занимают ширину одной, поле панели — четыре точки со
        // всех сторон. Раньше обе раскладки считались по паре, и в плитке
        // значок отходил от кнопок к середине карточки на лишнюю кнопку.
        //
        // Ширину кнопки даёт CardColorButtonSize — она выведена из размера
        // кнопок правки и растёт вместе с шириной карточки, поэтому отступ сам
        // подстраивается под размер плитки. Зазор до значка масштабируется тем
        // же порядком, с нижним пределом: на мелких карточках он не должен
        // схлопываться в ноль и слипать значок с кнопкой.
        public Avalonia.Thickness CardDeadBadgeMargin
        {
            get
            {
                var gap = Math.Max(3.0, Math.Round(CardActionIconSize * 0.3));
                var buttons = UseListRowLayout
                    ? CardColorButtonSize * 2 + 2 + 10
                    : CardColorButtonSize + 4;
                return new Avalonia.Thickness(0, 4, buttons + gap, 0);
            }
        }

        // Количество колонок — используется для расчётов drag.
        public int CardsPerRow => _cardsPerRow;

        // ── Горизонтальная строка списка ──────────────────────────────────
        // В режиме List карточка раскладывается строкой: кружок слева, имя
        // справа (для сплита с другими модулями). Если контейнер слишком узкий
        // и строка сжимается — возвращаемся к обычной вертикальной карточке.
        private const double ListRowThreshold = 340.0;
        public bool UseListRowLayout => IsListMode && _containerWidth >= ListRowThreshold;

        // Параметры раскладки карточки, зависящие от строки/плитки: докинг
        // цветной зоны, её ширина, выравнивание и ограничение блока имени.
        public Avalonia.Controls.Dock CardZoneDock =>
            UseListRowLayout ? Avalonia.Controls.Dock.Left : Avalonia.Controls.Dock.Top;
        public double CardZoneWidth => UseListRowLayout ? 64.0 : double.NaN;
        public Avalonia.Media.TextAlignment CardNameAlignment =>
            UseListRowLayout ? Avalonia.Media.TextAlignment.Left : Avalonia.Media.TextAlignment.Center;
        public Avalonia.Layout.HorizontalAlignment CardNamePanelAlignment =>
            UseListRowLayout ? Avalonia.Layout.HorizontalAlignment.Left
                             : Avalonia.Layout.HorizontalAlignment.Stretch;
        public double CardNameMaxWidth => UseListRowLayout ? 320.0 : double.PositiveInfinity;
        // Минимум блока имени нужен только строке (поля ввода имени не должны
        // схлопываться); в плитке минимум задаёт сама сетка.
        public double CardNameMinWidth => UseListRowLayout ? 160.0 : 0.0;

        // Кнопки карточки (пикер, шестерёнка, правка/удаление): в плитке — углы
        // цветной зоны, вертикально; в строке — правый край строки, горизонтально
        // и по центру высоты, чтобы не наезжать на аватар слева.
        public Avalonia.Layout.Orientation CardControlsOrientation =>
            UseListRowLayout ? Avalonia.Layout.Orientation.Horizontal
                             : Avalonia.Layout.Orientation.Vertical;
        public Avalonia.Layout.VerticalAlignment CardControlsVAlign =>
            UseListRowLayout ? Avalonia.Layout.VerticalAlignment.Center
                             : Avalonia.Layout.VerticalAlignment.Top;
        public Avalonia.Layout.HorizontalAlignment CardPickerHAlign =>
            UseListRowLayout ? Avalonia.Layout.HorizontalAlignment.Right
                             : Avalonia.Layout.HorizontalAlignment.Left;
        // Поле левого угла равно полю правого: иначе углы карточки визуально
        // не совпадают, даже когда сами кнопки одного размера.
        public Avalonia.Thickness CardPickerMargin =>
            UseListRowLayout ? new Avalonia.Thickness(0, 0, 76, 0) : new Avalonia.Thickness(4);
        public Avalonia.Thickness CardEditBtnsMargin =>
            UseListRowLayout ? new Avalonia.Thickness(0, 0, 10, 0) : new Avalonia.Thickness(4);

        // Скругление «полоски»-аватара повторяет углы карточки: в плитке зона
        // сверху, в строке списка — слева.
        public Avalonia.CornerRadius CardZoneCornerRadius =>
            UseListRowLayout ? new Avalonia.CornerRadius(8, 0, 0, 8)
                             : new Avalonia.CornerRadius(8, 8, 0, 0);

        // Скругление подложки под именем — дополнение к скруглению цветной
        // зоны: вместе они дают углы карточки. Подложка отсекает ту часть
        // фона карточки, которая не должна быть цветной, и обязана повторять
        // её форму — иначе прямой угол вылезет за скруглённый край.
        // Минимальная ширина слота (карточка + margin 6px с каждой стороны).
        // Передаётся в UniformGridLayout.MinItemWidth из code-behind.
        // UniformGridLayout сам вычислит число колонок и растянет карточки через ItemsStretch.Fill.
        public double CardMinWidth => CardWidthRange(_viewMode).min + 12.0;

        public void UpdateContainerWidth(double width)
        {
            if (width < 1.0) return;
            if (Math.Abs(_containerWidth - width) < 10.0) return;
            _containerWidth = width;
            RecalculateCardDimensions();
        }

        private static (double min, double max) CardWidthRange(CharactersViewMode mode) => mode switch
        {
            CharactersViewMode.GridSmall => (100.0, 150.0),
            CharactersViewMode.Grid or
            CharactersViewMode.GridMedium => (130.0, 180.0),
            CharactersViewMode.GridLarge => (180.0, 250.0),
            CharactersViewMode.GridHuge => (250.0, 380.0),
            _ => (130.0, 180.0)
        };

        private void RecalculateCardDimensions()
        {
            if (!IsGridMode)
            {
                _cardWidth = 148.0;
                _cardTopHeight = 60.0;
                _cardNameHeight = 40.0;
                _cardIconSize = 30.0;
                _cardsPerRow = 1;
                RaiseCardDimensionPropertiesIfChanged();
                return;
            }

            const double cardMargin = 6.0;
            const double slotMargin = cardMargin * 2; // 12px на карточку

            var (minW, maxW) = CardWidthRange(_viewMode);

            // максимальное число карточек в строке при котором каждая >= minW
            int n = Math.Max(1, (int)(_containerWidth / (minW + slotMargin)));

            // фактическая ширина при n карточках
            double cardW = _containerWidth / n - slotMargin;

            // если карточки шире maxW — добавляем ещё колонку
            if (cardW > maxW)
            {
                int nMore = (int)(_containerWidth / (maxW + slotMargin));
                if (nMore > n)
                {
                    n = nMore;
                    cardW = _containerWidth / n - slotMargin;
                }
            }

            cardW = Math.Max(minW, Math.Min(maxW, cardW));

            // высоты пропорциональны ширине от baseline 148×108
            double totalH = 108.0 * (cardW / 148.0);

            // CardWidth не округляем — используется только для ghost и drag-расчётов
            _cardWidth = cardW;
            _cardTopHeight = Math.Round(totalH * 0.64);
            _cardNameHeight = Math.Round(totalH * 0.36);
            _cardIconSize = Math.Round(cardW * (30.0 / 148.0));
            _cardsPerRow = n;

            RaiseCardDimensionPropertiesIfChanged();
        }

        // Снимок значений, при которых карточкам в последний раз рассылались
        // уведомления о размерах. Пересчёт приходит с каждым изменением ширины
        // контейнера (перетаскивание сплиттера, ресайз окна, появление панелей):
        // если наблюдаемые карточками значения не изменились, повторная рассылка
        // только заставляла каждую реализованную карточку заново прогонять два
        // десятка рефлексивных привязок и перемерять раскладку всего списка.
        // _cardWidth в снимок не входит: в шаблоне карточки на него никто не
        // привязан (он нужен призраку и расчётам drag, которые читают свойство
        // напрямую), а его дробные изменения при плавном ресайзе открывали бы
        // рассылку на каждый пиксель. Все зависящие от него привязываемые
        // значения (CardActionIconSize, CardColorButtonSize) округляются и
        // проверяются в снимке отдельно.
        private CharactersViewMode _raisedViewMode = (CharactersViewMode)(-1);
        private bool _raisedUseListRow;
        private double _raisedTopHeight = -1.0;
        private double _raisedNameHeight = -1.0;
        private double _raisedIconSize = -1.0;
        private double _raisedActionIconSize = -1.0;
        private double _raisedColorButtonSize = -1.0;
        private int _raisedCardsPerRow = -1;

        private void RaiseCardDimensionPropertiesIfChanged()
        {
            bool changed =
                _raisedViewMode != _viewMode
                || _raisedUseListRow != UseListRowLayout
                || _raisedCardsPerRow != _cardsPerRow
                || Math.Abs(_raisedTopHeight - _cardTopHeight) > 0.01
                || Math.Abs(_raisedNameHeight - _cardNameHeight) > 0.01
                || Math.Abs(_raisedIconSize - _cardIconSize) > 0.01
                || Math.Abs(_raisedActionIconSize - CardActionIconSize) > 0.01
                || Math.Abs(_raisedColorButtonSize - CardColorButtonSize) > 0.01;
            if (!changed) return;

            _raisedViewMode = _viewMode;
            _raisedUseListRow = UseListRowLayout;
            _raisedCardsPerRow = _cardsPerRow;
            _raisedTopHeight = _cardTopHeight;
            _raisedNameHeight = _cardNameHeight;
            _raisedIconSize = _cardIconSize;
            _raisedActionIconSize = CardActionIconSize;
            _raisedColorButtonSize = CardColorButtonSize;

            RaiseCardDimensionProperties();
        }

        private void RaiseCardDimensionProperties()
        {
            this.RaisePropertyChanged(nameof(CardWidth));
            this.RaisePropertyChanged(nameof(CardTopHeight));
            this.RaisePropertyChanged(nameof(CardAvatarSize));
            this.RaisePropertyChanged(nameof(CardRingSize));
            this.RaisePropertyChanged(nameof(CardLabelIconSize));
            this.RaisePropertyChanged(nameof(CardLabelArcRadius));
            this.RaisePropertyChanged(nameof(CardNameHeight));
            this.RaisePropertyChanged(nameof(CardTotalHeight));
            this.RaisePropertyChanged(nameof(CardIconFontSize));
            this.RaisePropertyChanged(nameof(CardActionIconSize));
            this.RaisePropertyChanged(nameof(CardColorButtonSize));
            this.RaisePropertyChanged(nameof(CardsPerRow));
            this.RaisePropertyChanged(nameof(CardMinWidth));
            this.RaisePropertyChanged(nameof(UseListRowLayout));
            this.RaisePropertyChanged(nameof(CardZoneDock));
            this.RaisePropertyChanged(nameof(CardZoneWidth));
            this.RaisePropertyChanged(nameof(CardNameAlignment));
            this.RaisePropertyChanged(nameof(CardNamePanelAlignment));
            this.RaisePropertyChanged(nameof(CardNameMaxWidth));
            this.RaisePropertyChanged(nameof(CardNameMinWidth));
            this.RaisePropertyChanged(nameof(CardControlsOrientation));
            this.RaisePropertyChanged(nameof(CardControlsVAlign));
            this.RaisePropertyChanged(nameof(CardPickerHAlign));
            this.RaisePropertyChanged(nameof(CardPickerMargin));
            this.RaisePropertyChanged(nameof(CardEditBtnsMargin));
            this.RaisePropertyChanged(nameof(CardDeadBadgeMargin));
            this.RaisePropertyChanged(nameof(CardZoneCornerRadius));
        }

        // ── карточка персонажа ─────────────────────────────────────────────

        private CharacterCardViewModel? _selectedCharacterCard;
        public CharacterCardViewModel? SelectedCharacterCard
        {
            get => _selectedCharacterCard;
            private set => this.RaiseAndSetIfChanged(ref _selectedCharacterCard, value);
        }

        private bool _isCardOpen;
        public bool IsCardOpen
        {
            get => _isCardOpen;
            set => this.RaiseAndSetIfChanged(ref _isCardOpen, value);
        }

        public ReactiveCommand<Unit, Unit> CreateCharacterCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateCharacterRandomizedCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateCollectiveCharacterCommand { get; }
        public ReactiveCommand<string, Unit> OpenCharacterCommand { get; }
        public ReactiveCommand<string, Unit> EditCharacterCommand { get; }
        public ReactiveCommand<string, Unit> DeleteCharacterCommand { get; }
        public ReactiveCommand<string, Unit> DuplicateCharacterCommand { get; }
        public ReactiveCommand<string, Unit> ConfirmInlineNameCommand { get; }
        public ReactiveCommand<string, Unit> CancelInlineNameCommand { get; }
        public ReactiveCommand<string, Unit> SelectFolderCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseCardCommand { get; }
        public ReactiveCommand<Unit, Unit> FocusSearchCommand { get; }
        public ReactiveCommand<Unit, Unit> UnfocusSearchCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }
        public ReactiveCommand<string, Unit> ToggleTagFilterCommand { get; }
        public ReactiveCommand<CharactersViewMode, Unit> SwitchViewModeCommand { get; }
        public event Action? SearchFocusRequested;

        private string? _activeFolderId;
        public string? ActiveFolderId
        {
            get => _activeFolderId;
            set
            {
                this.RaiseAndSetIfChanged(ref _activeFolderId, value);
                foreach (var folder in Folders)
                    folder.IsSelected = folder.FolderId == value;
            }
        }

        // ── состояние preview-drag ─────────────────────────────────────────
        private string? _previewDragCharId;
        private string? _previewDragOriginalFolderId;
        private int _previewDragOriginalIndex;

        public CharactersViewModel(
            ICharacterService characterService,
            IRelationshipService relationshipService,
            ICharacterAnketaService anketaService,
            CharactersTrashService trash,
            ICharacterAvatarService? avatarService = null)
        {
            _characterService = characterService;
            _relationshipService = relationshipService;
            _anketaService = anketaService;
            _trash = trash;
            _avatarService = avatarService;

            // Сервис персонажей нужен вкладке шаблонов, чтобы правка набора
            // разъезжалась по карточкам, к которым он подключён.
            TemplatesViewModel = new CharactersTemplatesViewModel(anketaService, ActiveTemplateIds, characterService);
            TemplatesViewModel.OnboardingRestartRequested += () => ShowOnboarding = true;

            GraphViewModel = new CharactersGraphViewModel(characterService, relationshipService,
                id => { MainTabIndex = 0; OpenCharacter(id); });

            OnboardingViewModel = new CharactersOnboardingViewModel();
            OnboardingViewModel.Completed += OnOnboardingCompleted;

            SwitchMainTabCommand = ReactiveCommand.Create<string>(s =>
            {
                if (int.TryParse(s, out var idx)) MainTabIndex = idx;
            });

            GoToCharactersCommand = ReactiveCommand.Create(() => { MainTabIndex = 0; });
            GoToEditCommand = ReactiveCommand.Create(() => { MainTabIndex = 1; });
            GoToRelationshipsCommand = ReactiveCommand.Create(() => { MainTabIndex = 2; });
            GoToTemplatesCommand = ReactiveCommand.Create(() => { MainTabIndex = 3; });

            GoToCharactersCommand.ThrownExceptions
                .Subscribe(ex => _logger.Error(ex, "GoToCharacters failed")).DisposeWith(_disposables);
            GoToEditCommand.ThrownExceptions
                .Subscribe(ex => _logger.Error(ex, "GoToEdit failed")).DisposeWith(_disposables);
            GoToRelationshipsCommand.ThrownExceptions
                .Subscribe(ex => _logger.Error(ex, "GoToRelationships failed")).DisposeWith(_disposables);
            GoToTemplatesCommand.ThrownExceptions
                .Subscribe(ex => _logger.Error(ex, "GoToTemplates failed")).DisposeWith(_disposables);

            // Плоский список для вкладки «Редактор» строится и поддерживается
            // инкрементально по мере наполнения Folders/персонажей.
            HookEditorRows();

            FilterPrimaryCommand = ReactiveCommand.Create(() =>
            {
                FilterImportance = FilterImportance == CharacterImportanceLevel.Primary
                    ? (CharacterImportanceLevel?)null
                    : CharacterImportanceLevel.Primary;
            });
            FilterSecondaryCommand = ReactiveCommand.Create(() =>
            {
                FilterImportance = FilterImportance == CharacterImportanceLevel.Secondary
                    ? (CharacterImportanceLevel?)null
                    : CharacterImportanceLevel.Secondary;
            });
            FilterTertiaryCommand = ReactiveCommand.Create(() =>
            {
                FilterImportance = FilterImportance == CharacterImportanceLevel.Tertiary
                    ? (CharacterImportanceLevel?)null
                    : CharacterImportanceLevel.Tertiary;
            });
            FilterCollectiveCommand = ReactiveCommand.Create(() =>
            {
                FilterCollectiveOnly = !FilterCollectiveOnly;
            });
            ClearImportanceFilterCommand = ReactiveCommand.Create(() =>
            {
                FilterImportance = null;
                FilterCollectiveOnly = false;
                ClearFilters();
            });

            CreateCharacterCommand = ReactiveCommand.Create(CreateCharacter);
            CreateCharacterRandomizedCommand = ReactiveCommand.Create(CreateCharacterRandomized);
            CreateCollectiveCharacterCommand = ReactiveCommand.Create(CreateCollectiveCharacter);
            OpenCharacterCommand = ReactiveCommand.Create<string>(SelectCharacter);
            EditCharacterCommand = ReactiveCommand.Create<string>(EditCharacter);
            DeleteCharacterCommand = ReactiveCommand.Create<string>(DeleteCharacter);
            DuplicateCharacterCommand = ReactiveCommand.Create<string>(DuplicateCharacter);
            ConfirmInlineNameCommand = ReactiveCommand.Create<string>(ConfirmInlineName);
            CancelInlineNameCommand = ReactiveCommand.Create<string>(CancelInlineName);
            SelectFolderCommand = ReactiveCommand.Create<string>(id => ActiveFolderId = id);
            CloseCardCommand = ReactiveCommand.Create(() => { IsCardOpen = false; SelectedCharacterCard = null; });
            FocusSearchCommand = ReactiveCommand.Create(() => SearchFocusRequested?.Invoke());
            UnfocusSearchCommand = ReactiveCommand.Create(() => { });
            ClearFiltersCommand = ReactiveCommand.Create(ClearFilters);
            ToggleTagFilterCommand = ReactiveCommand.Create<string>(ToggleTagFilter);
            SwitchViewModeCommand = ReactiveCommand.Create<CharactersViewMode>(m => ViewMode = m);

            CreateFolderCommand = ReactiveCommand.Create(CreateFolder);
            DeleteFolderCommand = ReactiveCommand.Create<string>(id =>
            {
                var folder = _folders.FirstOrDefault(f => f.Id == id);
                if (folder is not null)
                    FolderDeleteRequested?.Invoke(id, folder.Name);
            });
            ConfirmDeleteFolderCommand = ReactiveCommand.Create<string>(ConfirmDeleteFolder);
            ToggleFolderCommand = ReactiveCommand.Create<string>(id =>
            {
                var folder = Folders.FirstOrDefault(f => f.FolderId == id);
                if (folder is not null) folder.IsExpanded = !folder.IsExpanded;
            });

            RefreshAll();
            EnsureDefaultFolders();
        }

        /// <summary>
        /// Персонаж по идентификатору — для окон, которым нужны сами данные,
        /// а не строки списка (сравнение карточек).
        /// </summary>
        public Character? GetCharacter(string id) => _characterService.GetById(id);

        public void InitializeFirstLaunch()
        {
            ShowOnboarding = true;
        }

        private void OnOnboardingCompleted(bool completed)
        {
            ShowOnboarding = false;
            if (completed)
            {
                var tags = OnboardingViewModel.GetSelectedTags().ToList();
                var recommended = _anketaService.GetRecommended(tags);
                foreach (var anketa in recommended.Take(1))
                {
                    if (!ActiveTemplateIds.Contains(anketa.Id))
                        ActiveTemplateIds.Add(anketa.Id);
                }
                TemplatesViewModel.Refresh();
            }
            _logger.Information("Onboarding dismissed — can restart via Templates tab");
        }

        // Проектная настройка «кольцо у всех аватаров» (переключается в редакторе
        // цвета кнопкой «включить/убрать у всех»). Пока она включена, новые
        // персонажи и группы создаются сразу с кольцом.
        private static bool ProjectRingsAll =>
            Writersword.Core.Services.CoreServices
                .GetService<Writersword.Core.Interfaces.WorkFlows.ITabCollection>()
                ?.ActiveTab?.Context?.Project?.AvatarRingsAll ?? false;

        private void CreateCharacter()
        {
            if (IsReadOnly) return;
            var anketas = GetActiveAnketas();
            var character = anketas.Count > 0
                ? _characterService.CreateFromAnketas(CharactersStrings.Character_DefaultName, anketas, randomize: false)
                : _characterService.Create(CharactersStrings.Character_DefaultName);
            character.AvatarRing = ProjectRingsAll;
            AddCharacterToActiveFolderVm(character, isNaming: true);
        }

        private void CreateCharacterRandomized()
        {
            if (IsReadOnly) return;
            var anketas = GetActiveAnketas();
            var character = anketas.Count > 0
                ? _characterService.CreateFromAnketas(CharactersStrings.Character_DefaultName, anketas, randomize: true)
                : _characterService.Create(CharactersStrings.Character_DefaultName);
            character.AvatarRing = ProjectRingsAll;
            AddCharacterToActiveFolderVm(character, isNaming: true);
        }

        private void CreateCollectiveCharacter()
        {
            if (IsReadOnly) return;
            var collective = _anketaService.GetById(CharacterAnketa.CollectiveId);
            var anketas = collective is not null
                ? new[] { collective }
                : System.Array.Empty<CharacterAnketa>();
            var character = _characterService.CreateCollective(CharactersStrings.Character_DefaultName, anketas);
            character.AvatarRing = ProjectRingsAll;
            AddCharacterToActiveFolderVm(character, isNaming: true);
        }

        private void AddCharacterToActiveFolderVm(Character character, bool isNaming = false)
        {
            var folderId = ActiveFolderId ?? _folders.FirstOrDefault()?.Id;

            var modelFolder = _folders.FirstOrDefault(f => f.Id == folderId);
            if (modelFolder is not null && !modelFolder.CharacterIds.Contains(character.Id))
                modelFolder.CharacterIds.Add(character.Id);

            ApplyFolderImportance(character, modelFolder);

            var folderVm = Folders.FirstOrDefault(f => f.FolderId == folderId);
            if (folderVm is not null)
            {
                folderVm.IsExpanded = true;
                var relCount = _relationshipService.GetAllForCharacter(character.Id).Count;
                var item = new CharacterListItemViewModel(character, relCount, isNaming, _avatarService);
                BindCharacterItemCallbacks(item);
                folderVm.Characters.Add(item);
            }
            else
            {
                RefreshFolderViewModels(inlineBeingNamedId: character.Id);
            }

            // Подводим список к новой карточке — иначе создание из прокрученного
            // вверх положения происходит «за кадром» и выглядит как ничего.
            ScrollToCharacterCallback?.Invoke(folderId, character.Id);
        }

        /// <summary>
        /// Выдаёт персонажу ступень важности папки, в которую он попал, —
        /// при создании и при переносе.
        /// </summary>
        private void ApplyFolderImportance(Character character, CharacterFolder? folder)
        {
            if (folder is null) return;

            var level = folder.ImportanceLevel ?? CharacterImportanceLevel.Tertiary;
            if (character.ImportanceLevel == level) return;

            character.ImportanceLevel = level;
            _characterService.Update(character);
        }

        private List<CharacterAnketa> GetActiveAnketas() =>
            ActiveTemplateIds
                .Select(id => _anketaService.GetById(id))
                .Where(a => a is not null)
                .Cast<CharacterAnketa>()
                .ToList();

        private void SelectCharacter(string characterId)
        {
            foreach (var folder in Folders)
                foreach (var item in folder.Characters)
                    item.IsSelected = item.Id == characterId;
            foreach (var item in FilteredCharacters)
                item.IsSelected = item.Id == characterId;
        }

        public void EditCharacter(string characterId)
        {
            var character = _characterService.GetById(characterId);
            if (character is null) return;
            SelectedCharacterCard = new CharacterCardViewModel(
                _characterService, _relationshipService, _anketaService, character, _avatarService,
                // Папки нужны карточке для групповых обращений: «все из этой
                // папки зовут её так».
                GetFolders());

            // Смена аватара из галереи кладётся в общий стек отмены модуля:
            // Ctrl+Z возвращает прежний, как и для остальных действий.
            SelectedCharacterCard.BasicsTab.PushUndoableAvatarChange = (oldRef, newRef) =>
            {
                var id = character.Id;

                PushCommand(new Actions.SetAvatarCommand(id, oldRef, newRef, (cid, value) =>
                {
                    var card = SelectedCharacterCard;
                    if (card != null && card.CharacterId == cid)
                    {
                        card.BasicsTab.ApplyAvatarSilently(value);
                        return;
                    }

                    // Карточка закрыта — правим модель напрямую, чтобы отмена
                    // работала и после ухода с персонажа.
                    var target = _characterService.GetById(cid);
                    if (target == null) return;

                    target.AvatarPath = value;
                    _characterService.Update(target);

                    FindListItem(cid)?.ApplyAvatarRef(value);
                }));
            };
            // Автосейв карточки кладёт правки в сервис, но строки бокового
            // списка — снимки и сами об этом не узнают. По событию Saved
            // обновляются все вью-модели этого персонажа в списках.
            SelectedCharacterCard.Saved += OnCardSaved;
            // «Применить кольцо ко всем» из редактора цвета в карточке — тот же
            // обработчик, что у карточек основного списка (персист + Undo).
            SelectedCharacterCard.BasicsTab.OnApplyRingToAll = ApplyRingToAllCharacters;
            IsCardOpen = true;
            MainTabIndex = 1;
        }

        private void OnCardSaved(string characterId)
        {
            var character = _characterService.GetById(characterId);
            if (character is null) return;

            foreach (var folder in Folders)
            {
                var item = folder.Characters.FirstOrDefault(c => c.Id == characterId);
                if (item is not null) SyncRowFromCharacter(item, character);
            }

            var filtered = FilteredCharacters.FirstOrDefault(c => c.Id == characterId);
            if (filtered is not null) SyncRowFromCharacter(filtered, character);
        }

        /// <summary>
        /// Вью-модель строки списка для персонажа. Нужна окнам, которые
        /// работают через колбэки строки (настройки карточки, открытые
        /// из карточки персонажа в редакторе).
        /// </summary>
        public CharacterListItemViewModel? FindListItem(string characterId)
        {
            foreach (var folder in Folders)
            {
                var item = folder.Characters.FirstOrDefault(c => c.Id == characterId);
                if (item is not null) return item;
            }
            return FilteredCharacters.FirstOrDefault(c => c.Id == characterId);
        }

        // Переносит в строку списка поля, редактируемые на вкладке Basics.
        // Цвет присваивается только при реальном изменении: его сеттер дёргает
        // колбэк OnColorChanged (персист + перерисовка) при каждом присваивании.
        private static void SyncRowFromCharacter(CharacterListItemViewModel item, Character character)
        {
            item.Name = character.Name;
            item.ShortDescription = character.ShortDescription;
            item.FallbackIcon = character.FallbackIcon;
            item.SetLabels(character.Labels);
            // Аватар переносится наравне с остальным. Без этого смена фото в
            // карточке персонажа доезжала до модели, но не до строк списков:
            // они хранят собственную ссылку и загруженный по ней битмап, и
            // показывали прежнее фото до пересбора списка.
            item.SyncAvatarRef(character.AvatarPath);
            if (item.Color != character.Color) item.Color = character.Color;
            if (item.AvatarRing != character.AvatarRing) item.AvatarRing = character.AvatarRing;
            if (item.GroupBookmark != character.GroupBookmark) item.GroupBookmark = character.GroupBookmark;
            if (item.IsCollective != character.IsCollective) item.IsCollective = character.IsCollective;
        }

        /// <summary>
        /// Перечитать метки во всех строках списка из модели. Нужно после
        /// правки общей метки: она меняет метки сразу у многих персонажей,
        /// а каждая строка держит свою копию списка меток.
        /// </summary>
        public void RefreshLabelsFromModel()
        {
            foreach (var folder in Folders)
                foreach (var item in folder.Characters)
                    RefreshLabels(item);

            foreach (var item in FilteredCharacters)
                RefreshLabels(item);
        }

        private void RefreshLabels(CharacterListItemViewModel item)
        {
            var character = _characterService.GetById(item.Id);
            if (character != null) item.SetLabels(character.Labels);
        }

        public void OpenCharacter(string characterId) => EditCharacter(characterId);

        private void ConfirmInlineName(string characterId)
        {
            var character = _characterService.GetById(characterId);
            if (character is null) return;
            string? newName = null;
            foreach (var folder in Folders)
            {
                var item = folder.Characters.FirstOrDefault(c => c.Id == characterId);
                if (item is not null)
                {
                    newName = string.IsNullOrWhiteSpace(item.InlineName)
                        ? CharactersStrings.Character_DefaultName
                        : item.InlineName.Trim();
                    item.IsBeingNamed = false;
                    break;
                }
            }
            if (newName is not null)
            {
                character.Name = newName;
                _characterService.Update(character);
            }
            RefreshFolderViewModels();
            ApplyFilters();
        }

        private void CancelInlineName(string characterId)
        {
            _characterService.Delete(characterId);
            foreach (var f in _folders) f.CharacterIds.Remove(characterId);
            RefreshFolderViewModels();
            ApplyFilters();
        }

        private void DeleteCharacter(string characterId)
        {
            if (IsReadOnly) return;
            var character = _characterService.GetById(characterId);
            if (character is null) return;

            string? folderId = null;
            int folderIndex = 0;
            foreach (var folderVm in Folders)
            {
                var item = folderVm.Characters.FirstOrDefault(c => c.Id == characterId);
                if (item is not null)
                {
                    folderId = folderVm.FolderId;
                    folderIndex = folderVm.Characters.IndexOf(item);
                    break;
                }
            }

            if (folderId is null)
                folderId = _folders.FirstOrDefault(f => f.CharacterIds.Contains(characterId))?.Id;

            _trash.Add(character, folderId, folderIndex);
            DeleteCharacterCore(characterId);

            PushCommand(new DeleteCharacterCommand(
                characterId,
                character.Name,
                id => DeleteCharacterAndAddToTrash(id),
                id => RestoreFromTrash(id)));

            ShowUndoToast(CharactersStrings.Toast_CharacterDeleted);
        }

        private void DeleteCharacterAndAddToTrash(string characterId)
        {
            var character = _characterService.GetById(characterId);
            if (character is null) return;
            var folderId = _folders.FirstOrDefault(f => f.CharacterIds.Contains(characterId))?.Id;
            var folderIdx = 0;
            var folder = _folders.FirstOrDefault(f => f.Id == folderId);
            if (folder is not null) folderIdx = folder.CharacterIds.IndexOf(characterId);
            _trash.Add(character, folderId, folderIdx);
            DeleteCharacterCore(characterId);
        }

        private void DeleteCharacterCore(string characterId)
        {
            _characterService.Delete(characterId);
            foreach (var f in _folders) f.CharacterIds.Remove(characterId);
            if (SelectedCharacterCard?.CharacterId == characterId)
            {
                IsCardOpen = false;
                SelectedCharacterCard = null;
            }

            RefreshTags();
            RemoveCharacterFromFilteredList(characterId);
            RemoveCharacterFromFolderViewModels(characterId);
            GraphViewModel.RemoveCharacterNode(characterId);
        }

        /// <summary>
        /// Точечное удаление одной карточки из отфильтрованного списка — без полной
        /// пересборки FilteredCharacters. ApplyFilters создаёт новый
        /// CharacterListItemViewModel для каждого персонажа в списке, что при
        /// сотнях персонажей даёт заметный фриз при удалении одной карточки.
        /// Если карточки нет в списке (отфильтрована поиском/тегом), ничего не делает.
        /// </summary>
        private void RemoveCharacterFromFilteredList(string characterId)
        {
            var item = FilteredCharacters.FirstOrDefault(c => c.Id == characterId);
            if (item is not null)
                FilteredCharacters.Remove(item);
        }

        /// <summary>
        /// Точечное удаление одной карточки из Folders (что реально рисует список
        /// в модуле) — без RefreshFolderViewModelsAsync. Тот метод делает
        /// Folders.Clear() и заново, батчами, создаёт CharacterFolderViewModel и
        /// CharacterListItemViewModel для ВСЕХ папок и ВСЕХ персонажей — то есть
        /// удаление одной карточки полностью пересобирало весь список. Модель
        /// (_folders[].CharacterIds) уже обновлена в DeleteCharacterCore выше,
        /// здесь только убираем соответствующий элемент из уже существующих
        /// ViewModel-папок.
        /// </summary>
        private void RemoveCharacterFromFolderViewModels(string characterId)
        {
            foreach (var folderVm in Folders)
            {
                var item = folderVm.Characters.FirstOrDefault(c => c.Id == characterId);
                if (item is not null)
                {
                    folderVm.Characters.Remove(item);
                    break;
                }
            }
        }

        public void RestoreFromTrash(string characterId)
        {
            var result = _trash.Restore(characterId);
            if (result is null) return;
            var (character, origFolderId, origIndex) = result.Value;

            var targetFolder = _folders.FirstOrDefault(f => f.Id == origFolderId)
                ?? _folders.FirstOrDefault();
            int clampedIdx = 0;
            if (targetFolder is not null)
            {
                clampedIdx = Math.Min(origIndex, targetFolder.CharacterIds.Count);
                targetFolder.CharacterIds.Insert(clampedIdx, character.Id);
            }

            RefreshTags();

            // Точечная вставка вместо полного RefreshAll(): восстановление одной
            // карточки не должно сносить Folders.Clear()'ом весь список и заново
            // прогрессивно строить сотни CharacterListItemViewModel — иначе
            // возвращённые карточки «появляются заново» пачкой. Симметрично
            // удалению (RemoveCharacterFrom*). Модель (_folders[].CharacterIds и
            // сервис через CreateWithId в _trash.Restore) уже согласована выше.
            var folderVm = targetFolder is null
                ? null
                : Folders.FirstOrDefault(f => f.FolderId == targetFolder.Id);

            // Папки ещё не построены (вьюха отсоединена или первый показ) —
            // подстраховываемся полным рефрешем.
            if (folderVm is null)
            {
                RefreshAll();
                return;
            }

            InsertCharacterIntoFolderViewModels(folderVm, character, clampedIdx);
            InsertCharacterIntoFilteredList(character);
            GraphViewModel.Refresh();
        }

        /// <summary>
        /// Точечная вставка карточки в уже существующую ViewModel-папку — зеркало
        /// RemoveCharacterFromFolderViewModels. GroupBookmark, IsCollective, цвет и
        /// аватар берутся из модели персонажа, поэтому закладка групповой карточки
        /// сохраняется без отдельной обработки.
        /// </summary>
        private void InsertCharacterIntoFolderViewModels(
            CharacterFolderViewModel folderVm, Character character, int index)
        {
            if (folderVm.Characters.Any(c => c.Id == character.Id)) return;

            var relCount = _relationshipService.GetAllForCharacter(character.Id).Count;
            var item = new CharacterListItemViewModel(character, relCount, false, _avatarService);
            BindCharacterItemCallbacks(item);

            var clampedIdx = Math.Min(index, folderVm.Characters.Count);
            folderVm.Characters.Insert(clampedIdx, item);
            folderVm.IsExpanded = true;
        }

        /// <summary>
        /// Точечная вставка в отфильтрованный список — зеркало
        /// RemoveCharacterFromFilteredList. Персонаж добавляется только если
        /// проходит текущие фильтры. Позиция — в конец: CreateWithId возвращает
        /// персонажа последним в _characterService.GetAll(), поэтому это совпадает
        /// с порядком, который дал бы полный ApplyFilters.
        /// </summary>
        private void InsertCharacterIntoFilteredList(Character character)
        {
            if (!PassesCurrentFilters(character)) return;
            if (FilteredCharacters.Any(c => c.Id == character.Id)) return;

            var relCount = _relationshipService.GetAllForCharacter(character.Id).Count;
            var item = new CharacterListItemViewModel(character, relCount, false, _avatarService);
            var owningFolder = _folders.FirstOrDefault(f => f.CharacterIds.Contains(character.Id));
            if (owningFolder != null)
                item.SearchFolderColor = owningFolder.Color;
            FilteredCharacters.Add(item);
        }

        /// <summary>
        /// Проходит ли персонаж активные фильтры списка. Поиск делегируется
        /// _characterService.Search, чтобы не дублировать его логику.
        /// </summary>
        private bool PassesCurrentFilters(Character c)
        {
            if (!string.IsNullOrWhiteSpace(SearchQuery)
                && !_characterService.Search(SearchQuery).Any(x => x.Id == c.Id))
                return false;
            if (ActiveTagFilters.Any() && !c.Tags.Any(t => ActiveTagFilters.Contains(t)))
                return false;
            if (FilterImportance.HasValue && c.ImportanceLevel != FilterImportance.Value)
                return false;
            if (FilterCollectiveOnly && !c.IsCollective)
                return false;
            return true;
        }

        private void DuplicateCharacter(string characterId)
        {
            if (IsReadOnly) return;
            var copy = _characterService.Duplicate(characterId);
            RefreshAll();
            OpenCharacter(copy.Id);
        }

        public void RefreshAll()
        {
            RefreshTags();
            ApplyFilters();
            GraphViewModel.Refresh();
            _ = RefreshFolderViewModelsAsync();
        }

        public void CancelLoad()
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;

            // Прерываем и прогрессивное наполнение списка карточек: при перезагрузке
            // данных (SetCustomData) недобавленные карточки строились бы по старому
            // набору персонажей.
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = null;
        }

        // Прогрессивная загрузка: папки добавляются пустыми, затем карточки
        // заполняются небольшими батчами с Background-приоритетом между
        // батчами — UI не фризится. Размер батча задаётся в
        // RefreshFolderViewModelsProgressiveAsync.
        private async Task RefreshFolderViewModelsAsync()
        {
            CancelLoad();
            _refreshCts = new CancellationTokenSource();
            var ct = _refreshCts.Token;

            IsLoading = true;
            try
            {
                await RefreshFolderViewModelsProgressiveAsync(ct);
            }
            catch (OperationCanceledException) { }
            finally { IsLoading = false; }
        }

        /// <summary>
        /// Публичный триггер для повторного прогрессивного рефреша.
        /// Вызывается когда CharactersModuleView переподключается к visual tree
        /// (workmode switch, dock move) — чтобы карточки появлялись плавно
        /// вместо моментального layout pass всей коллекции.
        /// </summary>
        public Task RequestProgressiveRefreshAsync()
            => RefreshFolderViewModelsAsync();

        /// <summary>
        /// Готовит VM к отсоединению вьюхи (workmode switch, dock move). Прерывает
        /// незавершённую прогрессивную загрузку и очищает список папок.
        /// Без очистки при повторном attach ItemsRepeater синхронно реализует и
        /// раскладывает все ранее построенные карточки одним проходом и фризит UI
        /// на ~секунду — ещё до того, как OnLoaded запустит прогрессивный рефреш.
        /// Данные не теряются: RequestProgressiveRefreshAsync восстанавливает список
        /// из _folders и сервиса при следующем подключении вьюхи.
        /// </summary>
        public void PrepareForReattach()
        {
            CancelLoad();
            Folders.Clear();
        }

        private void RefreshTags()
        {
            AvailableTags.Clear();
            foreach (var tag in _characterService.GetAllTags()) AvailableTags.Add(tag);
        }

        // Отмена незавершённого прогрессивного наполнения списка: новый вызов
        // ApplyFilters (поиск, фильтры, повторная загрузка данных) прерывает
        // предыдущий проход между батчами.
        private System.Threading.CancellationTokenSource? _filterCts;

        private void ApplyFilters()
        {
            IReadOnlyList<Character> all;
            if (!string.IsNullOrWhiteSpace(SearchQuery))
                all = _characterService.Search(SearchQuery);
            else
                all = _characterService.GetAll();
            if (ActiveTagFilters.Any())
                all = all.Where(c => c.Tags.Any(t => ActiveTagFilters.Contains(t))).ToList().AsReadOnly();
            if (FilterImportance.HasValue)
                all = all.Where(c => c.ImportanceLevel == FilterImportance.Value).ToList().AsReadOnly();
            if (FilterCollectiveOnly)
                all = all.Where(c => c.IsCollective).ToList().AsReadOnly();

            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = new System.Threading.CancellationTokenSource();

            // Прогрессивное наполнение списка: карточки добавляются батчами с
            // Background-приоритетом между ними — тот же паттерн, что у
            // RefreshFolderViewModelsProgressiveAsync. Создание сотен
            // CharacterListItemViewModel (включая аватарки) одним проходом
            // блокировало UI-поток на секунды при загрузке модуля.
            _ = ApplyFiltersProgressiveAsync(all, _filterCts.Token);
        }

        private async Task ApplyFiltersProgressiveAsync(
            IReadOnlyList<Character> all,
            System.Threading.CancellationToken ct)
        {
            const int BatchSize = 30;

            FilteredCharacters.Clear();

            // Карта персонаж -> цвет папки: точка-индикатор на баннере результата
            // поиска показывает, из какой папки взят персонаж.
            var folderColorById = new Dictionary<string, string>();
            foreach (var folder in _folders)
                foreach (var cid in folder.CharacterIds)
                    folderColorById[cid] = folder.Color;

            for (int i = 0; i < all.Count; i += BatchSize)
            {
                if (ct.IsCancellationRequested) return;

                int end = System.Math.Min(i + BatchSize, all.Count);
                for (int j = i; j < end; j++)
                {
                    var c = all[j];
                    var relCount = _relationshipService.GetAllForCharacter(c.Id).Count;
                    var item = new CharacterListItemViewModel(c, relCount, false, _avatarService);
                    if (folderColorById.TryGetValue(c.Id, out var folderColor))
                        item.SearchFolderColor = folderColor;
                    item.MatchedName = ResolveMatchedName(c);
                    FilteredCharacters.Add(item);
                }

                // Отдаём диспетчер между батчами: ввод и рендер не блокируются.
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => { },
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Имя, по которому карточка попала в результат поиска, если оно не
        /// отображаемое. Пустая строка — совпало по имени, описанию, заметке
        /// или тегу, и пояснять нечего.
        /// </summary>
        private string ResolveMatchedName(Character character)
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return string.Empty;

            var query = SearchQuery.Trim();
            if (character.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                return string.Empty;

            foreach (var name in Models.CharacterNames.AllValues(character))
            {
                if (string.Equals(name, character.Name, StringComparison.CurrentCultureIgnoreCase)) continue;
                // Скобки собираются здесь, чтобы шаблон строки списка остался
                // простой привязкой без конвертера.
                if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return $"({name})";
            }

            return string.Empty;
        }

        private void ClearFilters()
        {
            ActiveTagFilters.Clear();
            SearchQuery = string.Empty;
            ApplyFilters();
        }

        private void ToggleTagFilter(string tag)
        {
            if (ActiveTagFilters.Contains(tag)) ActiveTagFilters.Remove(tag);
            else ActiveTagFilters.Add(tag);
            ApplyFilters();
        }

        public CharactersModuleSession GetSessionState() => new()
        {
            LastOpenedCharacterId = SelectedCharacterCard?.CharacterId,
            LastViewMode = ViewMode.ToString(),
            MainTabIndex = MainTabIndex,
            ActiveTagFilters = ActiveTagFilters.ToList(),
            LastSearchQuery = SearchQuery,
            ActiveTemplateIds = ActiveTemplateIds.ToList(),
            GraphOffsetX = GraphViewModel.OffsetX,
            GraphOffsetY = GraphViewModel.OffsetY,
            GraphScale = GraphViewModel.Scale,
            EditorSidebarWidth = EditorSidebarWidth,
            EditorSidebarMode = EditorSidebarMode
        };

        // Ширина бокового списка вкладки Character Editor. Сохраняется в сессии
        // модуля; вью применяет её к колонке при загрузке и записывает обратно
        // после перетаскивания сплиттера.
        private double _editorSidebarWidth = 240;
        public double EditorSidebarWidth
        {
            get => _editorSidebarWidth;
            set => this.RaiseAndSetIfChanged(ref _editorSidebarWidth, value);
        }

        // Режим бокового списка Редактора: 0 — полный (аватар + подписи),
        // 1 — компактный (только аватарки), 2 — скрыт (узкая полоса с кнопкой
        // разворота). Сохраняется в сессии модуля; ширину колонки под режим
        // выставляет code-behind вьюхи.
        private int _editorSidebarMode;
        public int EditorSidebarMode
        {
            get => _editorSidebarMode;
            set
            {
                if (value < 0 || value > 2) value = 0;
                if (value == 2 && _editorSidebarMode != 2)
                    _sidebarModeBeforeHide = _editorSidebarMode;
                this.RaiseAndSetIfChanged(ref _editorSidebarMode, value);
                this.RaisePropertyChanged(nameof(IsSidebarFull));
                this.RaisePropertyChanged(nameof(IsSidebarCompact));
                this.RaisePropertyChanged(nameof(IsSidebarHidden));
                this.RaisePropertyChanged(nameof(IsSidebarShown));
            }
        }

        private int _sidebarModeBeforeHide;

        public bool IsSidebarFull => _editorSidebarMode == 0;
        public bool IsSidebarCompact => _editorSidebarMode == 1;
        public bool IsSidebarHidden => _editorSidebarMode == 2;
        public bool IsSidebarShown => _editorSidebarMode != 2;

        /// <summary>Разворачивает скрытый список в режим, из которого его скрыли.</summary>
        public void RestoreSidebar() => EditorSidebarMode = _sidebarModeBeforeHide;

        public void RestoreSessionState(CharactersModuleSession session)
        {
            if (Enum.TryParse<CharactersViewMode>(session.LastViewMode, out var mode))
            {
                if (mode == CharactersViewMode.Grid) mode = CharactersViewMode.GridMedium;
                ViewMode = mode;
            }
            if (session.EditorSidebarWidth >= 170 && session.EditorSidebarWidth <= 520)
                EditorSidebarWidth = session.EditorSidebarWidth;
            if (session.EditorSidebarMode >= 0 && session.EditorSidebarMode <= 2)
                EditorSidebarMode = session.EditorSidebarMode;
            MainTabIndex = session.MainTabIndex;
            SearchQuery = session.LastSearchQuery ?? string.Empty;
            ActiveTagFilters.Clear();
            foreach (var tag in session.ActiveTagFilters) ActiveTagFilters.Add(tag);
            ActiveTemplateIds.Clear();
            foreach (var id in session.ActiveTemplateIds) ActiveTemplateIds.Add(id);
            TemplatesViewModel.Refresh();
            ApplyFilters();
            if (!string.IsNullOrEmpty(session.LastOpenedCharacterId))
                OpenCharacter(session.LastOpenedCharacterId);
            GraphViewModel.OffsetX = session.GraphOffsetX;
            GraphViewModel.OffsetY = session.GraphOffsetY;
            GraphViewModel.Scale = session.GraphScale;
        }

        // ── drag preview API ───────────────────────────────────────────────

        private CharacterListItemViewModel? _dragPlaceholder;
        private CharacterListItemViewModel? _dragItem;

        public void BeginDragPreview(string charId)
        {
            if (IsReadOnly) return;
            _previewDragCharId = charId;
            foreach (var folder in Folders)
            {
                var item = folder.Characters.FirstOrDefault(c => c.Id == charId);
                if (item is not null)
                {
                    _previewDragOriginalFolderId = folder.FolderId;
                    _previewDragOriginalIndex = folder.Characters.IndexOf(item);
                    item.IsDragging = true;
                    _dragItem = item;

                    // Плейсхолдер — копия перетаскиваемой карточки (аватар, имя, цвет),
                    // только тусклее (DragOpacity). Так в сетке остаётся та же карточка.
                    _dragPlaceholder = new CharacterListItemViewModel(
                        new Models.Character
                        {
                            Id = "__placeholder__",
                            Name = item.Name,
                            Color = item.Color,
                            FallbackIcon = item.FallbackIcon,
                            AvatarPath = item.AvatarPath,
                            // Признаки группы и кольца копируются, иначе на время
                            // перетаскивания карточка-плейсхолдер теряет закладку
                            // и кольцо и выглядит чужой.
                            IsCollective = item.IsCollective,
                            GroupBookmark = item.GroupBookmark,
                            AvatarRing = item.AvatarRing,
                            FrameThickness = item.FrameThickness,
                            AvatarStrip = item.AvatarStrip
                        },
                        0, false, AvatarService)
                    { IsPlaceholder = true, IsDragging = true };

                    var idx = folder.Characters.IndexOf(item);
                    try
                    {
                        folder.Characters.Remove(item);
                        folder.Characters.Insert(idx, _dragPlaceholder);
                    }
                    catch (NotImplementedException) { }
                    return;
                }
            }
        }

        public void UpdateDragPreview(string charId, string targetFolderId, int targetIndex)
        {
            if (_previewDragCharId != charId) return;
            if (_dragPlaceholder is null) return;

            CharacterFolderViewModel? sourceFolderVm = null;
            int sourceIdx = -1;
            foreach (var folder in Folders)
            {
                var idx = folder.Characters.IndexOf(_dragPlaceholder);
                if (idx >= 0) { sourceFolderVm = folder; sourceIdx = idx; break; }
            }

            var targetFolderVm = Folders.FirstOrDefault(f => f.FolderId == targetFolderId);
            if (targetFolderVm is null) return;

            var clampedIdx = Math.Min(targetIndex, targetFolderVm.Characters.Count);

            // Обёртка от бага Avalonia 12: UniformGridLayout.ClearElementOnDataSourceChange
            // не реализован и кидает NotImplementedException при перестройке коллекции в
            // виртуализованном состоянии (например, у нижних карточек длинного списка).
            // Данные переставляются корректно до выброса; раскладку дотянет UpdateLayout.
            try
            {
                if (sourceFolderVm != null && ReferenceEquals(sourceFolderVm, targetFolderVm))
                {
                    // Та же папка: Move сохраняет инстансы элементов в ItemsRepeater.
                    // Remove+Insert создаёт новый элемент и ломает TranslateTransform анимацию.
                    // targetIndex уже целевая ЯЧЕЙКА под курсором — кладём плейсхолдер прямо
                    // туда (без -1), иначе при движении вниз он встаёт на блок левее.
                    var dest = Math.Min(clampedIdx, sourceFolderVm.Characters.Count - 1);
                    if (dest != sourceIdx)
                        sourceFolderVm.Characters.Move(sourceIdx, dest);
                }
                else
                {
                    if (sourceFolderVm != null)
                        sourceFolderVm.Characters.Remove(_dragPlaceholder);
                    var clampedCross = Math.Min(clampedIdx, targetFolderVm.Characters.Count);
                    targetFolderVm.Characters.Insert(clampedCross, _dragPlaceholder);
                }
            }
            catch (NotImplementedException) { }
        }

        public void CancelDragPreview(string charId)
        {
            if (_previewDragCharId != charId) return;

            if (_dragPlaceholder is not null)
            {
                try
                {
                    foreach (var folder in Folders)
                        folder.Characters.Remove(_dragPlaceholder);
                }
                catch (NotImplementedException) { }
                _dragPlaceholder = null;
            }

            if (_dragItem is not null)
            {
                _dragItem.IsDragging = false;
                var origFolderVm = Folders.FirstOrDefault(f => f.FolderId == _previewDragOriginalFolderId);
                if (origFolderVm is not null)
                {
                    var idx = Math.Min(_previewDragOriginalIndex, origFolderVm.Characters.Count);
                    try { origFolderVm.Characters.Insert(idx, _dragItem); }
                    catch (NotImplementedException) { }
                }
                _dragItem = null;
            }

            _previewDragCharId = null;
        }

        public void CommitDragPreview(string charId, string targetFolderId, int targetIndex)
        {
            if (_previewDragCharId != charId) return;

            var origFolderId = _previewDragOriginalFolderId;
            var origIndex = _previewDragOriginalIndex;

            int finalIndex = targetIndex;
            CharacterFolderViewModel? finalFolderVm = null;
            if (_dragPlaceholder is not null)
            {
                foreach (var folder in Folders)
                {
                    var phIdx = folder.Characters.IndexOf(_dragPlaceholder);
                    if (phIdx >= 0)
                    {
                        finalIndex = phIdx;
                        finalFolderVm = folder;
                        try { folder.Characters.Remove(_dragPlaceholder); }
                        catch (NotImplementedException) { }
                        break;
                    }
                }
                _dragPlaceholder = null;
            }

            if (finalFolderVm is null)
                finalFolderVm = Folders.FirstOrDefault(f => f.FolderId == targetFolderId);

            var item = _dragItem;
            _dragItem = null;

            if (item is not null)
            {
                item.IsDragging = false;

                if (finalFolderVm is not null)
                {
                    var clampedVm = Math.Min(finalIndex, finalFolderVm.Characters.Count);
                    try { finalFolderVm.Characters.Insert(clampedVm, item); }
                    catch (NotImplementedException) { }
                }

                bool posChanged = finalFolderVm?.FolderId != origFolderId || finalIndex != origIndex;
                if (posChanged && finalFolderVm is not null)
                {
                    foreach (var f in _folders) f.CharacterIds.Remove(charId);
                    var modelFolder = _folders.FirstOrDefault(f => f.Id == finalFolderVm.FolderId);
                    if (modelFolder is not null)
                    {
                        var clampedModel = Math.Min(finalIndex, modelFolder.CharacterIds.Count);
                        modelFolder.CharacterIds.Insert(clampedModel, charId);

                        // Переехал в другую папку — принимает её ступень.
                        // Перестановка внутри своей же папки важности не трогает.
                        if (finalFolderVm.FolderId != origFolderId)
                        {
                            var moved = _characterService.GetById(charId);
                            if (moved is not null)
                                ApplyFolderImportance(moved, modelFolder);
                        }
                    }

                    var capturedOrigFolder = origFolderId ?? finalFolderVm.FolderId;
                    PushCommand(new MoveCharacterCommand(
                        charId, item.Name,
                        capturedOrigFolder, origIndex,
                        finalFolderVm.FolderId, finalIndex,
                        (cid, fid, fidx) => RestoreCharacterPosition(cid, fid, fidx)));
                }
            }

            _previewDragCharId = null;
        }

        private void RestoreCharacterPosition(string charId, string folderId, int index)
        {
            foreach (var f in _folders) f.CharacterIds.Remove(charId);
            var targetFolder = _folders.FirstOrDefault(f => f.Id == folderId)
                ?? _folders.FirstOrDefault();
            if (targetFolder is not null)
            {
                var clampedIdx = Math.Min(index, targetFolder.CharacterIds.Count);
                targetFolder.CharacterIds.Insert(clampedIdx, charId);
            }
            RefreshFolderViewModels();
        }

        // ── папки ─────────────────────────────────────────────────────────

        private readonly List<CharacterFolder> _folders = new();

        private void EnsureDefaultFolders()
        {
            if (_folders.Count == 0)
            {
                // Ступени у двух начальных папок расставлены по смыслу их
                // названий: в «Главных» персонаж сразу первой ступени, во
                // «Второстепенных» — второй. Выбирать это руками для каждой
                // карточки бессмысленно, папка о том и говорит.
                _folders.Add(new CharacterFolder
                {
                    Id = "default_main",
                    Name = CharactersStrings.Folder_DefaultMain,
                    Comment = string.Empty,
                    Color = "#E07B39",
                    Order = 0,
                    ImportanceLevel = CharacterImportanceLevel.Primary
                });
                _folders.Add(new CharacterFolder
                {
                    Id = "default_secondary",
                    Name = CharactersStrings.Folder_DefaultSecondary,
                    Comment = string.Empty,
                    Color = "#607D8B",
                    Order = 1,
                    ImportanceLevel = CharacterImportanceLevel.Secondary
                });
            }
            RefreshFolderViewModels();
            if (ActiveFolderId is null)
                ActiveFolderId = _folders.FirstOrDefault()?.Id;
        }

        private void RefreshFolderViewModels(string? inlineBeingNamedId = null, string? newlyCreatedFolderId = null)
        {
            var allChars = _characterService.GetAll().ToList();
            var assignedIds = _folders.SelectMany(f => f.CharacterIds).ToHashSet();
            var unassigned = allChars.Where(c => !assignedIds.Contains(c.Id)).ToList();

            var expandedState = Folders.GroupBy(f => f.FolderId).ToDictionary(g => g.Key, g => g.Last().IsExpanded);

            Folders.Clear();
            foreach (var folder in _folders.OrderBy(f => f.Order))
            {
                var capturedFolder = folder;

                bool isExpanded = folder.Id == newlyCreatedFolderId
                    ? true
                    : expandedState.GetValueOrDefault(folder.Id, true);

                var vm = new CharacterFolderViewModel(folder)
                {
                    IsExpanded = isExpanded,
                    IsSelected = folder.Id == ActiveFolderId,
                    IsRenaming = folder.Id == newlyCreatedFolderId,
                    OnSelectRequested = id => ActiveFolderId = id,
                    IsReadOnlyProvider = () => IsReadOnly,
                    EditCommand = EditCharacterCommand,
                    ConfirmCommand = ConfirmInlineNameCommand,
                    CancelCommand = CancelInlineNameCommand,
                    ToggleCommand = ToggleFolderCommand,
                    RequestDeleteCommand = ReactiveCommand.Create(() =>
                        FolderDeleteRequested?.Invoke(capturedFolder.Id, capturedFolder.Name))
                };
                foreach (var id in folder.CharacterIds)
                {
                    var c = allChars.FirstOrDefault(x => x.Id == id);
                    if (c is not null)
                    {
                        var relCount = _relationshipService.GetAllForCharacter(c.Id).Count;
                        var isNaming = c.Id == inlineBeingNamedId;
                        var item = new CharacterListItemViewModel(c, relCount, isNaming, _avatarService);
                        BindCharacterItemCallbacks(item);
                        vm.Characters.Add(item);
                    }
                }
                Folders.Add(vm);
            }

            if (unassigned.Count > 0)
            {
                bool ungroupedExpanded = expandedState.GetValueOrDefault("ungrouped", true);
                var ungrouped = new CharacterFolderViewModel(new CharacterFolder
                {
                    Id = "ungrouped",
                    Name = CharactersStrings.Folder_Ungrouped,
                    Comment = string.Empty,
                    Color = "#455A64",
                    Order = 999
                })
                {
                    IsExpanded = ungroupedExpanded,
                    IsSelected = "ungrouped" == ActiveFolderId,
                    IsRenaming = false,
                    OnSelectRequested = id => ActiveFolderId = id,
                    IsReadOnlyProvider = () => IsReadOnly,
                    EditCommand = EditCharacterCommand,
                    ConfirmCommand = ConfirmInlineNameCommand,
                    CancelCommand = CancelInlineNameCommand,
                    ToggleCommand = ToggleFolderCommand,
                    RequestDeleteCommand = null
                };
                foreach (var c in unassigned)
                {
                    var relCount = _relationshipService.GetAllForCharacter(c.Id).Count;
                    var isNaming = c.Id == inlineBeingNamedId;
                    var item = new CharacterListItemViewModel(c, relCount, isNaming, _avatarService);
                    BindCharacterItemCallbacks(item);
                    ungrouped.Characters.Add(item);
                }
                Folders.Add(ungrouped);
            }
        }

        private async Task RefreshFolderViewModelsProgressiveAsync(
            CancellationToken ct,
            string? inlineBeingNamedId = null,
            string? newlyCreatedFolderId = null)
        {
            // Размер пачки зависит от активной вкладки. На вкладке «Персонажи»
            // (index 0) добавление персонажа тут же реализует его карточку (список
            // карточек не виртуализирован) — это дорого, поэтому грузим по одному,
            // чтобы карточки появлялись плавно, не подвешивая UI. На остальных
            // вкладках (Редактор и т.д.) карточки не строятся вообще, а создание
            // самой VM теперь дёшево (команды ленивые), поэтому грузим крупными
            // пачками — данные наполняются быстро, и скролл в Редакторе не дёргается
            // от бесконечного дорастания списка.
            int batchSize = MainTabIndex == 0 ? 1 : 100;

            var allChars = await Task.Run(() => _characterService.GetAll().ToList(), ct);
            var assignedIds = _folders.SelectMany(f => f.CharacterIds).ToHashSet();
            var unassigned = allChars.Where(c => !assignedIds.Contains(c.Id)).ToList();
            var expandedState = Folders.GroupBy(f => f.FolderId).ToDictionary(g => g.Key, g => g.Last().IsExpanded);

            ct.ThrowIfCancellationRequested();

            Folders.Clear();

            foreach (var folder in _folders.OrderBy(f => f.Order))
            {
                ct.ThrowIfCancellationRequested();

                var capturedFolder = folder;
                bool isExpanded = folder.Id == newlyCreatedFolderId
                    ? true
                    : expandedState.GetValueOrDefault(folder.Id, true);

                var vm = new CharacterFolderViewModel(folder)
                {
                    IsExpanded = isExpanded,
                    IsSelected = folder.Id == ActiveFolderId,
                    IsRenaming = folder.Id == newlyCreatedFolderId,
                    OnSelectRequested = id => ActiveFolderId = id,
                    IsReadOnlyProvider = () => IsReadOnly,
                    EditCommand = EditCharacterCommand,
                    ConfirmCommand = ConfirmInlineNameCommand,
                    CancelCommand = CancelInlineNameCommand,
                    ToggleCommand = ToggleFolderCommand,
                    RequestDeleteCommand = ReactiveCommand.Create(() =>
                        FolderDeleteRequested?.Invoke(capturedFolder.Id, capturedFolder.Name))
                };

                // Папка добавляется сразу — пользователь видит что что-то происходит.
                Folders.Add(vm);

                // Карточки добавляем батчами чтобы не фризить UI.
                var ids = folder.CharacterIds.ToList();
                for (int i = 0; i < ids.Count; i += batchSize)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = ids.Skip(i).Take(batchSize);
                    foreach (var id in batch)
                    {
                        var c = allChars.FirstOrDefault(x => x.Id == id);
                        if (c is not null)
                        {
                            var relCount = _relationshipService.GetAllForCharacter(c.Id).Count;
                            var isNaming = c.Id == inlineBeingNamedId;
                            var item = new CharacterListItemViewModel(c, relCount, isNaming, _avatarService);
                            BindCharacterItemCallbacks(item);
                            vm.Characters.Add(item);
                        }
                    }

                    // Отпускаем поток между батчами — UI успевает обработать события.
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => { }, Avalonia.Threading.DispatcherPriority.Background);

                    // Оверлей «Loading characters...» нужен только пока на экране
                    // пусто. Как только первые карточки реально отрисованы,
                    // затемнение снимается, а остальные батчи дозагружаются уже
                    // без него — раньше оверлей висел до самого конца загрузки.
                    if (IsLoading && vm.Characters.Count > 0)
                        IsLoading = false;
                }
            }

            if (unassigned.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                bool ungroupedExpanded = expandedState.GetValueOrDefault("ungrouped", true);
                var ungrouped = new CharacterFolderViewModel(new CharacterFolder
                {
                    Id = "ungrouped",
                    Name = CharactersStrings.Folder_Ungrouped,
                    Comment = string.Empty,
                    Color = "#455A64",
                    Order = 999
                })
                {
                    IsExpanded = ungroupedExpanded,
                    IsSelected = "ungrouped" == ActiveFolderId,
                    IsRenaming = false,
                    OnSelectRequested = id => ActiveFolderId = id,
                    IsReadOnlyProvider = () => IsReadOnly,
                    EditCommand = EditCharacterCommand,
                    ConfirmCommand = ConfirmInlineNameCommand,
                    CancelCommand = CancelInlineNameCommand,
                    ToggleCommand = ToggleFolderCommand,
                    RequestDeleteCommand = null
                };

                Folders.Add(ungrouped);

                for (int i = 0; i < unassigned.Count; i += batchSize)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = unassigned.Skip(i).Take(batchSize);
                    foreach (var c in batch)
                    {
                        var relCount = _relationshipService.GetAllForCharacter(c.Id).Count;
                        var isNaming = c.Id == inlineBeingNamedId;
                        var item = new CharacterListItemViewModel(c, relCount, isNaming, _avatarService);
                        BindCharacterItemCallbacks(item);
                        ungrouped.Characters.Add(item);
                    }

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                        () => { }, Avalonia.Threading.DispatcherPriority.Background);

                    // Та же логика для карточек вне групп: первые видимые
                    // карточки снимают оверлей загрузки.
                    if (IsLoading && ungrouped.Characters.Count > 0)
                        IsLoading = false;
                }
            }

            // Список пересобран — вью-модели карточек другие. Выделение надо
            // переложить на новые объекты, иначе боковая панель осталась бы
            // править карточки, которых на экране уже нет.
            PruneSelection();
        }

        private void BindCharacterItemCallbacks(CharacterListItemViewModel item)
        {
            item.OnConfirmName = (id, name) =>
            {
                var character = _characterService.GetById(id);
                if (character is null) return;
                var oldName = character.Name;
                if (oldName == name) return;

                character.Name = name;
                _characterService.Update(character);
                ApplyFilters();

                PushCommand(new RenameCharacterCommand(id, oldName, name, (cid, n) =>
                {
                    var c = _characterService.GetById(cid);
                    if (c is null) return;
                    c.Name = n;
                    _characterService.Update(c);
                    foreach (var folder in Folders)
                    {
                        var vm = folder.Characters.FirstOrDefault(x => x.Id == cid);
                        if (vm is not null) { vm.Name = n; break; }
                    }
                    ApplyFilters();
                }));
            };

            item.OnCancelNewCharacter = (id) =>
            {
                _characterService.Delete(id);
                foreach (var f in _folders) f.CharacterIds.Remove(id);
                RefreshFolderViewModels();
                ApplyFilters();
            };

            item.OnDeleteRequested = (id) => DeleteCharacter(id);

            item.OnColorChanged = (id, color) =>
            {
                var character = _characterService.GetById(id);
                if (character is null) return;
                var oldColor = character.Color;
                if (oldColor == color) return;

                character.Color = color;
                _characterService.Update(character);

                PushCommand(new ChangeCharacterColorCommand(id, character.Name, oldColor, color, (cid, c) =>
                {
                    var ch = _characterService.GetById(cid);
                    if (ch is null) return;
                    ch.Color = c;
                    _characterService.Update(ch);
                    foreach (var folder in Folders)
                    {
                        var vm = folder.Characters.FirstOrDefault(x => x.Id == cid);
                        if (vm is not null) { vm.Color = c; break; }
                    }
                }));
            };

            item.OnAvatarRingChanged = (id, on) =>
            {
                var character = _characterService.GetById(id);
                if (character is null || character.AvatarRing == on) return;
                var wasOn = character.AvatarRing;
                character.AvatarRing = on;
                _characterService.Update(character);

                // Возврат идёт сначала в модель, потом в карточку. Присваивание
                // карточке снова позовёт этот же колбэк, но модель к тому
                // времени уже несёт новое значение, и он выйдет по проверке
                // выше — без второго шага в истории.
                PushCommand(new Actions.ChangeAvatarRingCommand(id, character.Name, wasOn, on, (cid, value) =>
                {
                    var ch = _characterService.GetById(cid);
                    if (ch is null) return;
                    ch.AvatarRing = value;
                    _characterService.Update(ch);
                    foreach (var folder in Folders)
                    {
                        var vm = folder.Characters.FirstOrDefault(x => x.Id == cid);
                        if (vm is not null) { vm.AvatarRing = value; break; }
                    }
                }));
            };

            item.OnGroupBookmarkChanged = (id, on) =>
            {
                var character = _characterService.GetById(id);
                if (character is null || character.GroupBookmark == on) return;
                var wasOn = character.GroupBookmark;
                character.GroupBookmark = on;
                _characterService.Update(character);

                PushCommand(new Actions.ChangeGroupBookmarkCommand(id, character.Name, wasOn, on, (cid, value) =>
                {
                    var ch = _characterService.GetById(cid);
                    if (ch is null) return;
                    ch.GroupBookmark = value;
                    _characterService.Update(ch);
                    foreach (var folder in Folders)
                    {
                        var vm = folder.Characters.FirstOrDefault(x => x.Id == cid);
                        if (vm is not null) { vm.GroupBookmark = value; break; }
                    }
                }));
            };

            item.OnFrameThicknessChanged = (id, v) =>
            {
                var character = _characterService.GetById(id);
                if (character is null || Math.Abs(character.FrameThickness - v) < 0.01) return;
                var wasValue = character.FrameThickness;
                character.FrameThickness = v;
                _characterService.Update(character);

                PushCommand(new Actions.ChangeFrameThicknessCommand(id, character.Name, wasValue, v, (cid, value) =>
                {
                    var ch = _characterService.GetById(cid);
                    if (ch is null) return;
                    ch.FrameThickness = value;
                    _characterService.Update(ch);
                    foreach (var folder in Folders)
                    {
                        var vm = folder.Characters.FirstOrDefault(x => x.Id == cid);
                        if (vm is not null) { vm.FrameThickness = value; break; }
                    }
                }));
            };

            item.OnAvatarStripChanged = (id, on) =>
            {
                var character = _characterService.GetById(id);
                if (character is null || character.AvatarStrip == on) return;
                var wasOn = character.AvatarStrip;
                character.AvatarStrip = on;
                _characterService.Update(character);

                PushCommand(new Actions.ChangeAvatarStripCommand(id, character.Name, wasOn, on, (cid, value) =>
                {
                    var ch = _characterService.GetById(cid);
                    if (ch is null) return;
                    ch.AvatarStrip = value;
                    _characterService.Update(ch);
                    foreach (var folder in Folders)
                    {
                        var vm = folder.Characters.FirstOrDefault(x => x.Id == cid);
                        if (vm is not null) { vm.AvatarStrip = value; break; }
                    }
                }));
            };

            // Метки: панель оформления пишет их полным снимком (список
            // целиком), а не по одному изменению за раз — она либо
            // добавляет метку, либо снимает её, либо правит через тот же
            // редактор, что и вкладка «Основное», и каждое такое действие —
            // один шаг истории.
            item.OnLabelsChanged = (id, labels) =>
            {
                var character = _characterService.GetById(id);
                if (character is null) return;
                var wasLabels = character.Labels?.ToList() ?? new List<Models.CharacterLabel>();
                character.Labels = labels;
                _characterService.Update(character);

                PushCommand(new Actions.ChangeLabelsCommand(id, character.Name, wasLabels, labels, (cid, value) =>
                {
                    var ch = _characterService.GetById(cid);
                    if (ch is null) return;
                    ch.Labels = value;
                    _characterService.Update(ch);
                    foreach (var folder in Folders)
                    {
                        var vm = folder.Characters.FirstOrDefault(x => x.Id == cid);
                        if (vm is not null) { vm.SetLabels(value.ToList()); break; }
                    }
                }));
            };

            item.OnImportanceChanged = (id, level) =>
            {
                var character = _characterService.GetById(id);
                if (character is null || character.ImportanceLevel == level) return;
                var wasLevel = character.ImportanceLevel;
                character.ImportanceLevel = level;
                _characterService.Update(character);

                PushCommand(new Actions.ChangeImportanceCommand(id, character.Name, wasLevel, level, (cid, value) =>
                {
                    var ch = _characterService.GetById(cid);
                    if (ch is null) return;
                    ch.ImportanceLevel = value;
                    _characterService.Update(ch);
                    foreach (var folder in Folders)
                    {
                        var vm = folder.Characters.FirstOrDefault(x => x.Id == cid);
                        if (vm is not null) { vm.ImportanceLevel = value; break; }
                    }
                }));
            };

            item.OnApplyRingToAll = ApplyRingToAllCharacters;

            BindAvatarPickerCallback?.Invoke(item);
        }

        /// <summary>
        /// Применить состояние закладки-ленточки ко всем группам (из окна
        /// настроек карточки). Персист идёт через колбэки самих карточек.
        /// </summary>
        public void ApplyBookmarkToAllGroups(bool on)
        {
            foreach (var folder in Folders)
                foreach (var vm in folder.Characters)
                    if (vm.IsCollective) vm.GroupBookmark = on;
        }

        /// <summary>
        /// Применить толщину рамки ко всем карточкам (из окна настроек карточки).
        /// </summary>
        public void ApplyFrameThicknessToAll(double v)
        {
            foreach (var folder in Folders)
                foreach (var vm in folder.Characters)
                    vm.FrameThickness = v;
        }

        // Применить кольцо аватара ко всем персонажам: снимок прежних значений
        // для отмены, персист через ApplyRingToCharacter, команда в стек Undo.
        // Вызывается и колбэком карточек списка, и из карточки персонажа
        // (редактор цвета через вкладку Basics).
        private void ApplyRingToAllCharacters(bool on)
        {
            var previous = new List<(string id, bool old)>();
            foreach (var folder in Folders)
                foreach (var vm in folder.Characters)
                {
                    var ch = _characterService.GetById(vm.Id);
                    if (ch is null) continue;
                    previous.Add((vm.Id, ch.AvatarRing));
                }
            if (previous.Count == 0) return;

            foreach (var (id, _) in previous) ApplyRingToCharacter(id, on);

            PushCommand(new ApplyAvatarRingToAllCommand(previous, on, ApplyRingToCharacter));
        }

        // Применяет состояние кольца к одному персонажу: модель + персист + VM во всех папках.
        private void ApplyRingToCharacter(string id, bool val)
        {
            var ch = _characterService.GetById(id);
            if (ch is not null && ch.AvatarRing != val)
            {
                ch.AvatarRing = val;
                _characterService.Update(ch);
            }
            foreach (var folder in Folders)
            {
                var vm = folder.Characters.FirstOrDefault(x => x.Id == id);
                if (vm is not null) { vm.AvatarRing = val; break; }
            }
        }

        private void CreateFolder()
        {
            if (IsReadOnly) return;
            var folder = new CharacterFolder
            {
                Id = Guid.NewGuid().ToString(),
                Name = CharactersStrings.Folder_NewName,
                Order = _folders.Count
            };
            _folders.Add(folder);
            ActiveFolderId = folder.Id;

            // Точечно: добавляем VM новой (пустой) папки прямо в показ, без полной
            // пересборки списка. Полная пересборка пересоздаёт все карточки и глючит
            // рендер (папка иногда рисуется дважды — мнимый «дубль»). Вставляем перед
            // «без папки» (Order 999), если она есть, иначе в конец.
            var vm = BuildFolderVm(folder, isExpanded: true, isRenaming: true);
            int insertIdx = Folders.Count;
            for (int i = 0; i < Folders.Count; i++)
                if (Folders[i].FolderId == "ungrouped") { insertIdx = i; break; }
            Folders.Insert(insertIdx, vm);

            PushCommand(new CreateFolderCommand(
                folder.Id,
                id => RestoreFolderById(id),
                id => ConfirmDeleteFolder(id)));
        }

        // Создаёт VM папки с тем же набором команд, что и RefreshFolderViewModels.
        private CharacterFolderViewModel BuildFolderVm(CharacterFolder folder, bool isExpanded, bool isRenaming)
        {
            var captured = folder;
            return new CharacterFolderViewModel(folder)
            {
                IsExpanded = isExpanded,
                IsSelected = folder.Id == ActiveFolderId,
                IsRenaming = isRenaming,
                OnSelectRequested = id => ActiveFolderId = id,
                IsReadOnlyProvider = () => IsReadOnly,
                EditCommand = EditCharacterCommand,
                ConfirmCommand = ConfirmInlineNameCommand,
                CancelCommand = CancelInlineNameCommand,
                ToggleCommand = ToggleFolderCommand,
                RequestDeleteCommand = ReactiveCommand.Create(() =>
                    FolderDeleteRequested?.Invoke(captured.Id, captured.Name))
            };
        }

        private void ConfirmDeleteFolder(string folderId)
        {
            if (IsReadOnly) return;
            var folder = _folders.FirstOrDefault(f => f.Id == folderId);
            if (folder is null) return;
            _folders.Remove(folder);
            if (ActiveFolderId == folderId)
                ActiveFolderId = _folders.FirstOrDefault()?.Id;

            // Точечное удаление БЕЗ полной пересборки списка: убираем только VM этой папки,
            // а её карточки ПЕРЕИСПОЛЬЗУЕМ (не пересоздаём 479 штук) — переносим в «без
            // папки». Остальные папки и их карточки не трогаются → без лага.
            var folderVm = Folders.FirstOrDefault(f => f.FolderId == folderId);
            if (folderVm is null) { RefreshFolderViewModels(); return; }

            var orphans = folderVm.Characters.Where(c => !c.IsPlaceholder).ToList();
            Folders.Remove(folderVm);

            if (orphans.Count > 0)
            {
                var ungrouped = Folders.FirstOrDefault(f => f.FolderId == "ungrouped")
                                ?? CreateUngroupedFolderVm();
                foreach (var item in orphans)
                    ungrouped.Characters.Add(item);
            }
        }

        // Создаёт и добавляет в показ папку «без папки» (для осиротевших карточек).
        private CharacterFolderViewModel CreateUngroupedFolderVm()
        {
            var vm = new CharacterFolderViewModel(new CharacterFolder
            {
                Id = "ungrouped",
                Name = CharactersStrings.Folder_Ungrouped,
                Comment = string.Empty,
                Color = "#455A64",
                Order = 999
            })
            {
                IsExpanded = true,
                IsSelected = "ungrouped" == ActiveFolderId,
                IsRenaming = false,
                OnSelectRequested = id => ActiveFolderId = id,
                IsReadOnlyProvider = () => IsReadOnly,
                EditCommand = EditCharacterCommand,
                ConfirmCommand = ConfirmInlineNameCommand,
                CancelCommand = CancelInlineNameCommand,
                ToggleCommand = ToggleFolderCommand,
                RequestDeleteCommand = null
            };
            Folders.Add(vm);
            return vm;
        }

        private void RestoreFolderById(string folderId)
        {
            if (_folders.Any(f => f.Id == folderId)) return;
            var folder = new CharacterFolder
            {
                Id = folderId,
                Name = CharactersStrings.Folder_NewName,
                Order = _folders.Count
            };
            _folders.Add(folder);
            ActiveFolderId = folder.Id;
            RefreshFolderViewModels(newlyCreatedFolderId: folderId);
        }

        public void MoveCharacterToFolder(string characterId, string folderId)
        {
            if (IsReadOnly) return;
            foreach (var f in _folders) f.CharacterIds.Remove(characterId);
            var target = _folders.FirstOrDefault(f => f.Id == folderId);
            if (target is not null && !target.CharacterIds.Contains(characterId))
                target.CharacterIds.Add(characterId);
            RefreshFolderViewModels();
        }

        public void MoveCharacterBeforeInFolder(string characterId, string targetCharId)
        {
            if (IsReadOnly) return;
            var targetFolder = _folders.FirstOrDefault(f => f.CharacterIds.Contains(targetCharId));
            if (targetFolder is null) return;
            foreach (var f in _folders) f.CharacterIds.Remove(characterId);
            var idx = targetFolder.CharacterIds.IndexOf(targetCharId);
            if (idx < 0) idx = targetFolder.CharacterIds.Count;
            targetFolder.CharacterIds.Insert(idx, characterId);
            RefreshFolderViewModels();
        }

        public List<CharacterFolder> GetFolders()
        {
            return _folders.ToList();
        }

        public void EnsureValidNamesForSave()
        {
            foreach (var folder in Folders)
            {
                if (folder.IsRenaming && string.IsNullOrWhiteSpace(folder.Name))
                {
                    folder.Name = CharactersStrings.Folder_FallbackName;
                }
            }
        }

        public void CommitAllPendingEdits()
        {
            foreach (var folder in Folders)
            {
                if (folder.IsRenaming)
                    folder.ConfirmRenameCommand.Execute().Subscribe();
                if (folder.IsEditingComment)
                    folder.ConfirmCommentCommand.Execute().Subscribe();
            }
            var allChars = Folders.SelectMany(f => f.Characters).ToList();
            foreach (var character in allChars)
            {
                if (character.IsBeingNamed)
                    character.ConfirmNameCommand.Execute().Subscribe();
                else if (character.IsRenaming)
                    character.ConfirmRenameCommand.Execute().Subscribe();
            }
        }


        public void Dispose()
        {
            _disposables.Dispose();

            // Явно очищаем данные персонажей — освобождаем аватарки и
            // строковые данные не дожидаясь GC.
            foreach (var folder in Folders)
            {
                foreach (var ch in folder.Characters)
                    ch.RefreshAvatar();
                folder.Characters.Clear();
            }
            Folders.Clear();
            FilteredCharacters.Clear();
            _folders.Clear();
        }

        public void LoadFolders(List<CharacterFolder> folders)
        {
            _folders.Clear();
            // Дедуп по Id: в сохранёнке могли оказаться папки-дубли с одинаковым Id
            // (порча данных). Дубли роняли ToDictionary и ломали операции с папками.
            var seen = new HashSet<string>();
            foreach (var f in folders)
                if (f is not null && seen.Add(f.Id))
                {
                    NormalizeFolderImportance(f);
                    _folders.Add(f);
                }
        }

        /// <summary>
        /// Проставляет ступень папке, сохранённой версией без ступеней: двум
        /// начальным — по их роли, остальным — третью. Уже заданную ступень
        /// не трогает, поэтому выбранное руками не возвращается к прежнему
        /// при следующем открытии проекта.
        /// </summary>
        private static void NormalizeFolderImportance(CharacterFolder folder)
        {
            if (folder.ImportanceLevel is not null) return;

            folder.ImportanceLevel = folder.Id switch
            {
                "default_main" => CharacterImportanceLevel.Primary,
                "default_secondary" => CharacterImportanceLevel.Secondary,
                _ => CharacterImportanceLevel.Tertiary
            };
        }
    }

    public class CharacterFolderViewModel : ReactiveObject, Controls.IRowHeight
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterFolderViewModel>();
        private bool _isExpanded = true;
        private bool _isRenaming = false;
        private bool _isEditingComment = false;
        private bool _isSelected = false;
        private bool _isDragOver = false;
        private string _name;
        private string _comment;
        private string _color;
        private readonly CharacterFolder _folder;

        public string FolderId { get; }
        public bool IsSystem { get; }
        public bool IsUngrouped { get; }

        public Action<string>? OnSelectRequested { get; set; }
        public ReactiveCommand<string, Unit>? EditCommand { get; set; }
        public ReactiveCommand<string, Unit>? ConfirmCommand { get; set; }
        public ReactiveCommand<string, Unit>? CancelCommand { get; set; }
        public ReactiveCommand<string, Unit>? ToggleCommand { get; set; }
        public ReactiveCommand<Unit, Unit>? RequestDeleteCommand { get; set; }

        public ReactiveCommand<Unit, Unit> SelectOrRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> StartRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleExpandCommand { get; }
        public ReactiveCommand<Unit, Unit> StartEditCommentCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmCommentCommand { get; }

        public ObservableCollection<CharacterListItemViewModel> Characters { get; } = new();

        private static readonly ObservableCollection<CharacterListItemViewModel> _emptyCharacters = new();

        /// <summary>
        /// ItemsSource для списка карточек. Возвращает пустую коллекцию когда папка
        /// свёрнута — UniformGrid не меряет 0 элементов, workmode switch мгновенный.
        /// </summary>
        public ObservableCollection<CharacterListItemViewModel> VisibleCharacters
            => IsExpanded ? Characters : _emptyCharacters;

        // Режим сравнения версий: изменения свойств папки игнорируются.
        // Провайдер задаётся создателем VM (CharactersViewModel).
        public Func<bool>? IsReadOnlyProvider { get; set; }

        private bool IsReadOnly => IsReadOnlyProvider?.Invoke() == true;

        public string Name
        {
            get => _name;
            set
            {
                if (IsReadOnly) return;
                this.RaiseAndSetIfChanged(ref _name, value);
                _folder.Name = value;
            }
        }

        public string Comment
        {
            get => _comment;
            set
            {
                if (IsReadOnly) return;
                this.RaiseAndSetIfChanged(ref _comment, value);
                _folder.Comment = value;
                this.RaisePropertyChanged(nameof(RowHeight));
            }
        }

        /// <summary>
        /// Высота строки-заголовка в боковом списке редактора. Отдаётся
        /// раскладке до создания контрола и одновременно задаёт высоту
        /// в шаблоне — источник истины один, поэтому расчёт скролла и
        /// нарисованная строка не могут разойтись.
        ///
        /// С комментарием строка выше: под ним вторая подпись. В величину
        /// входят и внешние отступы баннера — раскладка считает шаг списка,
        /// а не размер содержимого.
        /// </summary>
        public double RowHeight =>
            string.IsNullOrWhiteSpace(_comment) ? 40 : 52;

        public string Color
        {
            get => _color;
            set
            {
                if (IsReadOnly) return;
                this.RaiseAndSetIfChanged(ref _color, value);
                _folder.Color = value;
            }
        }

        // ── ступень важности папки ────────────────────────────────────────
        // Папка выдаёт свою ступень тем, кто в неё попал: при создании и при
        // переносе. Так «Главные герои» сами проставляют первую ступень, и в
        // каждой карточке её выбирать не нужно.

        /// <summary>
        /// Ступень, которую папка выдаёт своим персонажам. Пустого значения
        /// здесь нет: в модели оно означает только «сохранено старой версией»,
        /// и для показа сводится к третьей ступени.
        /// </summary>
        public CharacterImportanceLevel ImportanceLevel
        {
            get => _folder.ImportanceLevel ?? CharacterImportanceLevel.Tertiary;
            set
            {
                if (IsReadOnly) return;
                if (_folder.ImportanceLevel == value) return;

                _folder.ImportanceLevel = value;
                this.RaisePropertyChanged(nameof(ImportanceLevel));
                this.RaisePropertyChanged(nameof(ImportanceMark));
            }
        }

        /// <summary>Римская цифра ступени для значка у названия папки.</summary>
        public string ImportanceMark => ImportanceLevel switch
        {
            CharacterImportanceLevel.Primary => "I",
            CharacterImportanceLevel.Secondary => "II",
            _ => "III"
        };

        /// <summary>Следующая ступень по кругу: I → II → III → I.</summary>
        public void CycleImportance()
        {
            ImportanceLevel = ImportanceLevel switch
            {
                CharacterImportanceLevel.Primary => CharacterImportanceLevel.Secondary,
                CharacterImportanceLevel.Secondary => CharacterImportanceLevel.Tertiary,
                _ => CharacterImportanceLevel.Primary
            };
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                this.RaiseAndSetIfChanged(ref _isExpanded, value);
                this.RaisePropertyChanged(nameof(VisibleCharacters));
            }
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set => this.RaiseAndSetIfChanged(ref _isRenaming, value);
        }

        public bool IsEditingComment
        {
            get => _isEditingComment;
            set => this.RaiseAndSetIfChanged(ref _isEditingComment, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public bool IsDragOver
        {
            get => _isDragOver;
            set => this.RaiseAndSetIfChanged(ref _isDragOver, value);
        }

        public int Count => Characters.Count;

        // Размеры карточек — копируются из CharactersViewModel при пересчёте.
        // Хранятся здесь потому что $parent[UserControl] не работает внутри ItemsRepeater.
        private double _cardTopHeight = 60.0;
        private double _cardNameHeight = 40.0;
        private double _cardIconSize = 30.0;

        public double CardTopHeight
        {
            get => _cardTopHeight;
            set => this.RaiseAndSetIfChanged(ref _cardTopHeight, value);
        }
        public double CardNameHeight
        {
            get => _cardNameHeight;
            set => this.RaiseAndSetIfChanged(ref _cardNameHeight, value);
        }
        public double CardIconFontSize
        {
            get => _cardIconSize;
            set => this.RaiseAndSetIfChanged(ref _cardIconSize, value);
        }

        public CharacterFolderViewModel(CharacterFolder folder)
        {
            _folder = folder;
            FolderId = folder.Id;
            _name = folder.Name;
            _comment = folder.Comment;
            _color = folder.Color;
            IsSystem = folder.Id.StartsWith("default_") || folder.Id == "ungrouped";
            IsUngrouped = folder.Id == "ungrouped";
            _isRenaming = false;

            Characters.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(Count));

            ToggleExpandCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
            SelectOrRenameCommand = ReactiveCommand.Create(() =>
            {
                if (IsSelected) { if (!IsReadOnly) IsRenaming = true; }
                else OnSelectRequested?.Invoke(FolderId);
            });
            StartRenameCommand = ReactiveCommand.Create(() =>
            {
                if (!IsReadOnly) IsRenaming = true;
            });
            ConfirmRenameCommand = ReactiveCommand.Create(() =>
            {
                if (string.IsNullOrWhiteSpace(Name)) Name = CharactersStrings.Folder_FallbackName;
                IsRenaming = false;
            });
            StartEditCommentCommand = ReactiveCommand.Create(() =>
            {
                if (!IsReadOnly) IsEditingComment = true;
            });
            ConfirmCommentCommand = ReactiveCommand.Create(() =>
            {
                IsEditingComment = false;
            });
        }
    }
}