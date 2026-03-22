using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Writersword.Core.Enums;
using Writersword.Core.Models.Settings;
using Writersword.Resources.Localization;
using Writersword.Core.Interfaces.Services.Input;

namespace Writersword.ViewModels.Settings
{
    /// <summary>
    /// Активная вкладка внутри настроек горячих клавиш.
    /// Bindings — таблица хоткеев с навигацией по секциям.
    /// Prefixes — управление зарегистрированными префиксами.
    /// </summary>
    public enum HotKeySettingsTab
    {
        Bindings,
        Prefixes
    }

    /// <summary>
    /// Отдельный жест внутри строки горячей клавиши.
    /// Один HotKeyRowViewModel может содержать несколько HotKeyBindingViewModel (мульти-бинд).
    /// Каждый биндинг хранит собственный префикс и состояние Popup.
    /// </summary>
    public class HotKeyBindingViewModel : ReactiveObject
    {
        private string _gestureDisplay;
        private bool _isEditing;
        private KeyGesture? _selectedPrefix;
        private bool _isPrefixPopupOpen;

        /// <summary>Порядковый индекс жеста в списке CustomGestures или DefaultGestures</summary>
        public int Index { get; }

        /// <summary>Является ли жест пользовательским (иначе — дефолтный)</summary>
        public bool IsCustom { get; }

        /// <summary>
        /// Ссылка на родительскую строку.
        /// Используется в XAML командах чтобы не передавать кортеж через CommandParameter.
        /// </summary>
        public HotKeyRowViewModel ParentRow { get; internal set; } = null!;

        /// <summary>Отображаемая строка жеста (только последний шаг если последовательность)</summary>
        public string GestureDisplay
        {
            get => _gestureDisplay;
            set => this.RaiseAndSetIfChanged(ref _gestureDisplay, value);
        }

        /// <summary>В режиме редактирования (ожидает нажатия клавиши)</summary>
        public bool IsEditing
        {
            get => _isEditing;
            set => this.RaiseAndSetIfChanged(ref _isEditing, value);
        }

