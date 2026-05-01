using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Models.Settings;
using Writersword.Core.Services;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Src.Modules.Characters.Resources;
using Writersword.Modules.Characters.Services;
using Writersword.Modules.Characters.ViewModels;
using Writersword.Modules.Characters.Views;
using Writersword.Modules.Common;


namespace Writersword.Modules.Characters
{
    public class CharactersModule : BaseModule, IHotKeyProvider
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersModule>();

        private CharactersViewModel? _viewModel;
        private readonly ICharacterService _characterService;
        private readonly IRelationshipService _relationshipService;
        private readonly ICharacterAnketaService _anketaService;

        // Хранится для последующей отписки в Dispose().
        private Action? _onLanguageChanged;

        public CharactersModule() : base()
        {
            _relationshipService = new RelationshipService();
            _anketaService = new CharacterAnketaService();
            _characterService = new CharacterService(_relationshipService, _anketaService);
        }

        public override string moduleType => "Characters";
        public override string Title { get; set; } = CharactersStrings.DisplayName;
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata => new CharactersMetadata();

        public override void Initialize()
        {
            if (_characterService is CharacterService cs) cs.SetContext(Context);
            var trashService = new CharactersTrashService(_characterService);
            _viewModel = new CharactersViewModel(_characterService, _relationshipService, _anketaService, trashService);

            // Синхронизируем культуру модуля с текущим языком приложения.
            // CharactersStrings имеет собственный статический Culture, который
            // LocalizationService не трогает — поэтому выставляем его здесь
            // и подписываемся на смену языка, чтобы обновлять при Settings.
            var locService = App.Services.GetRequiredService<ILocalizationService>();
            CharactersStrings.Culture = new CultureInfo(locService.CurrentLanguage);

            _onLanguageChanged = () =>
                CharactersStrings.Culture = new CultureInfo(locService.CurrentLanguage);

            locService.LanguageChanged += _onLanguageChanged;

            base.Initialize();
            _logger.Debug("Initialized");
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            if (_characterService is CharacterService cs) cs.SetContext(context);
        }

        public override Control? CreateView() =>
            new CharactersModuleView { DataContext = _viewModel };

        // ── Горячие клавиши ───────────────────────────────────────────────

        public IReadOnlyList<HotKey> GetHotKeys() => new CharactersMetadata().GetHotKeys();

