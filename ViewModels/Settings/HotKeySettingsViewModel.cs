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
using Writersword.Src.Core.Interfaces.Services.Input;

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
    /// </summary>
    public class HotKeyBindingViewModel : ReactiveObject
    {
        private string _gestureDisplay;
        private bool _isEditing;

        /// <summary>Порядковый индекс жеста в списке CustomGestures или DefaultGestures</summary>
        public int Index { get; }

        /// <summary>Является ли жест пользовательским (иначе — дефолтный)</summary>
        public bool IsCustom { get; }

        /// <summary>
        /// Ссылка на родительскую строку.
        /// Используется в XAML командах чтобы не передавать кортеж через CommandParameter.
        /// </summary>
        public HotKeyRowViewModel ParentRow { get; internal set; } = null!;

        /// <summary>Отображаемая строка жеста</summary>
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

        public HotKeyBindingViewModel(int index, bool isCustom, string gestureDisplay)
        {
            Index = index;
            IsCustom = isCustom;
            _gestureDisplay = gestureDisplay;
        }
    }

    /// <summary>
    /// Строка в таблице горячих клавиш.
    /// Содержит список биндингов (мульти-бинд), флаги конфликтов/префиксов,
    /// выбранный префикс последовательности и временное сообщение об ошибке назначения.
    /// </summary>
    public class HotKeyRowViewModel : ReactiveObject
    {
        private HotKeyConflictType _conflictType;
        private string _conflictTooltip;
        private bool _isExecutorBound;
        private bool _isPrefix;
        private KeyGesture? _selectedPrefix;
        private bool _isPrefixPopupOpen;
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
        /// Выбранный префикс для этого хоткея.
        /// null — хоткей одиночный, без префикса.
        /// Устанавливается когда пользователь выбирает префикс из Popup.
        /// После выбора префикса ввод второй клавиши превращает жест в последовательность.
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

        /// <summary>Есть ли выбранный префикс у этого хоткея</summary>
        public bool HasPrefix => _selectedPrefix != null;

        /// <summary>
        /// Отображаемый текст кнопки префикса.
        /// "—" если префикс не выбран, иначе строка жеста префикса.
        /// </summary>
        public string PrefixDisplayText => _selectedPrefix?.ToString() ?? "—";

        /// <summary>
        /// Открыт ли Popup выбора префикса для этой строки.
        /// Только один Popup может быть открыт одновременно — управляется из VM.
        /// </summary>
        public bool IsPrefixPopupOpen
        {
            get => _isPrefixPopupOpen;
            set => this.RaiseAndSetIfChanged(ref _isPrefixPopupOpen, value);
        }

        /// <summary>
        /// Временное сообщение об ошибке назначения комбинации.
        /// Показывается под строкой и сбрасывается автоматически через 3 секунды.
        /// Пустая строка — ошибок нет.
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

            // Инициализируем SelectedPrefix из активного жеста если он является последовательностью
            if (hotKey.ActiveGesture != null && hotKey.ActiveGesture.IsSequence)
                _selectedPrefix = hotKey.ActiveGesture.FirstStep;
        }

        /// <summary>
        /// Пересобрать список биндингов из модели HotKey.
        /// Вызывается при инициализации и после каждого изменения жестов.
        /// Устанавливает ParentRow на каждый биндинг.
        /// Если жест является последовательностью — в колонке "Текущая" показывается
        /// только последний шаг, префикс отображается отдельно в колонке "Префикс".
        /// Синхронизирует SelectedPrefix с актуальным жестом.
        /// </summary>
        public void RebuildBindings(HotKey hotKey)
        {
            Bindings.Clear();

            if (hotKey.CustomGestures.Count > 0)
            {
                for (int i = 0; i < hotKey.CustomGestures.Count; i++)
                {
                    var g = hotKey.CustomGestures[i];
                    // Если последовательность — показываем только последний шаг
                    var display = g.IsSequence ? g.Steps.Last().ToString() : g.ToString();
                    var binding = new HotKeyBindingViewModel(i, true, display);
                    binding.ParentRow = this;
                    Bindings.Add(binding);
                }
            }
            else if (hotKey.DefaultGestures.Count > 0)
            {
                for (int i = 0; i < hotKey.DefaultGestures.Count; i++)
                {
                    var g = hotKey.DefaultGestures[i];
                    // Если последовательность — показываем только последний шаг
                    var display = g.IsSequence ? g.Steps.Last().ToString() : g.ToString();
                    var binding = new HotKeyBindingViewModel(i, false, display);
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

            // Синхронизируем SelectedPrefix с актуальным активным жестом
            var newPrefix = (hotKey.ActiveGesture?.IsSequence == true)
                ? hotKey.ActiveGesture.FirstStep
                : null;

            bool prefixChanged =
                newPrefix?.Key != _selectedPrefix?.Key ||
                newPrefix?.KeyModifiers != _selectedPrefix?.KeyModifiers;

            if (prefixChanged)
            {
                _selectedPrefix = newPrefix;
                this.RaisePropertyChanged(nameof(SelectedPrefix));
                this.RaisePropertyChanged(nameof(HasPrefix));
                this.RaisePropertyChanged(nameof(PrefixDisplayText));
            }

            this.RaisePropertyChanged(nameof(HasCustomBindings));
        }
    }

    /// <summary>
    /// Подсекция навигации — категория внутри секции (File, Tools и т.д.).
    /// Отображается с отступом под родительской секцией.
    /// </summary>
    public class HotKeySubSectionItem : ReactiveObject
    {
        private bool _isSelected;

        /// <summary>Отображаемое название подсекции</summary>
        public string Title { get; }

        /// <summary>Категория для фильтрации строк</summary>
        public HotKeyCategory Category { get; }

        /// <summary>Тип модуля которому принадлежит подсекция (null для глобальных)</summary>
        public string? ModuleType { get; }

        /// <summary>Выбрана ли подсекция в навигации</summary>
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
    /// Может содержать подсекции по категориям если их больше одной.
    /// </summary>
    public class HotKeySectionItem : ReactiveObject
    {
        private bool _isSelected;
        private bool _isExpanded;

        /// <summary>Отображаемое название секции</summary>
        public string Title { get; }

        /// <summary>
        /// Тип модуля для фильтрации строк.
        /// null — секция глобальных клавиш.
        /// </summary>
        public string? ModuleType { get; }

        /// <summary>Является ли секция глобальной (не модульной)</summary>
        public bool IsGlobal => ModuleType == null;

        /// <summary>
        /// Подсекции по категориям.
        /// Пустой список — у секции нет подсекций, кликабельна напрямую.
        /// </summary>
        public ObservableCollection<HotKeySubSectionItem> SubSections { get; } = new();

        /// <summary>Есть ли подсекции</summary>
        public bool HasSubSections => SubSections.Count > 0;

        /// <summary>Выбрана ли секция напрямую (без подсекций)</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        /// <summary>Развёрнута ли секция (показывает подсекции)</summary>
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
    /// Содержит жест, редактируемый комментарий и список хоткеев использующих этот префикс.
    /// </summary>
    public class PrefixRowViewModel : ReactiveObject
    {
        private string _comment;
        private bool _isEditingGesture;
        private bool _isEditingComment;
        private string _errorMessage;
        private string _gestureDisplay;

        /// <summary>
        /// Жест префикса.
        /// null только пока пользователь вводит новый префикс и ещё не нажал клавишу.
        /// </summary>
        public KeyGesture? Gesture { get; private set; }

        /// <summary>Отображаемая строка жеста</summary>
        public string GestureDisplay
        {
            get => _gestureDisplay;
            private set => this.RaiseAndSetIfChanged(ref _gestureDisplay, value);
        }

        /// <summary>
        /// Пользовательский комментарий.
        /// Редактируется inline прямо в таблице.
        /// </summary>
        public string Comment
        {
            get => _comment;
            set => this.RaiseAndSetIfChanged(ref _comment, value);
        }

        /// <summary>
        /// Список отображаемых имён хоткеев использующих этот префикс.
        /// Пустой список — показывается HotKey_Prefix_UsedByNone.
        /// </summary>
        public ObservableCollection<string> UsedByDisplayNames { get; } = new();

        /// <summary>
        /// Строка для колонки "Используется".
        /// Формируется из UsedByDisplayNames через запятую.
        /// </summary>
        public string UsedByText => UsedByDisplayNames.Count > 0
            ? string.Join(", ", UsedByDisplayNames)
            : Strings.HotKey_Prefix_UsedByNone;

        /// <summary>Ожидает ли строка нажатия клавиши для назначения жеста</summary>
        public bool IsEditingGesture
        {
            get => _isEditingGesture;
            set => this.RaiseAndSetIfChanged(ref _isEditingGesture, value);
        }

        /// <summary>Редактируется ли комментарий прямо сейчас</summary>
        public bool IsEditingComment
        {
            get => _isEditingComment;
            set => this.RaiseAndSetIfChanged(ref _isEditingComment, value);
        }

        /// <summary>
        /// Сообщение об ошибке последней операции.
        /// Пустая строка — ошибок нет.
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

        /// <summary>
        /// Является ли префикс автоматически выведенным из дефолтных последовательностей.
        /// Такие префиксы нельзя удалить — они управляются дефолтными жестами хоткеев.
        /// </summary>
        public bool IsAutoDerived { get; }

        /// <summary>Можно ли удалить префикс — только пользовательские и не используемые</summary>
        public bool CanRemove => !IsAutoDerived && UsedByDisplayNames.Count == 0;

        public PrefixRowViewModel(KeyGesture? gesture, string comment, bool isAutoDerived)
        {
            Gesture = gesture;
            _comment = comment;
            _errorMessage = string.Empty;
            IsAutoDerived = isAutoDerived;
            _gestureDisplay = gesture?.ToString() ?? Strings.HotKey_Prefix_PressKey;
        }

        /// <summary>
        /// Обновить жест после успешного назначения.
        /// Сбрасывает режим редактирования и ошибку.
        /// </summary>
        public void ApplyGesture(KeyGesture gesture)
        {
            Gesture = gesture;
            GestureDisplay = gesture.ToString();
            IsEditingGesture = false;
            ErrorMessage = string.Empty;
        }

        /// <summary>
        /// Обновить список хоткеев использующих этот префикс.
        /// Вызывается после любого изменения в сервисе.
        /// </summary>
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
    /// Пункт в Popup выбора префикса для хоткея.
    /// Содержит жест для отображения и флаг "убрать префикс".
    /// </summary>
    public class PrefixPopupItem
    {
        /// <summary>
        /// Жест префикса.
        /// null означает специальный пункт "Убрать префикс".
        /// </summary>
        public KeyGesture? Gesture { get; }

        /// <summary>Отображаемый текст пункта в Popup</summary>
        public string DisplayText { get; }

        /// <summary>Является ли этот пункт специальным действием "Убрать префикс"</summary>
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
    /// Привязки — левая навигация по секциям, правая — таблица хоткеев с колонками
    /// Действие / По умолчанию / Префикс / Текущая / Сброс.
    /// Префиксы — таблица зарегистрированных префиксов с комментариями.
    /// </summary>
    public class HotKeySettingsViewModel : ReactiveObject
    {
        private readonly ILogger<HotKeySettingsViewModel> _logger;
        private readonly IHotKeyService _hotKeyService;

        // -------------------------------------------------------------------
        // Состояние вкладок
        // -------------------------------------------------------------------

        private HotKeySettingsTab _activeTab = HotKeySettingsTab.Bindings;

        /// <summary>Активная внутренняя вкладка</summary>
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

        /// <summary>Активна ли вкладка Привязки</summary>
        public bool IsBindingsTabActive => _activeTab == HotKeySettingsTab.Bindings;

        /// <summary>Активна ли вкладка Префиксы</summary>
        public bool IsPrefixesTabActive => _activeTab == HotKeySettingsTab.Prefixes;

        // -------------------------------------------------------------------
        // Состояние редактирования биндингов
        // -------------------------------------------------------------------

        private HotKeyBindingViewModel? _editingBinding;
        private HotKeyRowViewModel? _editingRow;
        private string _filterText = string.Empty;
        private HotKeySectionItem? _selectedSection;
        private HotKeySubSectionItem? _selectedSubSection;
        private string _liveInputDisplay = string.Empty;
        private bool _isLiveInputActive;
        private KeyModifiers _currentModifiers = KeyModifiers.None;

        // -------------------------------------------------------------------
        // Состояние редактирования префиксов
        // -------------------------------------------------------------------

        private PrefixRowViewModel? _editingPrefixRow;
        private KeyModifiers _prefixCurrentModifiers = KeyModifiers.None;

        // -------------------------------------------------------------------
        // Коллекции
        // -------------------------------------------------------------------

        /// <summary>Секции левой навигационной панели</summary>
        public ObservableCollection<HotKeySectionItem> Sections { get; } = new();

        /// <summary>Все строки всех секций</summary>
        public ObservableCollection<HotKeyRowViewModel> AllRows { get; } = new();

        /// <summary>Строки текущей секции/подсекции с учётом фильтра</summary>
        public ObservableCollection<HotKeyRowViewModel> FilteredRows { get; } = new();

        /// <summary>Строки таблицы префиксов</summary>
        public ObservableCollection<PrefixRowViewModel> PrefixRows { get; } = new();

        /// <summary>
        /// Пункты Popup выбора префикса для текущей открытой строки.
        /// Пересобирается при открытии Popup через TogglePrefixPopupCommand.
        /// </summary>
        public ObservableCollection<PrefixPopupItem> PrefixPopupItems { get; } = new();

        // -------------------------------------------------------------------
        // Свойства
        // -------------------------------------------------------------------

        /// <summary>Выбранная секция верхнего уровня</summary>
        public HotKeySectionItem? SelectedSection
        {
            get => _selectedSection;
            private set => this.RaiseAndSetIfChanged(ref _selectedSection, value);
        }

        /// <summary>Выбранная подсекция (null если выбрана вся секция)</summary>
        public HotKeySubSectionItem? SelectedSubSection
        {
            get => _selectedSubSection;
            private set => this.RaiseAndSetIfChanged(ref _selectedSubSection, value);
        }

        /// <summary>Текст фильтра для поиска по DisplayName и жестам</summary>
        public string FilterText
        {
            get => _filterText;
            set
            {
                this.RaiseAndSetIfChanged(ref _filterText, value);
                ApplyFilter();
            }
        }

        /// <summary>
        /// Строка отображающая текущее состояние ввода в режиме редактирования.
        /// Например: "Ctrl + " пока пользователь удерживает модификаторы.
        /// </summary>
        public string LiveInputDisplay
        {
            get => _liveInputDisplay;
            set => this.RaiseAndSetIfChanged(ref _liveInputDisplay, value);
        }

        /// <summary>Активен ли режим live input прямо сейчас</summary>
        public bool IsLiveInputActive
        {
            get => _isLiveInputActive;
            set => this.RaiseAndSetIfChanged(ref _isLiveInputActive, value);
        }


        private string _prefixTabErrorMessage = string.Empty;

        /// <summary>
        /// Временное сообщение об ошибке на уровне вкладки Префиксы.
        /// Показывается над таблицей и сбрасывается автоматически через 3 секунды.
        /// </summary>
        public string PrefixTabErrorMessage
        {
            get => _prefixTabErrorMessage;
            set
            {
                this.RaiseAndSetIfChanged(ref _prefixTabErrorMessage, value);
                this.RaisePropertyChanged(nameof(HasPrefixTabError));
            }
        }

        /// <summary>Есть ли ошибка уровня вкладки для отображения</summary>
        public bool HasPrefixTabError => !string.IsNullOrEmpty(_prefixTabErrorMessage);

        /// <summary>
        /// Активно ли редактирование биндинга или префикса прямо сейчас.
        /// Используется в code-behind чтобы не перехватывать клавиши вне режима редактирования.
        /// </summary>
        public bool IsEditingActive => _editingBinding != null || _editingPrefixRow != null;

        // -------------------------------------------------------------------
        // Команды биндингов
        // -------------------------------------------------------------------

        /// <summary>Команда сброса одной клавиши к дефолту</summary>
        public ReactiveCommand<HotKeyRowViewModel, Unit> ResetRowCommand { get; }

        /// <summary>Команда сброса всех клавиш к дефолту</summary>
        public ReactiveCommand<Unit, Unit> ResetAllCommand { get; }

        /// <summary>
        /// Команда начала редактирования конкретного биндинга.
        /// Принимает HotKeyBindingViewModel — ParentRow достаётся из него.
        /// </summary>
        public ReactiveCommand<HotKeyBindingViewModel, Unit> StartEditBindingCommand { get; }

        /// <summary>Команда добавления нового биндинга к строке</summary>
        public ReactiveCommand<HotKeyRowViewModel, Unit> AddBindingCommand { get; }

        /// <summary>
        /// Команда удаления биндинга.
        /// Принимает HotKeyBindingViewModel — ParentRow достаётся из него.
        /// </summary>
        public ReactiveCommand<HotKeyBindingViewModel, Unit> RemoveBindingCommand { get; }

        /// <summary>Команда отмены редактирования биндинга</summary>
        public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }

        /// <summary>Команда выбора секции верхнего уровня</summary>
        public ReactiveCommand<HotKeySectionItem, Unit> SelectSectionCommand { get; }

        /// <summary>Команда выбора подсекции</summary>
        public ReactiveCommand<HotKeySubSectionItem, Unit> SelectSubSectionCommand { get; }

        // -------------------------------------------------------------------
        // Команды вкладок
        // -------------------------------------------------------------------

        /// <summary>Команда переключения на вкладку Привязки</summary>
        public ReactiveCommand<Unit, Unit> ShowBindingsTabCommand { get; }

        /// <summary>Команда переключения на вкладку Префиксы</summary>
        public ReactiveCommand<Unit, Unit> ShowPrefixesTabCommand { get; }

        // -------------------------------------------------------------------
        // Команды префиксов в таблице хоткеев
        // -------------------------------------------------------------------

        /// <summary>
        /// Команда открытия/закрытия Popup выбора префикса для строки хоткея.
        /// Закрывает все остальные открытые Popup перед открытием нового.
        /// Пересобирает PrefixPopupItems с учётом текущего состояния строки.
        /// Если зарегистрированных префиксов нет — Popup не открывается.
        /// </summary>
        public ReactiveCommand<HotKeyRowViewModel, Unit> TogglePrefixPopupCommand { get; }

        /// <summary>
        /// Команда выбора префикса из Popup для конкретной строки хоткея.
        /// Если IsRemoveAction — убирает префикс и делает жест одиночным.
        /// Иначе — немедленно применяет префикс к существующему жесту.
        /// Если биндинга нет совсем — автоматически запускает редактирование
        /// чтобы пользователь мог сразу нажать вторую клавишу.
        /// </summary>
        public ReactiveCommand<PrefixPopupItem, Unit> SelectPrefixForRowCommand { get; }

        // -------------------------------------------------------------------
        // Команды управления префиксами во вкладке Префиксы
        // -------------------------------------------------------------------

        /// <summary>
        /// Команда начала добавления нового префикса.
        /// Добавляет пустую строку в PrefixRows и переводит её в режим ввода жеста.
        /// </summary>
        public ReactiveCommand<Unit, Unit> AddPrefixCommand { get; }

        /// <summary>
        /// Команда удаления префикса.
        /// Блокируется если префикс используется хоткеями или является автоматическим.
        /// </summary>
        public ReactiveCommand<PrefixRowViewModel, Unit> RemovePrefixCommand { get; }

        /// <summary>
        /// Команда начала редактирования жеста существующего префикса.
        /// Переводит строку в режим ожидания нажатия клавиши.
        /// </summary>
        public ReactiveCommand<PrefixRowViewModel, Unit> StartEditPrefixGestureCommand { get; }

        /// <summary>
        /// Команда сохранения комментария префикса.
        /// Вызывается когда поле комментария теряет фокус (через code-behind).
        /// </summary>
        public ReactiveCommand<PrefixRowViewModel, Unit> SavePrefixCommentCommand { get; }

        /// <summary>Команда отмены редактирования жеста префикса</summary>
        public ReactiveCommand<Unit, Unit> CancelEditPrefixCommand { get; }

        /// <summary>
        /// Команда перевода комментария префикса в режим редактирования.
        /// Вызывается при клике на поле комментария.
        /// </summary>
        public ReactiveCommand<PrefixRowViewModel, Unit> StartEditCommentCommand { get; }

        // -------------------------------------------------------------------
        // События
        // -------------------------------------------------------------------

        /// <summary>
        /// Событие срабатывает когда биндинг или префикс переходит в режим редактирования.
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

            TogglePrefixPopupCommand = ReactiveCommand.Create<HotKeyRowViewModel>(TogglePrefixPopup);
            SelectPrefixForRowCommand = ReactiveCommand.Create<PrefixPopupItem>(SelectPrefixForRow);

            AddPrefixCommand = ReactiveCommand.Create(AddPrefix);
            RemovePrefixCommand = ReactiveCommand.Create<PrefixRowViewModel>(RemovePrefix);
            StartEditPrefixGestureCommand = ReactiveCommand.Create<PrefixRowViewModel>(StartEditPrefixGesture);
            SavePrefixCommentCommand = ReactiveCommand.Create<PrefixRowViewModel>(SavePrefixComment);
            CancelEditPrefixCommand = ReactiveCommand.Create(CancelEditPrefix);

            StartEditCommentCommand = ReactiveCommand.Create<PrefixRowViewModel>(row =>
            {
                if (!row.IsAutoDerived)
                    row.IsEditingComment = true;
            });

            LoadAll();

            _hotKeyService.HotKeysChanged += OnHotKeysChanged;
        }

        // -------------------------------------------------------------------
        // Публичные методы для code-behind (перехват клавиш)
        // -------------------------------------------------------------------

        /// <summary>
        /// Обработать нажатие клавиши в режиме редактирования.
        /// Вызывается из View при KeyDown только когда IsEditingActive == true.
        /// Маршрутизирует в HandleBindingKeyDown или HandlePrefixKeyDown в зависимости
        /// от того что сейчас редактируется.
        /// </summary>
        public void HandleKeyDown(Key key, KeyModifiers modifiers)
        {
            if (_editingPrefixRow != null)
                HandlePrefixKeyDown(key, modifiers);
            else if (_editingBinding != null)
                HandleBindingKeyDown(key, modifiers);
        }

        /// <summary>
        /// Обработать отпускание клавиши.
        /// Вызывается из View при KeyUp только когда IsEditingActive == true.
        /// </summary>
        public void HandleKeyUp(Key key, KeyModifiers modifiers)
        {
            if (_editingPrefixRow != null)
                HandlePrefixKeyUp(key, modifiers);
            else if (_editingBinding != null)
                HandleBindingKeyUp(key, modifiers);
        }

        // -------------------------------------------------------------------
        // Обработка клавиш для биндингов
        // -------------------------------------------------------------------

        /// <summary>
        /// Обработать нажатие клавиши в режиме редактирования биндинга.
        /// Одиночные модификаторы обновляют LiveInputDisplay но не завершают ввод.
        /// Escape — отменить редактирование.
        /// Delete/Backspace — сбросить строку к дефолту.
        /// Win/Meta клавиши игнорируются полностью.
        /// Если у строки выбран префикс — фиксирует жест как последовательность prefix -> key.
        /// При ошибке назначения — показывает временное сообщение под строкой на 3 секунды.
        /// </summary>
        private void HandleBindingKeyDown(Key key, KeyModifiers modifiers)
        {
            if (_editingBinding == null || _editingRow == null) return;

            bool isModifierOnly = key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin;

            bool isWinKey = key is Key.LWin or Key.RWin;

            if (isModifierOnly)
            {
                if (!isWinKey)
                {
                    _currentModifiers = modifiers;
                    UpdateBindingLiveDisplay();
                }
                return;
            }

            if (key == Key.Escape)
            {
                CancelEditBinding();
                return;
            }

            if (key is Key.Delete or Key.Back)
            {
                var rowToReset = _editingRow;
                CancelEditBinding();
                ResetRow(rowToReset);
                return;
            }

            var cleanModifiers = modifiers & ~KeyModifiers.Meta;
            var gesture = new KeyGesture(key, cleanModifiers);

            // Если у строки выбран префикс — строим последовательность prefix -> gesture
            HotKeyGesture hotKeyGesture;
            if (_editingRow.SelectedPrefix != null)
                hotKeyGesture = new HotKeyGesture(
                    new System.Collections.Generic.List<KeyGesture> { _editingRow.SelectedPrefix, gesture });
            else
                hotKeyGesture = new HotKeyGesture(gesture);

            var row = _editingRow;
            var binding = _editingBinding;
            bool isNewBinding = !binding.IsCustom && binding.GestureDisplay == Strings.HotKey_PressKey;
            CancelEditBinding();

            if (isNewBinding)
            {
                // Для нового биндинга используем CommitNewBinding — он добавляет через AddCustomGesture
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
                if (updatedHotKey != null)
                    row.RebuildBindings(updatedHotKey);
            }
            else
            {
                // Показываем временную ошибку под строкой и сбрасываем через 3 секунды
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

        /// <summary>
        /// Обработать отпускание клавиши в режиме редактирования биндинга.
        /// Win/Meta клавиши игнорируются.
        /// </summary>
        private void HandleBindingKeyUp(Key key, KeyModifiers modifiers)
        {
            if (_editingBinding == null) return;

            bool isWinKey = key is Key.LWin or Key.RWin;
            if (isWinKey) return;

            _currentModifiers = modifiers & ~KeyModifiers.Meta;
            UpdateBindingLiveDisplay();
        }

        // -------------------------------------------------------------------
        // Обработка клавиш для префиксов
        // -------------------------------------------------------------------

        /// <summary>
        /// Обработать нажатие клавиши в режиме редактирования жеста префикса.
        /// Escape — отменить. Win/Meta — игнорировать.
        /// Одиночные модификаторы — обновить live display.
        /// Любая другая клавиша — зафиксировать жест префикса.
        /// При неудаче регистрации нового — удаляет строку-заглушку.
        /// При неудаче замены — откатывает к старому префиксу.
        /// </summary>
        private void HandlePrefixKeyDown(Key key, KeyModifiers modifiers)
        {
            if (_editingPrefixRow == null) return;

            bool isModifierOnly = key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin;

            bool isWinKey = key is Key.LWin or Key.RWin;

            if (isModifierOnly)
            {
                if (!isWinKey)
                {
                    _prefixCurrentModifiers = modifiers;
                    UpdatePrefixLiveDisplay();
                }
                return;
            }

            if (key == Key.Escape)
            {
                CancelEditPrefix();
                return;
            }

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

        /// <summary>
        /// Обработать отпускание клавиши в режиме редактирования жеста префикса.
        /// Win/Meta клавиши игнорируются.
        /// </summary>
        private void HandlePrefixKeyUp(Key key, KeyModifiers modifiers)
        {
            if (_editingPrefixRow == null) return;

            bool isWinKey = key is Key.LWin or Key.RWin;
            if (isWinKey) return;

            _prefixCurrentModifiers = modifiers & ~KeyModifiers.Meta;
            UpdatePrefixLiveDisplay();
        }

        // -------------------------------------------------------------------
        // Popup выбора префикса для хоткея
        // -------------------------------------------------------------------

        /// <summary>
        /// Открыть или закрыть Popup выбора префикса для строки хоткея.
        /// Закрывает все остальные открытые Popup.
        /// Если открывается — пересобирает PrefixPopupItems для этой строки.
        /// Первый пункт "Убрать префикс" добавляется только если у строки уже есть префикс.
        /// Если зарегистрированных префиксов нет совсем — Popup не открывается.
        /// </summary>
        private void TogglePrefixPopup(HotKeyRowViewModel row)
        {
            bool wasOpen = row.IsPrefixPopupOpen;

            // Закрываем все открытые Popup
            foreach (var r in AllRows)
                r.IsPrefixPopupOpen = false;

            if (wasOpen) return;

            // Пересобираем список пунктов
            PrefixPopupItems.Clear();

            if (row.HasPrefix)
            {
                PrefixPopupItems.Add(new PrefixPopupItem(
                    null,
                    Strings.HotKey_Prefix_Remove,
                    isRemoveAction: true));
            }

            var reservedPrefixes = _hotKeyService.GetReservedPrefixes();
            foreach (var gesture in reservedPrefixes)
                PrefixPopupItems.Add(new PrefixPopupItem(gesture, gesture.ToString()));

            // Нет доступных префиксов для выбора — не открываем Popup
            bool hasSelectableItems = reservedPrefixes.Any();
            if (!hasSelectableItems)
            {
                _logger.LogDebug("No prefixes registered, Popup not opened for row: {Id}", row.Id);
                return;
            }

            row.IsPrefixPopupOpen = true;
        }

        /// <summary>
        /// Выбрать префикс из Popup для текущей строки хоткея.
        /// Если IsRemoveAction — убирает префикс и конвертирует жест в одиночный
        /// (оставляет только последний шаг последовательности).
        /// Иначе — немедленно применяет выбранный префикс к существующему жесту строки.
        /// Если биндинга нет совсем — автоматически запускает редактирование биндинга
        /// чтобы пользователь мог сразу нажать вторую клавишу последовательности.
        /// </summary>
        private void SelectPrefixForRow(PrefixPopupItem item)
        {
            var row = AllRows.FirstOrDefault(r => r.IsPrefixPopupOpen);
            if (row == null) return;

            row.IsPrefixPopupOpen = false;

            var hotKey = _hotKeyService.GetHotKey(row.Id);
            if (hotKey == null) return;

            if (item.IsRemoveAction)
            {
                row.SelectedPrefix = null;

                // Убираем префикс — оставляем только последний шаг как одиночный жест
                var gestures = hotKey.CustomGestures.Count > 0
                    ? hotKey.CustomGestures
                    : hotKey.DefaultGestures;

                if (gestures.Count > 0)
                {
                    var firstGesture = gestures[0];
                    var secondStep = firstGesture.IsSequence
                        ? firstGesture.Steps.Last()
                        : firstGesture.FirstStep;

                    var singleGesture = new HotKeyGesture(secondStep);
                    _hotKeyService.SetCustomGestureSequence(row.Id, singleGesture);
                }

                var updated = _hotKeyService.GetHotKey(row.Id);
                if (updated != null)
                    row.RebuildBindings(updated);
            }
            else
            {
                row.SelectedPrefix = item.Gesture;

                var gestures = hotKey.CustomGestures.Count > 0
                    ? hotKey.CustomGestures
                    : hotKey.DefaultGestures;

                if (gestures.Count > 0)
                {
                    // Применяем префикс к первому существующему жесту немедленно
                    var existingGesture = gestures[0];
                    var secondStep = existingGesture.IsSequence
                        ? existingGesture.Steps.Last()
                        : existingGesture.FirstStep;

                    var sequence = new HotKeyGesture(
                        new System.Collections.Generic.List<KeyGesture> { item.Gesture!, secondStep });

                    _hotKeyService.SetCustomGestureSequence(row.Id, sequence);

                    var updated = _hotKeyService.GetHotKey(row.Id);
                    if (updated != null)
                        row.RebuildBindings(updated);
                }
                else
                {
                    // Биндинга нет совсем — автоматически запускаем ввод второй клавиши
                    // Пользователь выбрал префикс и сразу может нажать вторую клавишу
                    var newBinding = new HotKeyBindingViewModel(0, false, Strings.HotKey_PressKey);
                    newBinding.ParentRow = row;
                    row.Bindings.Clear();
                    row.Bindings.Add(newBinding);

                    _editingRow = row;
                    _editingBinding = newBinding;
                    _currentModifiers = KeyModifiers.None;
                    newBinding.IsEditing = true;

                    UpdateBindingLiveDisplay();
                    IsLiveInputActive = true;
                    EditingStarted?.Invoke();

                    // Не вызываем RefreshConflictsAndPrefixes — биндинг ещё не зафиксирован
                    return;
                }
            }

            RefreshConflictsAndPrefixes();
        }

        // -------------------------------------------------------------------
        // Загрузка данных
        // -------------------------------------------------------------------

        /// <summary>
        /// Загрузить все секции и строки из HotKeyService.
        /// Строит подсекции автоматически из HotKeyCategory если их больше одной.
        /// Вызывается при инициализации и при ResetAll.
        /// По умолчанию ничего не выбрано — FilteredRows пустой до выбора секции.
        /// </summary>
        private void LoadAll()
        {
            Sections.Clear();
            AllRows.Clear();

            var hotKeys = _hotKeyService.GetAllHotKeys();

            var globalKeys = hotKeys
                .Where(hk => hk.ModuleType == null)
                .OrderBy(hk => hk.Category)
                .ThenBy(hk => hk.Id)
                .ToList();

            if (globalKeys.Count > 0)
            {
                var globalSection = new HotKeySectionItem(Strings.HotKey_Section_Global, null);

                var globalCategories = globalKeys
                    .Select(hk => hk.Category)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();

                if (globalCategories.Count > 1)
                {
                    foreach (var category in globalCategories)
                    {
                        var categoryTitle = ResolveCategoryTitle(category);
                        globalSection.SubSections.Add(
                            new HotKeySubSectionItem(categoryTitle, category, null));
                    }
                }

                Sections.Add(globalSection);

                foreach (var hk in globalKeys)
                    AllRows.Add(new HotKeyRowViewModel(hk, true));
            }

            var moduleTypes = hotKeys
                .Where(hk => hk.ModuleType != null)
                .Select(hk => hk.ModuleType!)
                .Distinct()
                .OrderBy(mt => mt)
                .ToList();

            foreach (var moduleType in moduleTypes)
            {
                var moduleKeys = hotKeys
                    .Where(hk => hk.ModuleType == moduleType)
                    .OrderBy(hk => hk.Category)
                    .ThenBy(hk => hk.Id)
                    .ToList();

                if (moduleKeys.Count == 0) continue;

                var sectionTitle = Strings.ResourceManager.GetString(moduleType);
                var section = new HotKeySectionItem(
                    string.IsNullOrEmpty(sectionTitle) ? moduleType : sectionTitle,
                    moduleType);

                var moduleCategories = moduleKeys
                    .Select(hk => hk.Category)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();

                if (moduleCategories.Count > 1)
                {
                    foreach (var category in moduleCategories)
                    {
                        var categoryTitle = ResolveCategoryTitle(category);
                        section.SubSections.Add(
                            new HotKeySubSectionItem(categoryTitle, category, moduleType));
                    }
                }

                Sections.Add(section);

                bool executorBound = _hotKeyService.IsExecutorBound(moduleType);
                foreach (var hk in moduleKeys)
                    AllRows.Add(new HotKeyRowViewModel(hk, executorBound));
            }

            RefreshConflictsAndPrefixes();
            ApplyFilter();
        }

        /// <summary>
        /// Перестроить список строк префиксов из сервиса.
        /// Сначала идут автоматически выведенные (только для чтения),
        /// затем пользовательские.
        /// </summary>
        private void RefreshPrefixRows()
        {
            PrefixRows.Clear();

            var reservedGestures = _hotKeyService.GetReservedPrefixes();
            var userPrefixes = _hotKeyService.GetUserPrefixes();
            var userGestures = userPrefixes.Select(p => p.Gesture).ToList();

            // Автоматически выведенные — первые шаги дефолтных последовательностей
            // которых нет в пользовательском списке
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

            // Пользовательские префиксы
            foreach (var prefix in userPrefixes)
            {
                var row = new PrefixRowViewModel(prefix.Gesture, prefix.Comment, isAutoDerived: false);
                row.RefreshUsedBy(GetUsedByDisplayNames(prefix.Gesture));
                PrefixRows.Add(row);
            }
        }

        /// <summary>
        /// Получить список отображаемых имён хоткеев использующих указанный префикс.
        /// </summary>
        private System.Collections.Generic.List<string> GetUsedByDisplayNames(KeyGesture gesture)
        {
            var ids = _hotKeyService.GetHotKeysUsingPrefix(gesture);
            return ids
                .Select(id =>
                {
                    var hk = _hotKeyService.GetHotKey(id);
                    if (hk == null) return id;
                    var localized = Strings.ResourceManager.GetString(hk.DisplayNameKey);
                    return string.IsNullOrEmpty(localized) ? hk.DisplayNameKey : localized;
                })
                .ToList();
        }

        // -------------------------------------------------------------------
        // Команды управления префиксами — реализация
        // -------------------------------------------------------------------

        /// <summary>
        /// Добавить новую строку префикса и перевести её в режим ввода жеста.
        /// Строка добавляется в конец списка пользовательских префиксов.
        /// </summary>
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

        /// <summary>
        /// Удалить пользовательский префикс.
        /// Показывает ошибку в строке если удаление заблокировано сервисом.
        /// </summary>
        private void RemovePrefix(PrefixRowViewModel row)
        {
            if (row.IsAutoDerived || row.Gesture == null) return;

            var result = _hotKeyService.UnregisterPrefix(row.Gesture);
            if (result == GestureAssignResult.Ok)
                PrefixRows.Remove(row);
            else
                row.ErrorMessage = ResolveGestureAssignError(result);
        }

        /// <summary>
        /// Перевести строку префикса в режим редактирования жеста.
        /// Автоматически выведенные префиксы нельзя редактировать.
        /// </summary>
        private void StartEditPrefixGesture(PrefixRowViewModel row)
        {
            if (row.IsAutoDerived) return;
            if (row.HasError) return;

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

        /// <summary>
        /// Сохранить комментарий префикса в сервисе.
        /// Вызывается когда поле комментария теряет фокус (через code-behind).
        /// </summary>
        private void SavePrefixComment(PrefixRowViewModel row)
        {
            if (row.Gesture == null || row.IsAutoDerived) return;

            _hotKeyService.UpdatePrefixComment(row.Gesture, row.Comment);
            row.IsEditingComment = false;
        }

        /// <summary>
        /// Отменить редактирование жеста префикса.
        /// Если строка была новой (Gesture == null) — удаляет её из списка.
        /// </summary>
        private void CancelEditPrefix()
        {
            if (_editingPrefixRow == null) return;

            var row = _editingPrefixRow;
            row.IsEditingGesture = false;
            _editingPrefixRow = null;
            _prefixCurrentModifiers = KeyModifiers.None;

            if (row.Gesture == null)
                PrefixRows.Remove(row);

            if (_editingBinding == null)
            {
                IsLiveInputActive = false;
                LiveInputDisplay = string.Empty;
            }
        }

        // -------------------------------------------------------------------
        // Команды биндингов — реализация
        // -------------------------------------------------------------------

        /// <summary>
        /// Начать редактирование конкретного биндинга.
        /// Отменяет предыдущее редактирование, захватывает фокус.
        /// </summary>
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

        /// <summary>
        /// Добавить новый биндинг к строке хоткея.
        /// Добавляет плейсхолдер-запись и переводит её в режим ввода.
        /// </summary>
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

        /// <summary>
        /// Зафиксировать новый биндинг через AddCustomGesture.
        /// Вызывается из HandleBindingKeyDown когда binding был плейсхолдером.
        /// </summary>
        private void CommitNewBinding(HotKeyRowViewModel row, HotKeyGesture gesture)
        {
            var result = _hotKeyService.AddCustomGesture(row.Id, gesture);
            if (result)
            {
                var updatedHotKey = _hotKeyService.GetHotKey(row.Id);
                if (updatedHotKey != null)
                    row.RebuildBindings(updatedHotKey);
            }
        }

        /// <summary>
        /// Удалить пользовательский биндинг.
        /// Если это последний кастомный биндинг — сбрасывает строку к дефолту.
        /// </summary>
        private void RemoveBinding(HotKeyBindingViewModel binding)
        {
            if (!binding.IsCustom) return;

            var row = binding.ParentRow;
            var hotKey = _hotKeyService.GetHotKey(row.Id);
            if (hotKey == null) return;

            if (hotKey.CustomGestures.Count <= 1)
            {
                ResetRow(row);
                return;
            }

            _hotKeyService.RemoveCustomGesture(row.Id, binding.Index);

            var updatedHotKey = _hotKeyService.GetHotKey(row.Id);
            if (updatedHotKey != null)
                row.RebuildBindings(updatedHotKey);

            RefreshConflictsAndPrefixes();
        }

        /// <summary>
        /// Отменить текущее редактирование биндинга.
        /// Восстанавливает отображение строки из сервиса.
        /// </summary>
        private void CancelEditBinding()
        {
            if (_editingBinding != null)
            {
                _editingBinding.IsEditing = false;

                if (_editingRow != null)
                {
                    var hotKey = _hotKeyService.GetHotKey(_editingRow.Id);
                    if (hotKey != null)
                        _editingRow.RebuildBindings(hotKey);
                }
            }

            _editingBinding = null;
            _editingRow = null;
            _currentModifiers = KeyModifiers.None;

            if (_editingPrefixRow == null)
            {
                LiveInputDisplay = string.Empty;
                IsLiveInputActive = false;
            }
        }

        /// <summary>
        /// Сбросить строку хоткея к дефолтным жестам.
        /// </summary>
        private void ResetRow(HotKeyRowViewModel row)
        {
            _hotKeyService.ResetToDefault(row.Id);

            var hotKey = _hotKeyService.GetHotKey(row.Id);
            if (hotKey != null)
                row.RebuildBindings(hotKey);

            RefreshConflictsAndPrefixes();
        }

        /// <summary>
        /// Сбросить все хоткеи к дефолтным жестам и перезагрузить всё.
        /// </summary>
        private void ResetAll()
        {
            _hotKeyService.ResetAllToDefaults();
            LoadAll();
        }

        // -------------------------------------------------------------------
        // Навигация по секциям
        // -------------------------------------------------------------------

        /// <summary>
        /// Выбрать секцию верхнего уровня.
        /// Сворачивает все остальные секции, разворачивает выбранную.
        /// Сбрасывает выбор подсекции.
        /// </summary>
        private void SelectSection(HotKeySectionItem section)
        {
            foreach (var s in Sections)
            {
                s.IsSelected = false;
                foreach (var sub in s.SubSections)
                    sub.IsSelected = false;

                if (s != section)
                    s.IsExpanded = false;
            }

            section.IsSelected = true;

            if (!section.IsExpanded)
                section.IsExpanded = true;

            SelectedSection = section;
            SelectedSubSection = null;
            ApplyFilter();
        }

        /// <summary>
        /// Выбрать подсекцию.
        /// Родительская секция остаётся развёрнутой но не помечается как IsSelected.
        /// </summary>
        private void SelectSubSection(HotKeySubSectionItem subSection)
        {
            foreach (var s in Sections)
            {
                s.IsSelected = false;
                foreach (var sub in s.SubSections)
                    sub.IsSelected = false;
            }

            subSection.IsSelected = true;
            SelectedSubSection = subSection;

            var parentSection = Sections.FirstOrDefault(s => s.SubSections.Contains(subSection));
            if (parentSection != null)
                SelectedSection = parentSection;

            ApplyFilter();
        }

        // -------------------------------------------------------------------
        // Вспомогательные методы
        // -------------------------------------------------------------------

        /// <summary>
        /// Получить локализованное название категории.
        /// Использует ключи Strings.HotKey_Category_* если они есть, иначе enum.ToString().
        /// </summary>
        private static string ResolveCategoryTitle(HotKeyCategory category)
        {
            var key = $"HotKey_Category_{category}";
            var localized = Strings.ResourceManager.GetString(key);
            return string.IsNullOrEmpty(localized) ? category.ToString() : localized;
        }

        /// <summary>
        /// Пересчитать конфликты и префикс-флаги для всех строк.
        /// Не пересоздаёт строки — только обновляет свойства ConflictType, IsPrefix, IsExecutorBound.
        /// </summary>
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

                // Помечаем строку если её одиночный жест совпадает с зарезервированным префиксом
                if (hotKey.ActiveGesture != null &&
                    hotKey.ActiveGesture.IsSingle &&
                    reservedPrefixes.Any(p =>
                        p.Key == hotKey.ActiveGesture.FirstStep.Key &&
                        p.KeyModifiers == hotKey.ActiveGesture.FirstStep.KeyModifiers))
                {
                    row.IsPrefix = true;
                }
            }

            // Проверяем конфликты между всеми парами строк
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

        /// <summary>Сформировать текст подсказки о конфликте</summary>
        private string BuildConflictTooltip(HotKeyRowViewModel conflictWith, HotKeyConflictType conflictType)
        {
            var severity = conflictType == HotKeyConflictType.Critical
                ? Strings.HotKey_Conflict_Critical
                : Strings.HotKey_Conflict_Warning;

            return $"{severity}: {conflictWith.DisplayName}";
        }

        /// <summary>
        /// Применить фильтр к AllRows с учётом выбранной секции/подсекции.
        /// Если есть текст фильтра — ищет по всем секциям игнорируя выбранную.
        /// Если текст пустой — фильтрует только по выбранной секции/подсекции.
        /// </summary>
        private void ApplyFilter()
        {
            FilteredRows.Clear();

            var filter = _filterText.Trim().ToLowerInvariant();
            bool hasFilter = !string.IsNullOrEmpty(filter);

            foreach (var row in AllRows)
            {
                bool sectionMatch;

                if (hasFilter)
                {
                    sectionMatch = true;
                }
                else if (_selectedSubSection != null)
                {
                    sectionMatch = row.ModuleType == _selectedSubSection.ModuleType
                        && row.Category == _selectedSubSection.Category;
                }
                else if (_selectedSection != null)
                {
                    sectionMatch = _selectedSection.IsGlobal
                        ? row.ModuleType == null
                        : row.ModuleType == _selectedSection.ModuleType;
                }
                else
                {
                    sectionMatch = false;
                }

                if (!sectionMatch) continue;

                bool textMatch = !hasFilter
                    || row.DisplayName.ToLowerInvariant().Contains(filter)
                    || (row.Bindings.Any(b =>
                        b.GestureDisplay.ToLowerInvariant().Contains(filter) &&
                        b.GestureDisplay != Strings.HotKey_NotAssigned))
                    || (row.ModuleType?.ToLowerInvariant().Contains(filter) ?? false);

                if (textMatch)
                    FilteredRows.Add(row);
            }
        }

        /// <summary>
        /// Обновить строку live display для биндинга на основе текущих модификаторов.
        /// </summary>
        private void UpdateBindingLiveDisplay()
        {
            if (_editingBinding == null)
            {
                LiveInputDisplay = string.Empty;
                return;
            }

            LiveInputDisplay = BuildModifiersDisplay(_currentModifiers);
        }

        /// <summary>
        /// Обновить строку live display для префикса на основе текущих модификаторов.
        /// </summary>
        private void UpdatePrefixLiveDisplay()
        {
            if (_editingPrefixRow == null)
            {
                LiveInputDisplay = string.Empty;
                return;
            }

            LiveInputDisplay = BuildModifiersDisplay(_prefixCurrentModifiers);
        }

        /// <summary>
        /// Сформировать строку отображения текущих зажатых модификаторов.
        /// Показывает "Ctrl + ", "Ctrl+Shift + " пока пользователь держит клавиши.
        /// Возвращает HotKey_PressKey если модификаторов нет.
        /// </summary>
        private static string BuildModifiersDisplay(KeyModifiers modifiers)
        {
            if (modifiers == KeyModifiers.None)
                return Strings.HotKey_PressKey;

            var parts = new System.Collections.Generic.List<string>();

            if (modifiers.HasFlag(KeyModifiers.Control))
                parts.Add("Ctrl");
            if (modifiers.HasFlag(KeyModifiers.Alt))
                parts.Add("Alt");
            if (modifiers.HasFlag(KeyModifiers.Shift))
                parts.Add("Shift");

            return parts.Count > 0
                ? string.Join("+", parts) + " + "
                : Strings.HotKey_PressKey;
        }

        /// <summary>
        /// Преобразовать GestureAssignResult в локализованное сообщение об ошибке
        /// для операций с биндингами.
        /// Использует формулировку "Комбинация" вместо "Жест".
        /// </summary>
        private static string ResolveBindingAssignError(GestureAssignResult result) => result switch
        {
            GestureAssignResult.BlockedByPrefix => Strings.HotKey_Error_BlockedByPrefix,
            GestureAssignResult.BlockedByHotKey => Strings.HotKey_Error_Conflict,
            _ => string.Empty
        };

        /// <summary>
        /// Преобразовать GestureAssignResult в локализованное сообщение об ошибке
        /// для операций с префиксами.
        /// </summary>
        private static string ResolveGestureAssignError(GestureAssignResult result) => result switch
        {
            GestureAssignResult.BlockedByHotKey => Strings.HotKey_Prefix_Error_BlockedByHotKey,
            GestureAssignResult.BlockedByPrefix => Strings.HotKey_Prefix_Error_BlockedByHotKey,
            GestureAssignResult.PrefixAlreadyExists => Strings.HotKey_Prefix_Error_AlreadyExists,
            GestureAssignResult.PrefixInUse => Strings.HotKey_Prefix_Error_InUse,
            GestureAssignResult.PrefixNotRegistered => Strings.HotKey_Prefix_Error_BlockedByHotKey,
            _ => string.Empty
        };

        /// <summary>
        /// Обработчик события HotKeysChanged от сервиса.
        /// Обновляет конфликты, фильтр и при необходимости строки префиксов.
        /// </summary>
        private void OnHotKeysChanged()
        {
            RefreshConflictsAndPrefixes();
            ApplyFilter();

            if (_activeTab == HotKeySettingsTab.Prefixes)
                RefreshPrefixRows();
        }
    }
}