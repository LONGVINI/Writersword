using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Src.Modules.Characters.Resources;
using Writersword.Modules.Characters.ViewModels.Onboarding;
using Writersword.Modules.Characters.ViewModels.Templates;

namespace Writersword.Modules.Characters.ViewModels
{
    public class CharactersViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersViewModel>();

        private readonly ICharacterService _characterService;
        private readonly IRelationshipService _relationshipService;
        private readonly ICharacterAnketaService _anketaService;

        // ── Вкладки модуля ────────────────────────────────────────────────

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

        private CharactersViewMode _viewMode = CharactersViewMode.Grid;
        public CharactersViewMode ViewMode
        {
            get => _viewMode;
            set { this.RaiseAndSetIfChanged(ref _viewMode, value); this.RaisePropertyChanged(nameof(IsListMode)); this.RaisePropertyChanged(nameof(IsGridMode)); }
        }

        public bool IsListMode => ViewMode == CharactersViewMode.List;
        public bool IsGridMode => ViewMode == CharactersViewMode.Grid;

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

        public CharactersViewModel(
            ICharacterService characterService,
            IRelationshipService relationshipService,
            ICharacterAnketaService anketaService)
        {
            _characterService = characterService;
            _relationshipService = relationshipService;
            _anketaService = anketaService;

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

            GoToCharactersCommand.ThrownExceptions.Subscribe(ex => _logger.Error(ex, "GoToCharacters failed"));
            GoToEditCommand.ThrownExceptions.Subscribe(ex => _logger.Error(ex, "GoToEdit failed"));
            GoToRelationshipsCommand.ThrownExceptions.Subscribe(ex => _logger.Error(ex, "GoToRelationships failed"));
            GoToTemplatesCommand.ThrownExceptions.Subscribe(ex => _logger.Error(ex, "GoToTemplates failed"));

            FilterPrimaryCommand = ReactiveCommand.Create(() =>
            {
                FilterImportance = FilterImportance == CharacterImportanceLevel.Primary ? (CharacterImportanceLevel?)null : CharacterImportanceLevel.Primary;
            });
            FilterSecondaryCommand = ReactiveCommand.Create(() =>
            {
                FilterImportance = FilterImportance == CharacterImportanceLevel.Secondary ? (CharacterImportanceLevel?)null : CharacterImportanceLevel.Secondary;
            });
            FilterTertiaryCommand = ReactiveCommand.Create(() =>
            {
                FilterImportance = FilterImportance == CharacterImportanceLevel.Tertiary ? (CharacterImportanceLevel?)null : CharacterImportanceLevel.Tertiary;
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
                if (folder != null)
                    FolderDeleteRequested?.Invoke(id, folder.Name);
            });
            ConfirmDeleteFolderCommand = ReactiveCommand.Create<string>(ConfirmDeleteFolder);

            ToggleFolderCommand = ReactiveCommand.Create<string>(id =>
            {
                var folder = Folders.FirstOrDefault(f => f.FolderId == id);
                if (folder != null) folder.IsExpanded = !folder.IsExpanded;
            });

            RefreshAll();
            EnsureDefaultFolders();
        }

        public void InitializeFirstLaunch()
        {
            ShowOnboarding = true;
            _logger.Debug("First launch: showing onboarding");
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
                _logger.Debug("Onboarding completed, applied {Count} templates", ActiveTemplateIds.Count);
            }