        public void ExecuteHotKey(string id)
        {
            if (_viewModel == null) return;

            switch (id)
            {
                case "characters.open_characters":
                    _viewModel.SwitchMainTabCommand.Execute("0").Subscribe(); break;
                case "characters.open_edit":
                    _viewModel.SwitchMainTabCommand.Execute("1").Subscribe(); break;
                case "characters.open_relationships":
                    _viewModel.SwitchMainTabCommand.Execute("2").Subscribe(); break;
                case "characters.open_templates":
                    _viewModel.SwitchMainTabCommand.Execute("3").Subscribe(); break;
                case "characters.create":
                    _viewModel.CreateCharacterCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.create_randomized":
                    _viewModel.CreateCharacterRandomizedCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.create_collective":
                    _viewModel.CreateCollectiveCharacterCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.save":
                    _viewModel.SelectedCharacterCard?.SaveCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.close_card":
                    _viewModel.CloseCardCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.delete":
                    if (_viewModel.SelectedCharacterCard != null)
                        _viewModel.DeleteCharacterCommand.Execute(_viewModel.SelectedCharacterCard.CharacterId).Subscribe(); break;
                case "characters.duplicate":
                    if (_viewModel.SelectedCharacterCard != null)
                        _viewModel.DuplicateCharacterCommand.Execute(_viewModel.SelectedCharacterCard.CharacterId).Subscribe(); break;
                case "characters.focus_search":
                    _viewModel.FocusSearchCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.clear_filters":
                    _viewModel.ClearFiltersCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.view_list":
                    _viewModel.SwitchViewModeCommand.Execute(CharactersViewMode.List).Subscribe(); break;
                case "characters.view_grid":
                    _viewModel.SwitchViewModeCommand.Execute(CharactersViewMode.Grid).Subscribe(); break;
                case "characters.tab_next":
                    if (_viewModel.SelectedCharacterCard != null)
                    {
                        var c = _viewModel.SelectedCharacterCard;
                        c.SelectedTabIndex = (c.SelectedTabIndex + 1) % CharacterCardViewModel.TabCount;
                    }
                    break;
                case "characters.tab_prev":
                    if (_viewModel.SelectedCharacterCard != null)
                    {
                        var c = _viewModel.SelectedCharacterCard;
                        c.SelectedTabIndex = (c.SelectedTabIndex + CharacterCardViewModel.TabCount - 1) % CharacterCardViewModel.TabCount;
                    }
                    break;
                case "characters.tab_basics":
                    if (_viewModel.SelectedCharacterCard != null) _viewModel.SelectedCharacterCard.SelectedTabIndex = 0; break;
                case "characters.tab_parameters":
                    if (_viewModel.SelectedCharacterCard != null) _viewModel.SelectedCharacterCard.SelectedTabIndex = 1; break;
                case "characters.tab_relationships_card":
                    if (_viewModel.SelectedCharacterCard != null) _viewModel.SelectedCharacterCard.SelectedTabIndex = 2; break;
                case "characters.tab_contexts":
                    if (_viewModel.SelectedCharacterCard != null) _viewModel.SelectedCharacterCard.SelectedTabIndex = 3; break;
                case "characters.tab_notes":
                    if (_viewModel.SelectedCharacterCard != null) _viewModel.SelectedCharacterCard.SelectedTabIndex = 4; break;
                case "characters.tab_personal_timeline":
                    if (_viewModel.SelectedCharacterCard != null) _viewModel.SelectedCharacterCard.SelectedTabIndex = 5; break;
                case "characters.tab_history":
                    if (_viewModel.SelectedCharacterCard != null) _viewModel.SelectedCharacterCard.SelectedTabIndex = 6; break;
                case "characters.add_parameter_numeric":
                    _viewModel.SelectedCharacterCard?.ParametersTab.AddNumericParameterCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.add_parameter_text":
                    _viewModel.SelectedCharacterCard?.ParametersTab.AddTextParameterCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.add_parameter_statelist":
                    _viewModel.SelectedCharacterCard?.ParametersTab.AddStateListParameterCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.add_parameter_boolean":
                    _viewModel.SelectedCharacterCard?.ParametersTab.AddBooleanParameterCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.add_context":
                    _viewModel.SelectedCharacterCard?.ContextsTab.AddContextCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.add_relationship":
                    _viewModel.SelectedCharacterCard?.RelationshipsTab.AddRelationshipCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.add_note":
                    _viewModel.SelectedCharacterCard?.NotesTab.AddNoteCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.add_timeline_event":
                    _viewModel.SelectedCharacterCard?.PersonalTimelineTab.AddEventCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.randomize_parameters":
                    _viewModel.SelectedCharacterCard?.ParametersTab.RandomizeAllCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.graph_reset_view":
                    _viewModel.GraphViewModel.ResetViewCommand.Execute(Unit.Default).Subscribe(); break;
                case "characters.graph_clear_focus":
                    _viewModel.GraphViewModel.ClearFocusCommand.Execute(Unit.Default).Subscribe(); break;
                default:
                    _logger.Warning("Unknown hotkey: {Id}", id); break;
            }
        }

        // ── Сериализация ──────────────────────────────────────────────────

        private CharactersModuleData? _moduleData;

