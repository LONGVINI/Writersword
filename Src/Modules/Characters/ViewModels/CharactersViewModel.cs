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
    /// <summary>
    /// Главный ViewModel модуля персонажей.
    /// Три вкладки: 0=Персонажи, 1=Связи, 2=Шаблоны.
    /// </summary>
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

        // Фильтры по важности
        public ReactiveCommand<Unit, Unit> FilterPrimaryCommand { get; }
        public ReactiveCommand<Unit, Unit> FilterSecondaryCommand { get; }
        public ReactiveCommand<Unit, Unit> FilterTertiaryCommand { get; }
        public ReactiveCommand<Unit, Unit> FilterCollectiveCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearImportanceFilterCommand { get; }

        // Папки
        public ReactiveCommand<Unit, Unit> CreateFolderCommand { get; }
        public ReactiveCommand<string, Unit> DeleteFolderCommand { get; }
        public ReactiveCommand<string, Unit> ToggleFolderCommand { get; }

        // ── Экраны ────────────────────────────────────────────────────────

        public CharactersTemplatesViewModel TemplatesViewModel { get; }
        public CharactersGraphViewModel GraphViewModel { get; }

        // ── Онбординг ─────────────────────────────────────────────────────

        private bool _showOnboarding;
        public bool ShowOnboarding
        {
            get => _showOnboarding;
            set => this.RaiseAndSetIfChanged(ref _showOnboarding, value);
        }

        public CharactersOnboardingViewModel OnboardingViewModel { get; }

        // ── Активные шаблоны ──────────────────────────────────────────────

        public ObservableCollection<string> ActiveTemplateIds { get; } = new();

        // ── Список персонажей ─────────────────────────────────────────────

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

        // ── Расширенные фильтры ───────────────────────────────────────────

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

        // ── Карточка персонажа ────────────────────────────────────────────

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

        // ── Команды ───────────────────────────────────────────────────────

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

        // ── Активная папка ────────────────────────────────────────────────

        private string? _activeFolderId;
        public string? ActiveFolderId
        {
            get => _activeFolderId;
            set => this.RaiseAndSetIfChanged(ref _activeFolderId, value);
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
            DeleteFolderCommand = ReactiveCommand.Create<string>(DeleteFolder);
            ToggleFolderCommand = ReactiveCommand.Create<string>(id =>
            {
                var folder = Folders.FirstOrDefault(f => f.FolderId == id);
                if (folder != null) folder.IsExpanded = !folder.IsExpanded;
            });

            RefreshAll();
            EnsureDefaultFolders();
        }

        // ── Онбординг ─────────────────────────────────────────────────────

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

            // Уведомление — 5 секунд что можно перепройти через вкладку Шаблоны
            _logger.Information("Onboarding dismissed — can restart via Templates tab");
        }

        // ── Создание персонажей ───────────────────────────────────────────

        private void CreateCharacter()
        {
            var anketas = GetActiveAnketas();
            var character = anketas.Count > 0
                ? _characterService.CreateFromAnketas(CharactersStrings.Character_DefaultName, anketas, randomize: false)
                : _characterService.Create(CharactersStrings.Character_DefaultName);

            // Добавляем в активную папку (или первую)
            AddCharacterToActiveFolder(character.Id);

            // Обновляем список — показываем в inline-режиме (ожидание ввода имени)
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

        // ── Операции с персонажами ────────────────────────────────────────

        /// <summary>
        /// Просто выделяет персонажа в списке — без перехода на вкладку редактирования.
        /// </summary>
        private void SelectCharacter(string characterId)
        {
            // Снимаем выделение со всех
            foreach (var folder in Folders)
                foreach (var item in folder.Characters)
                    item.IsSelected = item.Id == characterId;

            foreach (var item in FilteredCharacters)
                item.IsSelected = item.Id == characterId;

            _logger.Debug("Character selected: {Id}", characterId);
        }

        /// <summary>
        /// Открывает карточку персонажа в режиме редактирования и переключает на вкладку 1.
        /// Вызывается только кнопкой-карандашом.
        /// </summary>
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

        // Публичная версия для совместимости (используется в Module)
        public void OpenCharacter(string characterId) => EditCharacter(characterId);

        /// <summary>Подтвердить inline-ввод имени персонажа.</summary>
        private void ConfirmInlineName(string characterId)
        {
            var character = _characterService.GetById(characterId);
            if (character == null) return;

            // Ищем введённое имя в folder VM
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

        /// <summary>Отменить inline-создание — удаляет персонажа.</summary>
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

        // ── Фильтры и список ─────────────────────────────────────────────

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

        // ── Сессионное состояние ──────────────────────────────────────────

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

        private void RefreshFolderViewModels(string? inlineBeingNamedId = null)
        {
            var allChars = _characterService.GetAll().ToList();
            var assignedIds = _folders.SelectMany(f => f.CharacterIds).ToHashSet();
            var unassigned = allChars.Where(c => !assignedIds.Contains(c.Id)).ToList();

            _logger.Debug("RefreshFolderViewModels: {FolderCount} folders, {CharCount} characters, {UnassignedCount} unassigned",
                _folders.Count, allChars.Count, unassigned.Count);

            Folders.Clear();
            foreach (var folder in _folders.OrderBy(f => f.Order))
            {
                var vm = new CharacterFolderViewModel(folder)
                {
                    EditCommand = EditCharacterCommand,
                    ConfirmCommand = ConfirmInlineNameCommand,
                    CancelCommand = CancelInlineNameCommand,
                    ToggleCommand = ToggleFolderCommand
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
                    EditCommand = EditCharacterCommand,
                    ConfirmCommand = ConfirmInlineNameCommand,
                    CancelCommand = CancelInlineNameCommand,
                    ToggleCommand = ToggleFolderCommand
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
            RefreshFolderViewModels();
            _logger.Debug("Folder created: {Id}", folder.Id);
        }

        private void DeleteFolder(string folderId)
        {
            _folders.RemoveAll(f => f.Id == folderId);
            RefreshFolderViewModels();
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

        // Используется при аутосейве — синхронизирует данные в модель без закрытия TextBox.
        // Name и Comment папок актуальны в _folder через сеттеры (two-way binding),
        // поэтому нужна только проверка пустого имени при переименовании.
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
        private string _name;
        private string _comment;

        private readonly CharacterFolder _folder;

        public string FolderId { get; }
        public string Color { get; }
        public bool IsSystem { get; }

        // Команды — передаются снаружи из CharactersViewModel
        public ReactiveCommand<string, Unit>? EditCommand { get; set; }
        public ReactiveCommand<string, Unit>? ConfirmCommand { get; set; }
        public ReactiveCommand<string, Unit>? CancelCommand { get; set; }
        public ReactiveCommand<string, Unit>? ToggleCommand { get; set; }

        // Локальные команды папки
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

        public int Count => Characters.Count;

        public CharacterFolderViewModel(CharacterFolder folder)
        {
            _folder = folder;
            FolderId = folder.Id;
            _name = folder.Name;
            _comment = folder.Comment;
            Color = folder.Color;
            IsSystem = folder.Id.StartsWith("default_") || folder.Id == "ungrouped";
            _isRenaming = folder.Name == CharactersStrings.Folder_NewName;

            ToggleExpandCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
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