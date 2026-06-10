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

        public CharactersTrashService Trash => _trash;
        public ICharacterAvatarService? AvatarService => _avatarService;
        public ICharacterService CharacterService => _characterService;

        // Задаётся из CharactersListView code-behind.
        // Вызывается для каждого созданного CharacterListItemViewModel.
        public Action<CharacterListItemViewModel>? BindAvatarPickerCallback { get; set; }

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
        public void Undo() => _undoRedoStack.Undo();
        public void Redo() => _undoRedoStack.Redo();
        public void PushCommand(IUndoableCommand command) => _undoRedoStack.Push(command);

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
        public ObservableCollection<string> AvailableTags { get; } = new();
        public ObservableCollection<string> ActiveTagFilters { get; } = new();

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
                this.RaiseAndSetIfChanged(ref _viewMode, value);
                this.RaisePropertyChanged(nameof(IsListMode));
                this.RaisePropertyChanged(nameof(IsGridMode));
                this.RaisePropertyChanged(nameof(ViewModeIndex));
                RecalculateCardDimensions();
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
        public double CardNameHeight => _cardNameHeight;
        public double CardTotalHeight => _cardTopHeight + _cardNameHeight;
        public double CardIconFontSize => _cardIconSize;

        // Размеры кнопок взаимодействия, пропорциональные ширине карточки (baseline 148).
        public double CardActionIconSize => Math.Max(11, Math.Round(_cardWidth * 11.0 / 148.0));
        public double CardColorButtonSize => Math.Max(18, Math.Round(_cardWidth * 20.0 / 148.0));

        // Количество колонок — используется для расчётов drag.
        public int CardsPerRow => _cardsPerRow;

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
                RaiseCardDimensionProperties();
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

            RaiseCardDimensionProperties();
        }

        private void RaiseCardDimensionProperties()
        {
            this.RaisePropertyChanged(nameof(CardWidth));
            this.RaisePropertyChanged(nameof(CardTopHeight));
            this.RaisePropertyChanged(nameof(CardAvatarSize));
            this.RaisePropertyChanged(nameof(CardNameHeight));
            this.RaisePropertyChanged(nameof(CardTotalHeight));
            this.RaisePropertyChanged(nameof(CardIconFontSize));
            this.RaisePropertyChanged(nameof(CardActionIconSize));
            this.RaisePropertyChanged(nameof(CardColorButtonSize));
            this.RaisePropertyChanged(nameof(CardsPerRow));
            this.RaisePropertyChanged(nameof(CardMinWidth));
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

            TemplatesViewModel = new CharactersTemplatesViewModel(anketaService, ActiveTemplateIds);
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

        private void CreateCharacter()
        {
            var anketas = GetActiveAnketas();
            var character = anketas.Count > 0
                ? _characterService.CreateFromAnketas(CharactersStrings.Character_DefaultName, anketas, randomize: false)
                : _characterService.Create(CharactersStrings.Character_DefaultName);
            AddCharacterToActiveFolderVm(character, isNaming: true);
        }

        private void CreateCharacterRandomized()
        {
            var anketas = GetActiveAnketas();
            var character = anketas.Count > 0
                ? _characterService.CreateFromAnketas(CharactersStrings.Character_DefaultName, anketas, randomize: true)
                : _characterService.Create(CharactersStrings.Character_DefaultName);
            AddCharacterToActiveFolderVm(character, isNaming: true);
        }

        private void CreateCollectiveCharacter()
        {
            var collective = _anketaService.GetById("builtin_collective");
            var anketas = collective is not null
                ? new[] { collective }
                : System.Array.Empty<CharacterAnketa>();
            var character = _characterService.CreateCollective(CharactersStrings.Character_DefaultName, anketas);
            AddCharacterToActiveFolderVm(character, isNaming: true);
        }

        private void AddCharacterToActiveFolderVm(Character character, bool isNaming = false)
        {
            var folderId = ActiveFolderId ?? _folders.FirstOrDefault()?.Id;

            var modelFolder = _folders.FirstOrDefault(f => f.Id == folderId);
            if (modelFolder is not null && !modelFolder.CharacterIds.Contains(character.Id))
                modelFolder.CharacterIds.Add(character.Id);

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
                _characterService, _relationshipService, _anketaService, character, _avatarService);
            IsCardOpen = true;
            MainTabIndex = 1;
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
            RefreshAll();
        }

        public void RestoreFromTrash(string characterId)
        {
            var result = _trash.Restore(characterId);
            if (result is null) return;
            var (character, origFolderId, origIndex) = result.Value;

            var targetFolder = _folders.FirstOrDefault(f => f.Id == origFolderId)
                ?? _folders.FirstOrDefault();
            if (targetFolder is not null)
            {
                var clampedIdx = Math.Min(origIndex, targetFolder.CharacterIds.Count);
                targetFolder.CharacterIds.Insert(clampedIdx, character.Id);
            }

            RefreshAll();
        }

        private void DuplicateCharacter(string characterId)
        {
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
        }

        // Прогрессивная загрузка: папки добавляются пустыми,
        // затем карточки заполняются батчами по 25 штук с
        // Background-приоритетом между батчами — UI не фризится.
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

        private void RefreshTags()
        {
            AvailableTags.Clear();
            foreach (var tag in _characterService.GetAllTags()) AvailableTags.Add(tag);
        }

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
            FilteredCharacters.Clear();
            foreach (var c in all)
            {
                var relCount = _relationshipService.GetAllForCharacter(c.Id).Count;
                FilteredCharacters.Add(new CharacterListItemViewModel(c, relCount, false, _avatarService));
            }
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
            GraphScale = GraphViewModel.Scale
        };

        public void RestoreSessionState(CharactersModuleSession session)
        {
            if (Enum.TryParse<CharactersViewMode>(session.LastViewMode, out var mode))
            {
                if (mode == CharactersViewMode.Grid) mode = CharactersViewMode.GridMedium;
                ViewMode = mode;
            }
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

                    _dragPlaceholder = new CharacterListItemViewModel(
                        new Models.Character
                        {
                            Id = "__placeholder__",
                            Name = string.Empty,
                            Color = item.Color,
                            FallbackIcon = item.FallbackIcon
                        },
                        0, false)
                    { IsPlaceholder = true };

                    var idx = folder.Characters.IndexOf(item);
                    folder.Characters.Remove(item);
                    folder.Characters.Insert(idx, _dragPlaceholder);
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

            if (sourceFolderVm != null && ReferenceEquals(sourceFolderVm, targetFolderVm))
            {
                // Та же папка: Move сохраняет инстансы элементов в ItemsRepeater.
                // Remove+Insert создаёт новый элемент и ломает TranslateTransform анимацию.
                var adjusted = clampedIdx > sourceIdx ? clampedIdx - 1 : clampedIdx;
                if (adjusted != sourceIdx)
                    sourceFolderVm.Characters.Move(sourceIdx, adjusted);
            }
            else
            {
                if (sourceFolderVm != null)
                    sourceFolderVm.Characters.Remove(_dragPlaceholder);
                var clampedCross = Math.Min(clampedIdx, targetFolderVm.Characters.Count);
                targetFolderVm.Characters.Insert(clampedCross, _dragPlaceholder);
            }
        }

        public void CancelDragPreview(string charId)
        {
            if (_previewDragCharId != charId) return;

            if (_dragPlaceholder is not null)
            {
                foreach (var folder in Folders)
                    folder.Characters.Remove(_dragPlaceholder);
                _dragPlaceholder = null;
            }

            if (_dragItem is not null)
            {
                _dragItem.IsDragging = false;
                var origFolderVm = Folders.FirstOrDefault(f => f.FolderId == _previewDragOriginalFolderId);
                if (origFolderVm is not null)
                {
                    var idx = Math.Min(_previewDragOriginalIndex, origFolderVm.Characters.Count);
                    origFolderVm.Characters.Insert(idx, _dragItem);
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
                        folder.Characters.Remove(_dragPlaceholder);
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
                    finalFolderVm.Characters.Insert(clampedVm, item);
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
                _folders.Add(new CharacterFolder
                {
                    Id = "default_main",
                    Name = CharactersStrings.Folder_DefaultMain,
                    Comment = string.Empty,
                    Color = "#E07B39",
                    Order = 0
                });
                _folders.Add(new CharacterFolder
                {
                    Id = "default_secondary",
                    Name = CharactersStrings.Folder_DefaultSecondary,
                    Comment = string.Empty,
                    Color = "#607D8B",
                    Order = 1
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

            var expandedState = Folders.ToDictionary(f => f.FolderId, f => f.IsExpanded);

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
            const int batchSize = 1;

            var allChars = await Task.Run(() => _characterService.GetAll().ToList(), ct);
            var assignedIds = _folders.SelectMany(f => f.CharacterIds).ToHashSet();
            var unassigned = allChars.Where(c => !assignedIds.Contains(c.Id)).ToList();
            var expandedState = Folders.ToDictionary(f => f.FolderId, f => f.IsExpanded);

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
                }
            }
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
            BindAvatarPickerCallback?.Invoke(item);
        }

        private void CreateFolder()
        {
            var folder = new CharacterFolder
            {
                Id = Guid.NewGuid().ToString(),
                Name = CharactersStrings.Folder_NewName,
                Order = _folders.Count
            };
            _folders.Add(folder);
            ActiveFolderId = folder.Id;
            RefreshFolderViewModels(newlyCreatedFolderId: folder.Id);

            PushCommand(new CreateFolderCommand(
                folder.Id,
                id => RestoreFolderById(id),
                id => ConfirmDeleteFolder(id)));
        }

        private void ConfirmDeleteFolder(string folderId)
        {
            var folder = _folders.FirstOrDefault(f => f.Id == folderId);
            if (folder is null) return;
            _folders.Remove(folder);
            if (ActiveFolderId == folderId)
                ActiveFolderId = _folders.FirstOrDefault()?.Id;
            RefreshFolderViewModels();
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
            foreach (var f in _folders) f.CharacterIds.Remove(characterId);
            var target = _folders.FirstOrDefault(f => f.Id == folderId);
            if (target is not null && !target.CharacterIds.Contains(characterId))
                target.CharacterIds.Add(characterId);
            RefreshFolderViewModels();
        }

        public void MoveCharacterBeforeInFolder(string characterId, string targetCharId)
        {
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
            _folders.AddRange(folders);
        }
    }

    public class CharacterFolderViewModel : ReactiveObject
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

        public string Name
        {
            get => _name;
            set { this.RaiseAndSetIfChanged(ref _name, value); _folder.Name = value; }
        }

        public string Comment
        {
            get => _comment;
            set { this.RaiseAndSetIfChanged(ref _comment, value); _folder.Comment = value; }
        }

        public string Color
        {
            get => _color;
            set
            {
                this.RaiseAndSetIfChanged(ref _color, value);
                _folder.Color = value;
            }
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
                if (IsSelected) IsRenaming = true;
                else OnSelectRequested?.Invoke(FolderId);
            });
            StartRenameCommand = ReactiveCommand.Create(() => { IsRenaming = true; });
            ConfirmRenameCommand = ReactiveCommand.Create(() =>
            {
                if (string.IsNullOrWhiteSpace(Name)) Name = CharactersStrings.Folder_FallbackName;
                IsRenaming = false;
            });
            StartEditCommentCommand = ReactiveCommand.Create(() => { IsEditingComment = true; });
            ConfirmCommentCommand = ReactiveCommand.Create(() =>
            {
                IsEditingComment = false;
            });
        }
    }
}