            _logger.Information("Onboarding dismissed — can restart via Templates tab");
        }

        private void CreateCharacter()
        {
            var anketas = GetActiveAnketas();
            var character = anketas.Count > 0
                ? _characterService.CreateFromAnketas(CharactersStrings.Character_DefaultName, anketas, randomize: false)
                : _characterService.Create(CharactersStrings.Character_DefaultName);

            AddCharacterToActiveFolder(character.Id);
            RefreshFolderViewModels(inlineBeingNamedId: character.Id);
            ApplyFilters();
            _logger.Debug("Character created (awaiting name): {Id}", character.Id);
        }

        private void CreateCharacterRandomized()
        {
            var anketas = GetActiveAnketas();
            var character = anketas.Count > 0
                ? _characterService.CreateFromAnketas(CharactersStrings.Character_DefaultName, anketas, randomize: true)
                : _characterService.Create(CharactersStrings.Character_DefaultName);

            AddCharacterToActiveFolder(character.Id);
            RefreshFolderViewModels(inlineBeingNamedId: character.Id);
            ApplyFilters();
        }

        private void CreateCollectiveCharacter()
        {
            var collective = _anketaService.GetById("builtin_collective");
            var anketas = collective != null
                ? new[] { collective }
                : System.Array.Empty<CharacterAnketa>();
            var character = _characterService.CreateCollective(CharactersStrings.Character_DefaultName, anketas);
            AddCharacterToActiveFolder(character.Id);
            RefreshFolderViewModels(inlineBeingNamedId: character.Id);
            ApplyFilters();
        }

        private void AddCharacterToActiveFolder(string characterId)
        {
            var folderId = ActiveFolderId ?? _folders.FirstOrDefault()?.Id;
            if (folderId != null)
            {
                var folder = _folders.FirstOrDefault(f => f.Id == folderId);
                if (folder != null && !folder.CharacterIds.Contains(characterId))
                    folder.CharacterIds.Add(characterId);
            }
        }

        private System.Collections.Generic.List<CharacterAnketa> GetActiveAnketas() =>
            ActiveTemplateIds
                .Select(id => _anketaService.GetById(id))
                .Where(a => a != null)
                .Cast<CharacterAnketa>()
                .ToList();

        private void SelectCharacter(string characterId)
        {
            foreach (var folder in Folders)
                foreach (var item in folder.Characters)
                    item.IsSelected = item.Id == characterId;

            foreach (var item in FilteredCharacters)
                item.IsSelected = item.Id == characterId;

            _logger.Debug("Character selected: {Id}", characterId);
        }

        public void EditCharacter(string characterId)
        {
            var character = _characterService.GetById(characterId);
            if (character == null) return;

            SelectedCharacterCard = new CharacterCardViewModel(
                _characterService, _relationshipService, _anketaService, character);
            IsCardOpen = true;
            MainTabIndex = 1;
            _logger.Debug("Character opened for editing: {Id}", characterId);
        }

        public void OpenCharacter(string characterId) => EditCharacter(characterId);

        private void ConfirmInlineName(string characterId)
        {
            var character = _characterService.GetById(characterId);
            if (character == null) return;

            string? newName = null;
            foreach (var folder in Folders)
            {
                var item = folder.Characters.FirstOrDefault(c => c.Id == characterId);
                if (item != null)
                {
                    newName = string.IsNullOrWhiteSpace(item.InlineName)
                        ? CharactersStrings.Character_DefaultName
                        : item.InlineName.Trim();
                    item.IsBeingNamed = false;
                    break;
                }
            }

            if (newName != null)
            {
                character.Name = newName;
                _characterService.Update(character);
            }

            RefreshFolderViewModels();
            ApplyFilters();
            _logger.Debug("Inline name confirmed: {Id} = '{Name}'", characterId, newName);
        }

        private void CancelInlineName(string characterId)
        {
            _characterService.Delete(characterId);
            foreach (var f in _folders) f.CharacterIds.Remove(characterId);
            RefreshFolderViewModels();
            ApplyFilters();
            _logger.Debug("Inline name cancelled, character deleted: {Id}", characterId);
        }

        private void DeleteCharacter(string characterId)
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

        private void DuplicateCharacter(string characterId)
        {
            var copy = _characterService.Duplicate(characterId);
            RefreshAll();
            OpenCharacter(copy.Id);
        }

        public void RefreshAll()
        {
            _logger.Debug("RefreshAll called, folders in model: {Count}", _folders.Count);
            RefreshTags();
            ApplyFilters();
            RefreshFolderViewModels();
            GraphViewModel.Refresh();
        }

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
                FilteredCharacters.Add(new CharacterListItemViewModel(c, relCount));
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
                ViewMode = mode;

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

        // ── Папки ─────────────────────────────────────────────────────────

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
                _logger.Debug("Default folders created");
            }
            RefreshFolderViewModels();
            if (ActiveFolderId == null)
                ActiveFolderId = _folders.FirstOrDefault()?.Id;
        }

        // newlyCreatedFolderId — ID только что созданной папки.
        // Передаётся из CreateFolder чтобы ВМ этой папки сразу открылся в режиме переименования.
        // Все остальные папки IsRenaming = false независимо от их имени.

        private void RefreshFolderViewModels(string? inlineBeingNamedId = null, string? newlyCreatedFolderId = null)
        {
            var allChars = _characterService.GetAll().ToList();
            var assignedIds = _folders.SelectMany(f => f.CharacterIds).ToHashSet();
            var unassigned = allChars.Where(c => !assignedIds.Contains(c.Id)).ToList();

            _logger.Debug("RefreshFolderViewModels: {FolderCount} folders, {CharCount} characters, {UnassignedCount} unassigned",
                _folders.Count, allChars.Count, unassigned.Count);

            Folders.Clear();
            foreach (var folder in _folders.OrderBy(f => f.Order))
            {
                var capturedFolder = folder;
                var vm = new CharacterFolderViewModel(folder)
                {
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
                    if (c != null)
                    {
                        var relCount = _relationshipService.GetAllForCharacter(c.Id).Count;
                        var isNaming = c.Id == inlineBeingNamedId;
                        vm.Characters.Add(new CharacterListItemViewModel(c, relCount, isNaming));
                    }
                }
                Folders.Add(vm);
            }

            if (unassigned.Count > 0)
            {
                var ungrouped = new CharacterFolderViewModel(new CharacterFolder
                {
                    Id = "ungrouped",
                    Name = CharactersStrings.Folder_Ungrouped,
                    Comment = string.Empty,
                    Color = "#455A64",
                    Order = 999
                })
                {
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
                    ungrouped.Characters.Add(new CharacterListItemViewModel(c, relCount, isNaming));
                }
                Folders.Add(ungrouped);
            }
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
            // Передаём ID новой папки — только она откроется в режиме переименования.
            RefreshFolderViewModels(newlyCreatedFolderId: folder.Id);
            _logger.Debug("Folder created: {Id}", folder.Id);
        }

        private void ConfirmDeleteFolder(string folderId)
        {
            var folder = _folders.FirstOrDefault(f => f.Id == folderId);
            if (folder == null) return;

            // Персонажей из удаляемой папки НЕ перемещаем — они становятся unassigned
            // и автоматически отображаются в папке "Нераспределённые".
            _logger.Debug("Folder {Id}: {Count} characters become unassigned", folderId, folder.CharacterIds.Count);

            _folders.Remove(folder);

            if (ActiveFolderId == folderId)
                ActiveFolderId = _folders.FirstOrDefault()?.Id;

            RefreshFolderViewModels();
            _logger.Debug("Folder deleted: {Id}", folderId);
        }

        public void MoveCharacterToFolder(string characterId, string folderId)
        {
            foreach (var f in _folders)
                f.CharacterIds.Remove(characterId);

            var target = _folders.FirstOrDefault(f => f.Id == folderId);
            if (target != null && !target.CharacterIds.Contains(characterId))
                target.CharacterIds.Add(characterId);

            RefreshFolderViewModels();
        }

        public List<CharacterFolder> GetFolders()
        {
            _logger.Debug("GetFolders: returning {Count} folders: {Names}",
                _folders.Count,
                string.Join(", ", _folders.Select(f => $"'{f.Name}'[{f.CharacterIds.Count}]")));
            return _folders.ToList();
        }

        public void EnsureValidNamesForSave()
        {
            _logger.Debug("EnsureValidNamesForSave: checking {Count} folders", Folders.Count);
            foreach (var folder in Folders)
            {
                if (folder.IsRenaming && string.IsNullOrWhiteSpace(folder.Name))
                {
                    _logger.Debug("EnsureValidNamesForSave: fixed empty name for folder {Id}", folder.FolderId);
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

            var beingNamed = Folders
                .SelectMany(f => f.Characters)
                .Where(c => c.IsBeingNamed)
                .ToList();

            foreach (var character in beingNamed)
                ConfirmInlineNameCommand.Execute(character.Id).Subscribe();
        }

        public void LoadFolders(List<CharacterFolder> folders)
        {
            _logger.Debug("LoadFolders: loading {Count} folders: {Names}",
                folders.Count,
                string.Join(", ", folders.Select(f => $"'{f.Name}'[{f.CharacterIds.Count}]")));
            _folders.Clear();
            _folders.AddRange(folders);
        }
    }

    // ── Folder ViewModel ─────────────────────────────────────────────────

    public class CharacterFolderViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterFolderViewModel>();

        private bool _isExpanded = true;
        private bool _isRenaming = false;
        private bool _isEditingComment = false;
        private bool _isSelected = false;
        private string _name;
        private string _comment;
        private string _color;

        private readonly CharacterFolder _folder;

        public string FolderId { get; }
        public bool IsSystem { get; }

        // IsUngrouped — папка "Нераспределённые", создаётся автоматически.
        // Не имеет кнопки удаления, исчезает когда в ней не остаётся персонажей.
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

        public string Name
        {
            get => _name;
            set
            {
                this.RaiseAndSetIfChanged(ref _name, value);
                _folder.Name = value;
            }
        }

        public string Comment
        {
            get => _comment;
            set
            {
                this.RaiseAndSetIfChanged(ref _comment, value);
                _folder.Comment = value;
            }
        }

        public string Color
        {
            get => _color;
            set
            {
                this.RaiseAndSetIfChanged(ref _color, value);
                _folder.Color = value;
                _logger.Debug("Folder color changed: {Id} = {Color}", FolderId, value);
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
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

        public int Count => Characters.Count;

        public CharacterFolderViewModel(CharacterFolder folder)
        {
            _folder = folder;
            FolderId = folder.Id;
            _name = folder.Name;
            _comment = folder.Comment;
            _color = folder.Color;
            IsSystem = folder.Id.StartsWith("default_") || folder.Id == "ungrouped";
            IsUngrouped = folder.Id == "ungrouped";

            // IsRenaming устанавливается снаружи через свойство, а не через сравнение имени.
            // Сравнение имени ненадёжно: если папка сохранена с именем совпадающим с Folder_NewName,
            // она бы ошибочно открывалась в режиме переименования при каждой загрузке.
            _isRenaming = false;

            ToggleExpandCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });

            SelectOrRenameCommand = ReactiveCommand.Create(() =>
            {
                if (IsSelected)
                    IsRenaming = true;
                else
                    OnSelectRequested?.Invoke(FolderId);
            });

            StartRenameCommand = ReactiveCommand.Create(() => { IsRenaming = true; });
            ConfirmRenameCommand = ReactiveCommand.Create(() =>
            {
                if (string.IsNullOrWhiteSpace(Name)) Name = CharactersStrings.Folder_FallbackName;
                IsRenaming = false;
                _logger.Debug("Folder renamed: {Id} = '{Name}'", FolderId, Name);
            });
            StartEditCommentCommand = ReactiveCommand.Create(() => { IsEditingComment = true; });
            ConfirmCommentCommand = ReactiveCommand.Create(() =>
            {
                IsEditingComment = false;
                _logger.Debug("Folder comment saved: {Id} = '{Comment}'", FolderId, Comment);
            });
        }
    }
}