        /// <summary>
        /// Выбранный префикс для этого конкретного биндинга.
        /// null — биндинг одиночный, без префикса.
        /// </summary>
        public KeyGesture? SelectedPrefix
        {
            get => _selectedPrefix;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedPrefix, value);
                this.RaisePropertyChanged(nameof(HasPrefix));
                this.RaisePropertyChanged(nameof(PrefixDisplayText));
            }
        }

        /// <summary>Есть ли выбранный префикс у этого биндинга</summary>
        public bool HasPrefix => _selectedPrefix != null;

        /// <summary>"—" если префикс не выбран, иначе строка жеста префикса</summary>
        public string PrefixDisplayText => _selectedPrefix?.ToString() ?? "—";

        /// <summary>
        /// Открыт ли Popup выбора префикса для этого биндинга.
        /// Только один Popup может быть открыт одновременно — управляется из VM.
        /// </summary>
        public bool IsPrefixPopupOpen
        {
            get => _isPrefixPopupOpen;
            set => this.RaiseAndSetIfChanged(ref _isPrefixPopupOpen, value);
        }

        public HotKeyBindingViewModel(int index, bool isCustom, string gestureDisplay, KeyGesture? prefix = null)
        {
            Index = index;
            IsCustom = isCustom;
            _gestureDisplay = gestureDisplay;
            _selectedPrefix = prefix;
        }
    }

    /// <summary>
    /// Строка в таблице горячих клавиш.
    /// Содержит список биндингов (мульти-бинд) и флаги конфликтов/префиксов.
    /// Префикс теперь хранится на уровне каждого биндинга, не строки.
    /// </summary>
    public class HotKeyRowViewModel : ReactiveObject
    {
        private HotKeyConflictType _conflictType;
        private string _conflictTooltip;
        private bool _isExecutorBound;
        private bool _isPrefix;
        private string _errorMessage = string.Empty;

        /// <summary>ID горячей клавиши</summary>
        public string Id { get; }

        /// <summary>
        /// Отображаемое имя команды.
        /// Резолвится через ResourceManager — если DisplayNameKey является ключом локализации,
        /// берётся локализованная строка, иначе используется сам ключ как fallback.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>Тип модуля (null для глобальных)</summary>
        public string? ModuleType { get; }

        /// <summary>Категория горячей клавиши для группировки в подсекциях</summary>
        public HotKeyCategory Category { get; }

        /// <summary>Область действия</summary>
        public HotKeyScope Scope { get; }

        /// <summary>Жесты по умолчанию — строка для отображения в колонке "По умолчанию"</summary>
        public string DefaultGesturesDisplay { get; }

        /// <summary>Список активных биндингов для отображения в колонке "Текущая"</summary>
        public ObservableCollection<HotKeyBindingViewModel> Bindings { get; } = new();

        /// <summary>Тип конфликта</summary>
        public HotKeyConflictType ConflictType
        {
            get => _conflictType;
            set
            {
                this.RaiseAndSetIfChanged(ref _conflictType, value);
                this.RaisePropertyChanged(nameof(HasConflict));
                this.RaisePropertyChanged(nameof(IsCritical));
                this.RaisePropertyChanged(nameof(IsWarning));
            }
        }

        /// <summary>Подсказка с описанием конфликта</summary>
        public string ConflictTooltip
        {
            get => _conflictTooltip;
            set => this.RaiseAndSetIfChanged(ref _conflictTooltip, value);
        }

        /// <summary>
        /// Запущен ли модуль-владелец этой клавиши.
        /// false — модуль не активен, клавиша зарегистрирована но не выполняется.
        /// </summary>
        public bool IsExecutorBound
        {
            get => _isExecutorBound;
            set => this.RaiseAndSetIfChanged(ref _isExecutorBound, value);
        }

        /// <summary>
        /// Является ли один из жестов этой клавиши зарезервированным префиксом.
        /// Означает что этот жест используется как первый шаг последовательности другой клавиши.
        /// </summary>
        public bool IsPrefix
        {
            get => _isPrefix;
            set => this.RaiseAndSetIfChanged(ref _isPrefix, value);
        }

        /// <summary>
        /// Временное сообщение об ошибке назначения комбинации.
        /// Показывается под строкой и сбрасывается автоматически через 3 секунды.
        /// </summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                this.RaiseAndSetIfChanged(ref _errorMessage, value);
                this.RaisePropertyChanged(nameof(HasError));
            }
        }

        /// <summary>Есть ли ошибка для отображения</summary>
        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        public bool HasConflict => _conflictType != HotKeyConflictType.None;
        public bool IsCritical => _conflictType == HotKeyConflictType.Critical;
        public bool IsWarning => _conflictType == HotKeyConflictType.Warning;

        /// <summary>Есть ли хотя бы один пользовательский биндинг</summary>
        public bool HasCustomBindings => Bindings.Any(b => b.IsCustom);

        public HotKeyRowViewModel(HotKey hotKey, bool isExecutorBound)
        {
            Id = hotKey.Id;
            ModuleType = hotKey.ModuleType;
            Scope = hotKey.Scope;
            Category = hotKey.Category;
            _isExecutorBound = isExecutorBound;
            _conflictType = HotKeyConflictType.None;
            _conflictTooltip = string.Empty;

            var localizedName = Strings.ResourceManager.GetString(hotKey.DisplayNameKey);
            DisplayName = string.IsNullOrEmpty(localizedName)
                ? hotKey.DisplayNameKey
                : localizedName;

            DefaultGesturesDisplay = hotKey.DefaultGestures.Count > 0
                ? string.Join(", ", hotKey.DefaultGestures.Select(g => g.ToString()))
                : Strings.HotKey_NotAssigned;

            RebuildBindings(hotKey);
        }

        /// <summary>
        /// Пересобрать список биндингов из модели HotKey.
        /// Каждый биндинг получает свой собственный префикс если жест является последовательностью.
        /// </summary>
        public void RebuildBindings(HotKey hotKey)
        {
            Bindings.Clear();

            if (hotKey.CustomGestures.Count > 0)
            {
                for (int i = 0; i < hotKey.CustomGestures.Count; i++)
                {
                    var g = hotKey.CustomGestures[i];
                    var display = g.IsSequence ? g.Steps.Last().ToString() : g.ToString();
                    var prefix = g.IsSequence ? g.FirstStep : null;
                    var binding = new HotKeyBindingViewModel(i, true, display, prefix);
                    binding.ParentRow = this;
                    Bindings.Add(binding);
                }
            }
            else if (hotKey.DefaultGestures.Count > 0)
            {
                for (int i = 0; i < hotKey.DefaultGestures.Count; i++)
                {
                    var g = hotKey.DefaultGestures[i];
                    var display = g.IsSequence ? g.Steps.Last().ToString() : g.ToString();
                    var prefix = g.IsSequence ? g.FirstStep : null;
                    var binding = new HotKeyBindingViewModel(i, false, display, prefix);
                    binding.ParentRow = this;
                    Bindings.Add(binding);
                }
            }
            else
            {
                var binding = new HotKeyBindingViewModel(0, false, Strings.HotKey_NotAssigned);
                binding.ParentRow = this;
                Bindings.Add(binding);
            }

            this.RaisePropertyChanged(nameof(HasCustomBindings));
        }
    }

    /// <summary>
    /// Подсекция навигации — категория внутри секции (File, Tools и т.д.).
    /// </summary>
    public class HotKeySubSectionItem : ReactiveObject
    {
        private bool _isSelected;

        public string Title { get; }
        public HotKeyCategory Category { get; }
        public string? ModuleType { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public HotKeySubSectionItem(string title, HotKeyCategory category, string? moduleType)
        {
            Title = title;
            Category = category;
            ModuleType = moduleType;
        }
    }

    /// <summary>
    /// Секция в левой навигационной панели.
    /// Соответствует либо глобальным клавишам, либо отдельному модулю.
    /// </summary>
    public class HotKeySectionItem : ReactiveObject
    {
        private bool _isSelected;
        private bool _isExpanded;

        public string Title { get; }
        public string? ModuleType { get; }
        public bool IsGlobal => ModuleType == null;
        public ObservableCollection<HotKeySubSectionItem> SubSections { get; } = new();
        public bool HasSubSections => SubSections.Count > 0;

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
        }

        public HotKeySectionItem(string title, string? moduleType)
        {
            Title = title;
            ModuleType = moduleType;
            _isExpanded = false;
        }
    }

    /// <summary>
    /// Строка в таблице управления префиксами.
    /// </summary>
    public class PrefixRowViewModel : ReactiveObject
    {
        private string _comment;
        private bool _isEditingGesture;
        private bool _isEditingComment;
        private string _errorMessage;
        private string _gestureDisplay;

        public KeyGesture? Gesture { get; private set; }

        public string GestureDisplay
        {
            get => _gestureDisplay;
            private set => this.RaiseAndSetIfChanged(ref _gestureDisplay, value);
        }

        public string Comment
        {
            get => _comment;
            set => this.RaiseAndSetIfChanged(ref _comment, value);
        }

        public ObservableCollection<string> UsedByDisplayNames { get; } = new();

        public string UsedByText => UsedByDisplayNames.Count > 0
            ? string.Join(", ", UsedByDisplayNames)
            : Strings.HotKey_Prefix_UsedByNone;

        public bool IsEditingGesture
        {
            get => _isEditingGesture;
            set => this.RaiseAndSetIfChanged(ref _isEditingGesture, value);
        }

        public bool IsEditingComment
        {
            get => _isEditingComment;
            set => this.RaiseAndSetIfChanged(ref _isEditingComment, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                this.RaiseAndSetIfChanged(ref _errorMessage, value);
                this.RaisePropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorMessage);
        public bool IsAutoDerived { get; }
        public bool CanRemove => !IsAutoDerived && UsedByDisplayNames.Count == 0;

        public PrefixRowViewModel(KeyGesture? gesture, string comment, bool isAutoDerived)
        {
            Gesture = gesture;
            _comment = comment;
            _errorMessage = string.Empty;
            IsAutoDerived = isAutoDerived;
            _gestureDisplay = gesture?.ToString() ?? Strings.HotKey_Prefix_PressKey;
        }

        public void ApplyGesture(KeyGesture gesture)
        {
            Gesture = gesture;
            GestureDisplay = gesture.ToString();
            IsEditingGesture = false;
            ErrorMessage = string.Empty;
        }

        public void RefreshUsedBy(System.Collections.Generic.IEnumerable<string> displayNames)
        {
            UsedByDisplayNames.Clear();
            foreach (var name in displayNames)
                UsedByDisplayNames.Add(name);

            this.RaisePropertyChanged(nameof(UsedByText));
            this.RaisePropertyChanged(nameof(CanRemove));
        }
    }

    /// <summary>
    /// Пункт в Popup выбора префикса для биндинга.
    /// </summary>
    public class PrefixPopupItem
    {
        public KeyGesture? Gesture { get; }
        public string DisplayText { get; }
        public bool IsRemoveAction { get; }

        public PrefixPopupItem(KeyGesture? gesture, string displayText, bool isRemoveAction = false)
        {
            Gesture = gesture;
            DisplayText = displayText;
            IsRemoveAction = isRemoveAction;
        }
    }

    /// <summary>
    /// ViewModel вкладки настроек горячих клавиш.
    /// Содержит две внутренние вкладки: Привязки и Префиксы.
    /// </summary>
    public class HotKeySettingsViewModel : ReactiveObject
    {
        private readonly ILogger<HotKeySettingsViewModel> _logger;
        private readonly IHotKeyService _hotKeyService;

        // -------------------------------------------------------------------
        // Состояние вкладок
        // -------------------------------------------------------------------

        private HotKeySettingsTab _activeTab = HotKeySettingsTab.Bindings;

        public HotKeySettingsTab ActiveTab
        {
            get => _activeTab;
            set
            {
                // Сохраняем редактируемый комментарий перед переключением вкладки
                if (_activeTab == HotKeySettingsTab.Prefixes && value != HotKeySettingsTab.Prefixes)
                {
                    foreach (var row in PrefixRows)
                    {
                        if (row.IsEditingComment && row.Gesture != null && !row.IsAutoDerived)
                        {
                            _hotKeyService.UpdatePrefixComment(row.Gesture, row.Comment);
                            row.IsEditingComment = false;
                        }
                    }
                }

                this.RaiseAndSetIfChanged(ref _activeTab, value);
                this.RaisePropertyChanged(nameof(IsBindingsTabActive));
                this.RaisePropertyChanged(nameof(IsPrefixesTabActive));

                if (value == HotKeySettingsTab.Prefixes)
                    RefreshPrefixRows();
            }
        }

        public bool IsBindingsTabActive => _activeTab == HotKeySettingsTab.Bindings;
        public bool IsPrefixesTabActive => _activeTab == HotKeySettingsTab.Prefixes;

        // -------------------------------------------------------------------
        // Состояние редактирования
        // -------------------------------------------------------------------

        private HotKeyBindingViewModel? _editingBinding;
        private HotKeyRowViewModel? _editingRow;

        /// <summary>Биндинг для которого сейчас открыт Popup префикса</summary>
        private HotKeyBindingViewModel? _prefixPopupBinding;

        private string _filterText = string.Empty;
        private HotKeySectionItem? _selectedSection;
        private HotKeySubSectionItem? _selectedSubSection;
        private string _liveInputDisplay = string.Empty;
        private bool _isLiveInputActive;
        private KeyModifiers _currentModifiers = KeyModifiers.None;

        private PrefixRowViewModel? _editingPrefixRow;
        private KeyModifiers _prefixCurrentModifiers = KeyModifiers.None;
        private string _prefixTabErrorMessage = string.Empty;

        // -------------------------------------------------------------------
        // Коллекции
        // -------------------------------------------------------------------

        public ObservableCollection<HotKeySectionItem> Sections { get; } = new();
        public ObservableCollection<HotKeyRowViewModel> AllRows { get; } = new();
        public ObservableCollection<HotKeyRowViewModel> FilteredRows { get; } = new();
        public ObservableCollection<PrefixRowViewModel> PrefixRows { get; } = new();

        /// <summary>
        /// Пункты Popup выбора префикса.
        /// Пересобирается при открытии Popup через TogglePrefixPopupCommand.
        /// </summary>
        public ObservableCollection<PrefixPopupItem> PrefixPopupItems { get; } = new();

        // -------------------------------------------------------------------
        // Свойства
        // -------------------------------------------------------------------

        public HotKeySectionItem? SelectedSection
        {
            get => _selectedSection;
            private set => this.RaiseAndSetIfChanged(ref _selectedSection, value);
        }

        public HotKeySubSectionItem? SelectedSubSection
        {
            get => _selectedSubSection;
            private set => this.RaiseAndSetIfChanged(ref _selectedSubSection, value);
        }

        public string FilterText
        {
            get => _filterText;
            set
            {
                this.RaiseAndSetIfChanged(ref _filterText, value);
                ApplyFilter();
            }
        }

        public string LiveInputDisplay
        {
            get => _liveInputDisplay;
            set => this.RaiseAndSetIfChanged(ref _liveInputDisplay, value);
        }

        public bool IsLiveInputActive
        {
            get => _isLiveInputActive;
            set => this.RaiseAndSetIfChanged(ref _isLiveInputActive, value);
        }

        public string PrefixTabErrorMessage
        {
            get => _prefixTabErrorMessage;
            set
            {
                this.RaiseAndSetIfChanged(ref _prefixTabErrorMessage, value);
                this.RaisePropertyChanged(nameof(HasPrefixTabError));
            }
        }

        public bool HasPrefixTabError => !string.IsNullOrEmpty(_prefixTabErrorMessage);

        /// <summary>
        /// Активно ли редактирование биндинга или префикса прямо сейчас.
        /// Используется в code-behind чтобы не перехватывать клавиши вне режима редактирования.
        /// </summary>
        public bool IsEditingActive => _editingBinding != null || _editingPrefixRow != null;

        // -------------------------------------------------------------------
        // Команды биндингов
        // -------------------------------------------------------------------

        public ReactiveCommand<HotKeyRowViewModel, Unit> ResetRowCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetAllCommand { get; }
        public ReactiveCommand<HotKeyBindingViewModel, Unit> StartEditBindingCommand { get; }
        public ReactiveCommand<HotKeyRowViewModel, Unit> AddBindingCommand { get; }
        public ReactiveCommand<HotKeyBindingViewModel, Unit> RemoveBindingCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }
        public ReactiveCommand<HotKeySectionItem, Unit> SelectSectionCommand { get; }
        public ReactiveCommand<HotKeySubSectionItem, Unit> SelectSubSectionCommand { get; }

        // -------------------------------------------------------------------
        // Команды вкладок
        // -------------------------------------------------------------------

        public ReactiveCommand<Unit, Unit> ShowBindingsTabCommand { get; }
        public ReactiveCommand<Unit, Unit> ShowPrefixesTabCommand { get; }

        // -------------------------------------------------------------------
        // Команды префикса — принимают HotKeyBindingViewModel (не Row!)
        // -------------------------------------------------------------------

        /// <summary>
        /// Открыть/закрыть Popup выбора префикса для конкретного биндинга.
        /// Принимает HotKeyBindingViewModel — у каждого биндинга свой независимый Popup.
        /// </summary>
        public ReactiveCommand<HotKeyBindingViewModel, Unit> TogglePrefixPopupCommand { get; }

        /// <summary>
        /// Выбрать префикс из Popup и применить к биндингу.
        /// </summary>
        public ReactiveCommand<PrefixPopupItem, Unit> SelectPrefixForRowCommand { get; }

        // -------------------------------------------------------------------
        // Команды управления префиксами во вкладке Префиксы
        // -------------------------------------------------------------------

        public ReactiveCommand<Unit, Unit> AddPrefixCommand { get; }
        public ReactiveCommand<PrefixRowViewModel, Unit> RemovePrefixCommand { get; }
        public ReactiveCommand<PrefixRowViewModel, Unit> StartEditPrefixGestureCommand { get; }
        public ReactiveCommand<PrefixRowViewModel, Unit> SavePrefixCommentCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelEditPrefixCommand { get; }
        public ReactiveCommand<PrefixRowViewModel, Unit> StartEditCommentCommand { get; }

        // -------------------------------------------------------------------
        // События
        // -------------------------------------------------------------------

        /// <summary>
        /// Срабатывает когда биндинг или префикс переходит в режим редактирования.
        /// View подписывается чтобы захватить фокус для перехвата KeyDown/KeyUp.
        /// </summary>
        public event Action? EditingStarted;

        // -------------------------------------------------------------------
        // Конструктор
        // -------------------------------------------------------------------

        public HotKeySettingsViewModel()
        {
            _logger = App.Services.GetService<ILogger<HotKeySettingsViewModel>>()!;
            _hotKeyService = App.Services.GetRequiredService<IHotKeyService>();

            ResetRowCommand = ReactiveCommand.Create<HotKeyRowViewModel>(ResetRow);
            ResetAllCommand = ReactiveCommand.Create(ResetAll);
            StartEditBindingCommand = ReactiveCommand.Create<HotKeyBindingViewModel>(StartEditBinding);
            AddBindingCommand = ReactiveCommand.Create<HotKeyRowViewModel>(AddBinding);
            RemoveBindingCommand = ReactiveCommand.Create<HotKeyBindingViewModel>(RemoveBinding);
            CancelEditCommand = ReactiveCommand.Create(CancelEditBinding);
            SelectSectionCommand = ReactiveCommand.Create<HotKeySectionItem>(SelectSection);
            SelectSubSectionCommand = ReactiveCommand.Create<HotKeySubSectionItem>(SelectSubSection);

            ShowBindingsTabCommand = ReactiveCommand.Create(() => { ActiveTab = HotKeySettingsTab.Bindings; });
            ShowPrefixesTabCommand = ReactiveCommand.Create(() => { ActiveTab = HotKeySettingsTab.Prefixes; });

            // Ключевое: TogglePrefixPopupCommand принимает HotKeyBindingViewModel
            TogglePrefixPopupCommand = ReactiveCommand.Create<HotKeyBindingViewModel>(TogglePrefixPopup);
            SelectPrefixForRowCommand = ReactiveCommand.Create<PrefixPopupItem>(SelectPrefixForRow);

            AddPrefixCommand = ReactiveCommand.Create(AddPrefix);
            RemovePrefixCommand = ReactiveCommand.Create<PrefixRowViewModel>(RemovePrefix);
            StartEditPrefixGestureCommand = ReactiveCommand.Create<PrefixRowViewModel>(StartEditPrefixGesture);
            SavePrefixCommentCommand = ReactiveCommand.Create<PrefixRowViewModel>(SavePrefixComment);
            CancelEditPrefixCommand = ReactiveCommand.Create(CancelEditPrefix);
            StartEditCommentCommand = ReactiveCommand.Create<PrefixRowViewModel>(row =>
            {
                if (!row.IsAutoDerived) row.IsEditingComment = true;
            });

            LoadAll();
            _hotKeyService.HotKeysChanged += OnHotKeysChanged;
        }

        // -------------------------------------------------------------------
        // Публичные методы для code-behind
        // -------------------------------------------------------------------

        public void HandleKeyDown(Key key, KeyModifiers modifiers)
        {
            if (_editingPrefixRow != null) HandlePrefixKeyDown(key, modifiers);
            else if (_editingBinding != null) HandleBindingKeyDown(key, modifiers);
        }

        public void HandleKeyUp(Key key, KeyModifiers modifiers)
        {
            if (_editingPrefixRow != null) HandlePrefixKeyUp(key, modifiers);
            else if (_editingBinding != null) HandleBindingKeyUp(key, modifiers);
        }

        // -------------------------------------------------------------------
        // Обработка клавиш для биндингов
        // -------------------------------------------------------------------

        private void HandleBindingKeyDown(Key key, KeyModifiers modifiers)
        {
            if (_editingBinding == null || _editingRow == null) return;

            bool isModifierOnly = key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin;

            if (isModifierOnly)
            {
                if (key is not Key.LWin and not Key.RWin)
                {
                    _currentModifiers = modifiers;
                    UpdateBindingLiveDisplay();
                }
                return;
            }

            if (key == Key.Escape) { CancelEditBinding(); return; }

            if (key is Key.Delete or Key.Back)
            {
                var rowToReset = _editingRow;
                CancelEditBinding();
                ResetRow(rowToReset);
                return;
            }

            var cleanModifiers = modifiers & ~KeyModifiers.Meta;
            var gesture = new KeyGesture(key, cleanModifiers);

            // Берём префикс из биндинга — каждый биндинг хранит свой собственный
            HotKeyGesture hotKeyGesture;
            if (_editingBinding.SelectedPrefix != null)
                hotKeyGesture = new HotKeyGesture(
                    new System.Collections.Generic.List<KeyGesture> { _editingBinding.SelectedPrefix, gesture });
            else
                hotKeyGesture = new HotKeyGesture(gesture);

            var row = _editingRow;
            var binding = _editingBinding;
            bool isNewBinding = !binding.IsCustom && binding.GestureDisplay == Strings.HotKey_PressKey;
            CancelEditBinding();

            if (isNewBinding)
            {
                CommitNewBinding(row, hotKeyGesture);
                RefreshConflictsAndPrefixes();
                return;
            }

            GestureAssignResult result;
            if (binding.IsCustom)
                result = _hotKeyService.ReplaceCustomGesture(row.Id, binding.Index, hotKeyGesture);
            else
                result = _hotKeyService.SetCustomGestureSequence(row.Id, hotKeyGesture);

            if (result == GestureAssignResult.Ok)
            {
                var updatedHotKey = _hotKeyService.GetHotKey(row.Id);
                if (updatedHotKey != null) row.RebuildBindings(updatedHotKey);
            }
            else
            {
                row.ErrorMessage = ResolveBindingAssignError(result);
                _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (row.ErrorMessage == ResolveBindingAssignError(result))
                            row.ErrorMessage = string.Empty;
                    }));
            }

            RefreshConflictsAndPrefixes();
        }

        private void HandleBindingKeyUp(Key key, KeyModifiers modifiers)
        {
            if (_editingBinding == null) return;
            if (key is Key.LWin or Key.RWin) return;
            _currentModifiers = modifiers & ~KeyModifiers.Meta;
            UpdateBindingLiveDisplay();
        }

        // -------------------------------------------------------------------
        // Обработка клавиш для префиксов
        // -------------------------------------------------------------------

        private void HandlePrefixKeyDown(Key key, KeyModifiers modifiers)
        {
            if (_editingPrefixRow == null) return;

            bool isModifierOnly = key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin;

            if (isModifierOnly)
            {
                if (key is not Key.LWin and not Key.RWin)
                {
                    _prefixCurrentModifiers = modifiers;
                    UpdatePrefixLiveDisplay();
                }
                return;
            }

            if (key == Key.Escape) { CancelEditPrefix(); return; }

            var cleanModifiers = modifiers & ~KeyModifiers.Meta;
            var gesture = new KeyGesture(key, cleanModifiers);
            var row = _editingPrefixRow;
            bool isNew = row.Gesture == null;

            _editingPrefixRow = null;
            _prefixCurrentModifiers = KeyModifiers.None;
            IsLiveInputActive = false;
            LiveInputDisplay = string.Empty;
            row.IsEditingGesture = false;

            if (isNew)
            {
                var result = _hotKeyService.RegisterPrefix(gesture, row.Comment);
                if (result == GestureAssignResult.Ok)
                {
                    row.ApplyGesture(gesture);
                    RefreshPrefixRows();
                }
                else
                {
                    PrefixRows.Remove(row);
                    var errorMessage = ResolveGestureAssignError(result);
                    PrefixTabErrorMessage = errorMessage;
                    _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (PrefixTabErrorMessage == errorMessage)
                                PrefixTabErrorMessage = string.Empty;
                        }));
                }
            }
            else
            {
                var oldGesture = row.Gesture!;
                var unregResult = _hotKeyService.UnregisterPrefix(oldGesture);

                if (unregResult == GestureAssignResult.Ok || unregResult == GestureAssignResult.HotKeyNotFound)
                {
                    var registerResult = _hotKeyService.RegisterPrefix(gesture, row.Comment);
                    if (registerResult == GestureAssignResult.Ok)
                    {
                        row.ApplyGesture(gesture);
                        RefreshPrefixRows();
                    }
                    else
                    {
                        _hotKeyService.RegisterPrefix(oldGesture, row.Comment);
                        var errorMessage = ResolveGestureAssignError(registerResult);
                        row.ErrorMessage = errorMessage;
                        _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                if (row.ErrorMessage == errorMessage)
                                    row.ErrorMessage = string.Empty;
                            }));
                    }
                }
                else
                {
                    var errorMessage = ResolveGestureAssignError(unregResult);
                    row.ErrorMessage = errorMessage;
                    _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (row.ErrorMessage == errorMessage)
                                row.ErrorMessage = string.Empty;
                        }));
                }
            }
        }

        private void HandlePrefixKeyUp(Key key, KeyModifiers modifiers)
        {
            if (_editingPrefixRow == null) return;
            if (key is Key.LWin or Key.RWin) return;
            _prefixCurrentModifiers = modifiers & ~KeyModifiers.Meta;
            UpdatePrefixLiveDisplay();
        }

        // -------------------------------------------------------------------
        // Popup выбора префикса — теперь на уровне биндинга
        // -------------------------------------------------------------------

        /// <summary>
        /// Открыть/закрыть Popup выбора префикса для конкретного биндинга.
        /// Закрывает все Popup на всех биндингах всех строк перед открытием нового.
        /// </summary>
        private void TogglePrefixPopup(HotKeyBindingViewModel binding)
        {
            bool wasOpen = binding.IsPrefixPopupOpen;

            // Закрываем все открытые Popup у всех биндингов
            foreach (var row in AllRows)
                foreach (var b in row.Bindings)
                    b.IsPrefixPopupOpen = false;

            _prefixPopupBinding = null;

            if (wasOpen) return;

            PrefixPopupItems.Clear();

            if (binding.HasPrefix)
                PrefixPopupItems.Add(new PrefixPopupItem(null, Strings.HotKey_Prefix_Remove, isRemoveAction: true));

            var reservedPrefixes = _hotKeyService.GetReservedPrefixes();
            foreach (var gesture in reservedPrefixes)
                PrefixPopupItems.Add(new PrefixPopupItem(gesture, gesture.ToString()));

            if (!reservedPrefixes.Any())
            {
                _logger.LogDebug("No prefixes registered, Popup not opened");
                return;
            }

            _prefixPopupBinding = binding;
            binding.IsPrefixPopupOpen = true;
        }

        /// <summary>
        /// Применить выбранный префикс к биндингу.
        /// IsRemoveAction — убрать префикс, сделать жест одиночным.
        /// Иначе — применить префикс немедленно если жест уже есть,
        /// или дождаться ввода клавиши если биндинг пустой.
        /// </summary>
        private void SelectPrefixForRow(PrefixPopupItem item)
        {
            var binding = _prefixPopupBinding;
            if (binding == null) return;

            var row = binding.ParentRow;
            binding.IsPrefixPopupOpen = false;
            _prefixPopupBinding = null;

            var hotKey = _hotKeyService.GetHotKey(row.Id);
            if (hotKey == null) return;

            if (item.IsRemoveAction)
            {
                binding.SelectedPrefix = null;

                if (binding.IsCustom && binding.Index < hotKey.CustomGestures.Count)
                {
                    var existing = hotKey.CustomGestures[binding.Index];
                    var secondStep = existing.IsSequence ? existing.Steps.Last() : existing.FirstStep;
                    _hotKeyService.ReplaceCustomGesture(row.Id, binding.Index, new HotKeyGesture(secondStep));
                }
                else if (!binding.IsCustom)
                {
                    var existing = hotKey.ActiveGesture;
                    if (existing != null)
                    {
                        var secondStep = existing.IsSequence ? existing.Steps.Last() : existing.FirstStep;
                        _hotKeyService.SetCustomGestureSequence(row.Id, new HotKeyGesture(secondStep));
                    }
                }

                var updated = _hotKeyService.GetHotKey(row.Id);
                if (updated != null) row.RebuildBindings(updated);
            }
            else
            {
                binding.SelectedPrefix = item.Gesture;

                // Применяем немедленно если биндинг уже имеет жест
                if (binding.GestureDisplay != Strings.HotKey_PressKey &&
                    binding.GestureDisplay != Strings.HotKey_NotAssigned)
                {
                    KeyGesture? secondStep = null;

                    if (binding.IsCustom && binding.Index < hotKey.CustomGestures.Count)
                    {
                        var g = hotKey.CustomGestures[binding.Index];
                        secondStep = g.IsSequence ? g.Steps.Last() : g.FirstStep;
                    }
                    else if (!binding.IsCustom && binding.Index < hotKey.DefaultGestures.Count)
                    {
                        var g = hotKey.DefaultGestures[binding.Index];
                        secondStep = g.IsSequence ? g.Steps.Last() : g.FirstStep;
                    }

                    if (secondStep != null)
                    {
                        var sequence = new HotKeyGesture(
                            new System.Collections.Generic.List<KeyGesture> { item.Gesture!, secondStep });

                        if (binding.IsCustom)
                            _hotKeyService.ReplaceCustomGesture(row.Id, binding.Index, sequence);
                        else
                            _hotKeyService.SetCustomGestureSequence(row.Id, sequence);

                        var updated = _hotKeyService.GetHotKey(row.Id);
                        if (updated != null) row.RebuildBindings(updated);
                    }
                }
                // Иначе биндинг пустой — пользователь нажмёт клавишу через StartEditBinding
            }

            RefreshConflictsAndPrefixes();
        }

        // -------------------------------------------------------------------
        // Загрузка данных
        // -------------------------------------------------------------------

        private void LoadAll()
        {
            Sections.Clear();
            AllRows.Clear();

            var hotKeys = _hotKeyService.GetAllHotKeys();

            var globalKeys = hotKeys
                .Where(hk => hk.ModuleType == null)
                .OrderBy(hk => hk.Category).ThenBy(hk => hk.Id)
                .ToList();

            if (globalKeys.Count > 0)
            {
                var globalSection = new HotKeySectionItem(Strings.HotKey_Section_Global, null);

                var globalCategories = globalKeys.Select(hk => hk.Category).Distinct().OrderBy(c => c).ToList();
                if (globalCategories.Count > 1)
                    foreach (var category in globalCategories)
                        globalSection.SubSections.Add(
                            new HotKeySubSectionItem(ResolveCategoryTitle(category), category, null));

                Sections.Add(globalSection);
                foreach (var hk in globalKeys)
                    AllRows.Add(new HotKeyRowViewModel(hk, true));
            }

            var moduleTypes = hotKeys
                .Where(hk => hk.ModuleType != null)
                .Select(hk => hk.ModuleType!)
                .Distinct().OrderBy(mt => mt)
                .ToList();

            foreach (var moduleType in moduleTypes)
            {
                var moduleKeys = hotKeys
                    .Where(hk => hk.ModuleType == moduleType)
                    .OrderBy(hk => hk.Category).ThenBy(hk => hk.Id)
                    .ToList();

                if (moduleKeys.Count == 0) continue;

                var sectionTitle = Strings.ResourceManager.GetString(moduleType);
                var section = new HotKeySectionItem(
                    string.IsNullOrEmpty(sectionTitle) ? moduleType : sectionTitle, moduleType);

                var moduleCategories = moduleKeys.Select(hk => hk.Category).Distinct().OrderBy(c => c).ToList();
                if (moduleCategories.Count > 1)
                    foreach (var category in moduleCategories)
                        section.SubSections.Add(
                            new HotKeySubSectionItem(ResolveCategoryTitle(category), category, moduleType));

                Sections.Add(section);
                bool executorBound = _hotKeyService.IsExecutorBound(moduleType);
                foreach (var hk in moduleKeys)
                    AllRows.Add(new HotKeyRowViewModel(hk, executorBound));
            }

            RefreshConflictsAndPrefixes();
            ApplyFilter();
        }

        private void RefreshPrefixRows()
        {
            PrefixRows.Clear();

            var reservedGestures = _hotKeyService.GetReservedPrefixes();
            var userPrefixes = _hotKeyService.GetUserPrefixes();
            var userGestures = userPrefixes.Select(p => p.Gesture).ToList();

            foreach (var gesture in reservedGestures)
            {
                bool isUserDefined = userGestures.Any(g =>
                    g.Key == gesture.Key && g.KeyModifiers == gesture.KeyModifiers);

                if (!isUserDefined)
                {
                    var row = new PrefixRowViewModel(gesture, string.Empty, isAutoDerived: true);
                    row.RefreshUsedBy(GetUsedByDisplayNames(gesture));
                    PrefixRows.Add(row);
                }
            }

            foreach (var prefix in userPrefixes)
            {
                var row = new PrefixRowViewModel(prefix.Gesture, prefix.Comment, isAutoDerived: false);
                row.RefreshUsedBy(GetUsedByDisplayNames(prefix.Gesture));
                PrefixRows.Add(row);
            }
        }

        private System.Collections.Generic.List<string> GetUsedByDisplayNames(KeyGesture gesture)
        {
            var ids = _hotKeyService.GetHotKeysUsingPrefix(gesture);
            return ids.Select(id =>
            {
                var hk = _hotKeyService.GetHotKey(id);
                if (hk == null) return id;
                var localized = Strings.ResourceManager.GetString(hk.DisplayNameKey);
                return string.IsNullOrEmpty(localized) ? hk.DisplayNameKey : localized;
            }).ToList();
        }

        // -------------------------------------------------------------------
        // Управление префиксами
        // -------------------------------------------------------------------

        private void AddPrefix()
        {
            CancelEditPrefix();
            var newRow = new PrefixRowViewModel(null, string.Empty, isAutoDerived: false);
            newRow.IsEditingGesture = true;
            PrefixRows.Add(newRow);
            _editingPrefixRow = newRow;
            _prefixCurrentModifiers = KeyModifiers.None;
            UpdatePrefixLiveDisplay();
            IsLiveInputActive = true;
            EditingStarted?.Invoke();
        }

        private void RemovePrefix(PrefixRowViewModel row)
        {
            if (row.IsAutoDerived || row.Gesture == null) return;
            var result = _hotKeyService.UnregisterPrefix(row.Gesture);
            if (result == GestureAssignResult.Ok)
                PrefixRows.Remove(row);
            else
                row.ErrorMessage = ResolveGestureAssignError(result);
        }

        private void StartEditPrefixGesture(PrefixRowViewModel row)
        {
            if (row.IsAutoDerived || row.HasError) return;
            CancelEditPrefix();
            CancelEditBinding();
            row.ErrorMessage = string.Empty;
            row.IsEditingGesture = true;
            _editingPrefixRow = row;
            _prefixCurrentModifiers = KeyModifiers.None;
            UpdatePrefixLiveDisplay();
            IsLiveInputActive = true;
            EditingStarted?.Invoke();
        }

        private void SavePrefixComment(PrefixRowViewModel row)
        {
            if (row.Gesture == null || row.IsAutoDerived) return;
            _hotKeyService.UpdatePrefixComment(row.Gesture, row.Comment);
            row.IsEditingComment = false;
        }

        private void CancelEditPrefix()
        {
            if (_editingPrefixRow == null) return;
            var row = _editingPrefixRow;
            row.IsEditingGesture = false;
            _editingPrefixRow = null;
            _prefixCurrentModifiers = KeyModifiers.None;
            if (row.Gesture == null) PrefixRows.Remove(row);
            if (_editingBinding == null) { IsLiveInputActive = false; LiveInputDisplay = string.Empty; }
        }

        // -------------------------------------------------------------------
        // Управление биндингами
        // -------------------------------------------------------------------

        private void StartEditBinding(HotKeyBindingViewModel binding)
        {
            CancelEditBinding();
            CancelEditPrefix();
            _editingRow = binding.ParentRow;
            _editingBinding = binding;
            _currentModifiers = KeyModifiers.None;
            binding.IsEditing = true;
            UpdateBindingLiveDisplay();
            IsLiveInputActive = true;
            EditingStarted?.Invoke();
        }

        private void AddBinding(HotKeyRowViewModel row)
        {
            CancelEditBinding();
            CancelEditPrefix();
            var hotKey = _hotKeyService.GetHotKey(row.Id);
            if (hotKey == null) return;

            int newIndex = hotKey.CustomGestures.Count > 0
                ? hotKey.CustomGestures.Count
                : hotKey.DefaultGestures.Count;

            var newBinding = new HotKeyBindingViewModel(newIndex, false, Strings.HotKey_PressKey);
            newBinding.ParentRow = row;
            row.Bindings.Add(newBinding);

            _editingRow = row;
            _editingBinding = newBinding;
            _currentModifiers = KeyModifiers.None;
            newBinding.IsEditing = true;
            UpdateBindingLiveDisplay();
            IsLiveInputActive = true;
            EditingStarted?.Invoke();
        }

        private void CommitNewBinding(HotKeyRowViewModel row, HotKeyGesture gesture)
        {
            var result = _hotKeyService.AddCustomGesture(row.Id, gesture);
            if (result)
            {
                var updatedHotKey = _hotKeyService.GetHotKey(row.Id);
                if (updatedHotKey != null) row.RebuildBindings(updatedHotKey);
            }
        }

        private void RemoveBinding(HotKeyBindingViewModel binding)
        {
            if (!binding.IsCustom) return;
            var row = binding.ParentRow;
            var hotKey = _hotKeyService.GetHotKey(row.Id);
            if (hotKey == null) return;

            if (hotKey.CustomGestures.Count <= 1) { ResetRow(row); return; }

            _hotKeyService.RemoveCustomGesture(row.Id, binding.Index);
            var updatedHotKey = _hotKeyService.GetHotKey(row.Id);
            if (updatedHotKey != null) row.RebuildBindings(updatedHotKey);
            RefreshConflictsAndPrefixes();
        }

        private void CancelEditBinding()
        {
            if (_editingBinding != null)
            {
                _editingBinding.IsEditing = false;
                if (_editingRow != null)
                {
                    var hotKey = _hotKeyService.GetHotKey(_editingRow.Id);
                    if (hotKey != null) _editingRow.RebuildBindings(hotKey);
                }
            }

            _editingBinding = null;
            _editingRow = null;
            _currentModifiers = KeyModifiers.None;

            if (_editingPrefixRow == null) { LiveInputDisplay = string.Empty; IsLiveInputActive = false; }
        }

        private void ResetRow(HotKeyRowViewModel row)
        {
            _hotKeyService.ResetToDefault(row.Id);
            var hotKey = _hotKeyService.GetHotKey(row.Id);
            if (hotKey != null) row.RebuildBindings(hotKey);
            RefreshConflictsAndPrefixes();
        }

        private void ResetAll()
        {
            _hotKeyService.ResetAllToDefaults();
            LoadAll();
        }

        // -------------------------------------------------------------------
        // Навигация
        // -------------------------------------------------------------------

        private void SelectSection(HotKeySectionItem section)
        {
            foreach (var s in Sections)
            {
                s.IsSelected = false;
                foreach (var sub in s.SubSections) sub.IsSelected = false;
                if (s != section) s.IsExpanded = false;
            }
            section.IsSelected = true;
            if (!section.IsExpanded) section.IsExpanded = true;
            SelectedSection = section;
            SelectedSubSection = null;
            ApplyFilter();
        }

        private void SelectSubSection(HotKeySubSectionItem subSection)
        {
            foreach (var s in Sections)
            {
                s.IsSelected = false;
                foreach (var sub in s.SubSections) sub.IsSelected = false;
            }
            subSection.IsSelected = true;
            SelectedSubSection = subSection;
            var parentSection = Sections.FirstOrDefault(s => s.SubSections.Contains(subSection));
            if (parentSection != null) SelectedSection = parentSection;
            ApplyFilter();
        }

        // -------------------------------------------------------------------
        // Вспомогательные методы
        // -------------------------------------------------------------------

        private static string ResolveCategoryTitle(HotKeyCategory category)
        {
            var key = $"HotKey_Category_{category}";
            var localized = Strings.ResourceManager.GetString(key);
            return string.IsNullOrEmpty(localized) ? category.ToString() : localized;
        }

        private void RefreshConflictsAndPrefixes()
        {
            foreach (var row in AllRows)
            {
                row.ConflictType = HotKeyConflictType.None;
                row.ConflictTooltip = string.Empty;
                row.IsPrefix = false;
            }

            var reservedPrefixes = _hotKeyService.GetReservedPrefixes();
            var rowList = AllRows.ToList();

            foreach (var row in rowList)
            {
                if (row.ModuleType != null)
                    row.IsExecutorBound = _hotKeyService.IsExecutorBound(row.ModuleType);

                var hotKey = _hotKeyService.GetHotKey(row.Id);
                if (hotKey == null) continue;

                if (hotKey.ActiveGesture != null &&
                    hotKey.ActiveGesture.IsSingle &&
                    reservedPrefixes.Any(p =>
                        p.Key == hotKey.ActiveGesture.FirstStep.Key &&
                        p.KeyModifiers == hotKey.ActiveGesture.FirstStep.KeyModifiers))
                {
                    row.IsPrefix = true;
                }
            }

            for (int i = 0; i < rowList.Count; i++)
            {
                for (int j = i + 1; j < rowList.Count; j++)
                {
                    var conflictType = _hotKeyService.GetConflictType(rowList[i].Id, rowList[j].Id);
                    if (conflictType == HotKeyConflictType.None) continue;

                    if (conflictType > rowList[i].ConflictType)
                    {
                        rowList[i].ConflictType = conflictType;
                        rowList[i].ConflictTooltip = BuildConflictTooltip(rowList[j], conflictType);
                    }

                    if (conflictType > rowList[j].ConflictType)
                    {
                        rowList[j].ConflictType = conflictType;
                        rowList[j].ConflictTooltip = BuildConflictTooltip(rowList[i], conflictType);
                    }
                }
            }
        }

        private string BuildConflictTooltip(HotKeyRowViewModel conflictWith, HotKeyConflictType conflictType)
        {
            var severity = conflictType == HotKeyConflictType.Critical
                ? Strings.HotKey_Conflict_Critical
                : Strings.HotKey_Conflict_Warning;
            return $"{severity}: {conflictWith.DisplayName}";
        }

        private void ApplyFilter()
        {
            FilteredRows.Clear();
            var filter = _filterText.Trim().ToLowerInvariant();
            bool hasFilter = !string.IsNullOrEmpty(filter);

            foreach (var row in AllRows)
            {
                bool sectionMatch;

                if (hasFilter) sectionMatch = true;
                else if (_selectedSubSection != null)
                    sectionMatch = row.ModuleType == _selectedSubSection.ModuleType
                        && row.Category == _selectedSubSection.Category;
                else if (_selectedSection != null)
                    sectionMatch = _selectedSection.IsGlobal
                        ? row.ModuleType == null
                        : row.ModuleType == _selectedSection.ModuleType;
                else
                    sectionMatch = false;

                if (!sectionMatch) continue;

                bool textMatch = !hasFilter
                    || row.DisplayName.ToLowerInvariant().Contains(filter)
                    || (row.Bindings.Any(b =>
                        b.GestureDisplay.ToLowerInvariant().Contains(filter) &&
                        b.GestureDisplay != Strings.HotKey_NotAssigned))
                    || (row.ModuleType?.ToLowerInvariant().Contains(filter) ?? false);

                if (textMatch) FilteredRows.Add(row);
            }
        }

        private void UpdateBindingLiveDisplay()
        {
            if (_editingBinding == null) { LiveInputDisplay = string.Empty; return; }
            LiveInputDisplay = BuildModifiersDisplay(_currentModifiers);
        }

        private void UpdatePrefixLiveDisplay()
        {
            if (_editingPrefixRow == null) { LiveInputDisplay = string.Empty; return; }
            LiveInputDisplay = BuildModifiersDisplay(_prefixCurrentModifiers);
        }

        private static string BuildModifiersDisplay(KeyModifiers modifiers)
        {
            if (modifiers == KeyModifiers.None) return Strings.HotKey_PressKey;
            var parts = new System.Collections.Generic.List<string>();
            if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            return parts.Count > 0 ? string.Join("+", parts) + " + " : Strings.HotKey_PressKey;
        }

        private static string ResolveBindingAssignError(GestureAssignResult result) => result switch
        {
            GestureAssignResult.BlockedByPrefix => Strings.HotKey_Error_BlockedByPrefix,
            GestureAssignResult.BlockedByHotKey => Strings.HotKey_Error_Conflict,
            _ => string.Empty
        };

        private static string ResolveGestureAssignError(GestureAssignResult result) => result switch
        {
            GestureAssignResult.BlockedByHotKey => Strings.HotKey_Prefix_Error_BlockedByHotKey,
            GestureAssignResult.BlockedByPrefix => Strings.HotKey_Prefix_Error_BlockedByHotKey,
            GestureAssignResult.PrefixAlreadyExists => Strings.HotKey_Prefix_Error_AlreadyExists,
            GestureAssignResult.PrefixInUse => Strings.HotKey_Prefix_Error_InUse,
            GestureAssignResult.PrefixNotRegistered => Strings.HotKey_Prefix_Error_BlockedByHotKey,
            _ => string.Empty
        };

        private void OnHotKeysChanged()
        {
            RefreshConflictsAndPrefixes();
            ApplyFilter();
            if (_activeTab == HotKeySettingsTab.Prefixes) RefreshPrefixRows();
        }
    }
}