        public override object? GetCustomData()
        {
            // CommitAllPendingEdits здесь не вызываем — он ставит IsRenaming/IsEditingComment = false,
            // что закрывает TextBox прямо во время ввода пользователя (аутосейв срабатывает в фоне).
            // Name и Comment уже актуальны в модели через two-way binding.
            // Проверяем только пустые имена при переименовании.
            _viewModel?.EnsureValidNamesForSave();
            var data = _characterService.GetModuleData();
            data.Relationships = _relationshipService.GetAll().ToList();
            data.ActiveTemplateIds = _viewModel?.ActiveTemplateIds.ToList() ?? new List<string>();
            data.Folders = _viewModel?.GetFolders() ?? new List<CharacterFolder>();
            data.IsFirstLaunch = false;
            if (_anketaService is CharacterAnketaService as_)
                data.CustomAnketas = as_.GetCustom().ToList();
            _logger.Debug("GetCustomData: {CharCount} characters, {FolderCount} folders, {RelCount} relationships",
                data.Characters.Count, data.Folders.Count, data.Relationships.Count);
            return data;
        }

        public override void SetCustomData(object? data)
        {
            if (data == null) return;

            try
            {
                CharactersModuleData? moduleData = data is CharactersModuleData d ? d
                    : data is string s
                        ? JsonConvert.DeserializeObject<CharactersModuleData>(s)
                        : JsonConvert.DeserializeObject<CharactersModuleData>(JsonConvert.SerializeObject(data));

                if (moduleData == null) return;

                _moduleData = moduleData;
                bool isFirst = moduleData.IsFirstLaunch;

                _logger.Debug("SetCustomData: {CharCount} characters, {FolderCount} folders, {RelCount} relationships, isFirst={IsFirst}",
                    moduleData.Characters?.Count ?? 0,
                    moduleData.Folders?.Count ?? 0,
                    moduleData.Relationships?.Count ?? 0,
                    isFirst);

                if (_relationshipService is RelationshipService rs)
                    rs.LoadRelationships(moduleData.Relationships ?? new List<CharacterRelationship>());

                _characterService.LoadModuleData(moduleData);

                if (_anketaService is CharacterAnketaService as_)
                    as_.LoadCustomAnketas(moduleData.CustomAnketas ?? new List<CharacterAnketa>());

                if (_viewModel != null)
                {
                    if (moduleData.Folders != null && moduleData.Folders.Count > 0)
                    {
                        _viewModel.LoadFolders(moduleData.Folders);
                        _logger.Debug("Folders loaded into ViewModel: {Count}", moduleData.Folders.Count);
                    }
                    else
                    {
                        _logger.Warning("SetCustomData: no folders in module data, default folders will be used");
                    }

                    _viewModel.ActiveTemplateIds.Clear();
                    foreach (var id in moduleData.ActiveTemplateIds ?? new List<string>())
                        _viewModel.ActiveTemplateIds.Add(id);

                    _viewModel.RefreshAll();
                    _logger.Debug("ViewModel refreshed after SetCustomData");

                    if (isFirst)
                        _viewModel.InitializeFirstLaunch();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "SetCustomData failed");
            }
        }

        public override object? GetSessionData() => _viewModel?.GetSessionState();

        public override void SetSessionData(object? data)
        {
            if (data == null || _viewModel == null) return;
            try
            {
                var session = data is CharactersModuleSession s ? s
                    : JsonConvert.DeserializeObject<CharactersModuleSession>(JsonConvert.SerializeObject(data));
                if (session != null) _viewModel.RestoreSessionState(session);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SetSessionData failed");
            }
        }

        public override void Dispose()
        {
            // Отписываемся от смены языка чтобы не удерживать ссылку на модуль
            // через синглтон LocalizationService после уничтожения модуля.
            if (_onLanguageChanged != null)
            {
                var locService = App.Services.GetService<ILocalizationService>();
                if (locService != null)
                    locService.LanguageChanged -= _onLanguageChanged;
                _onLanguageChanged = null;
            }

            base.Dispose();
            _logger.Debug("Disposed");
        }
    }

    internal class CharactersMetadata : IModuleMetadata, IHotKeyDescriptor
    {
        public string ModuleType => "Characters";
        public string DisplayName => CharactersStrings.DisplayName;
        public string Description => CharactersStrings.Description;

        public IReadOnlyList<HotKey> GetHotKeys() => new[]
        {
            new HotKey { Id = "characters.open_characters",       DisplayNameKey = CharactersStrings.HotKey_OpenCharacters,       Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.open_relationships",    DisplayNameKey = CharactersStrings.HotKey_OpenRelationships,    Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.open_templates",        DisplayNameKey = CharactersStrings.HotKey_OpenTemplates,        Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.create",                DisplayNameKey = CharactersStrings.HotKey_CreateCharacter,      Category = HotKeyCategory.Tools,      Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.create_randomized",     DisplayNameKey = CharactersStrings.HotKey_CreateRandomized,     Category = HotKeyCategory.Tools,      Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.create_collective",     DisplayNameKey = CharactersStrings.HotKey_CreateCollective,     Category = HotKeyCategory.Tools,      Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.save",                  DisplayNameKey = CharactersStrings.HotKey_SaveCard,             Category = HotKeyCategory.Tools,      Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.close_card",            DisplayNameKey = CharactersStrings.HotKey_CloseCard,            Category = HotKeyCategory.Tools,      Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.delete",                DisplayNameKey = CharactersStrings.HotKey_DeleteCharacter,      Category = HotKeyCategory.Tools,      Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.duplicate",             DisplayNameKey = CharactersStrings.HotKey_DuplicateCharacter,   Category = HotKeyCategory.Tools,      Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.focus_search",          DisplayNameKey = CharactersStrings.HotKey_FocusSearch,          Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.clear_filters",         DisplayNameKey = CharactersStrings.HotKey_ClearFilters,         Category = HotKeyCategory.Tools,      Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.view_list",             DisplayNameKey = CharactersStrings.HotKey_ViewList,             Category = HotKeyCategory.View,       Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.view_grid",             DisplayNameKey = CharactersStrings.HotKey_ViewGrid,             Category = HotKeyCategory.View,       Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_next",              DisplayNameKey = CharactersStrings.HotKey_TabNext,              Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_prev",              DisplayNameKey = CharactersStrings.HotKey_TabPrev,              Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_basics",            DisplayNameKey = CharactersStrings.HotKey_TabBasics,            Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_parameters",        DisplayNameKey = CharactersStrings.HotKey_TabParameters,        Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_relationships_card",DisplayNameKey = CharactersStrings.HotKey_TabRelationshipsCard, Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_contexts",          DisplayNameKey = CharactersStrings.HotKey_TabContexts,          Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_notes",             DisplayNameKey = CharactersStrings.HotKey_TabNotes,             Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_personal_timeline", DisplayNameKey = CharactersStrings.HotKey_TabPersonalTimeline,  Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.tab_history",           DisplayNameKey = CharactersStrings.HotKey_TabHistory,           Category = HotKeyCategory.Navigation, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.add_parameter_numeric",   DisplayNameKey = CharactersStrings.HotKey_AddParameterNumeric,  Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.add_parameter_text",      DisplayNameKey = CharactersStrings.HotKey_AddParameterText,     Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.add_parameter_statelist", DisplayNameKey = CharactersStrings.HotKey_AddParameterStateList,Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.add_parameter_boolean",   DisplayNameKey = CharactersStrings.HotKey_AddParameterBoolean,  Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.add_context",           DisplayNameKey = CharactersStrings.HotKey_AddContext,           Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.add_relationship",      DisplayNameKey = CharactersStrings.HotKey_AddRelationship,      Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.add_note",              DisplayNameKey = CharactersStrings.HotKey_AddNote,              Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.add_timeline_event",    DisplayNameKey = CharactersStrings.HotKey_AddTimelineEvent,     Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.randomize_parameters",  DisplayNameKey = CharactersStrings.HotKey_RandomizeParameters,  Category = HotKeyCategory.Tools, Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.graph_reset_view",      DisplayNameKey = CharactersStrings.HotKey_GraphResetView,       Category = HotKeyCategory.View,  Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
            new HotKey { Id = "characters.graph_clear_focus",     DisplayNameKey = CharactersStrings.HotKey_GraphClearFocus,      Category = HotKeyCategory.View,  Scope = HotKeyScope.Background, ModuleType = ModuleType, DefaultGesture = null },
        };
    }
}