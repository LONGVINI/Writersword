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
    /// Содержит список биндингов (мульти-бинд) и флаги конфликтов/префиксов.
    /// </summary>
    public class HotKeyRowViewModel : ReactiveObject
    {
        private HotKeyConflictType _conflictType;
        private string _conflictTooltip;
        private bool _isExecutorBound;
        private bool _isPrefix;

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
            _isExecutorBound = isExecutorBound;
            _conflictType = HotKeyConflictType.None;
            _conflictTooltip = string.Empty;

            // Пробуем резолвить DisplayNameKey как ключ локализации.
            // Если ключ не найден — используем сам ключ как отображаемое имя.
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
        /// Вызывается при инициализации и после каждого изменения жестов.
        /// Устанавливает ParentRow на каждый биндинг.
        /// </summary>
        public void RebuildBindings(HotKey hotKey)
        {
            Bindings.Clear();

            if (hotKey.CustomGestures.Count > 0)
            {
                for (int i = 0; i < hotKey.CustomGestures.Count; i++)
                {
                    var binding = new HotKeyBindingViewModel(i, true,
                        hotKey.CustomGestures[i].ToString());
                    binding.ParentRow = this;
                    Bindings.Add(binding);
                }
            }
            else if (hotKey.DefaultGestures.Count > 0)
            {
                for (int i = 0; i < hotKey.DefaultGestures.Count; i++)
                {
                    var binding = new HotKeyBindingViewModel(i, false,
                        hotKey.DefaultGestures[i].ToString());
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
    /// Секция в левой навигационной панели.
    /// Соответствует либо категории глобальных клавиш, либо отдельному модулю.
    /// </summary>
    public class HotKeySectionViewModel : ReactiveObject
    {
        private bool _isSelected;

        /// <summary>Отображаемое название секции</summary>
        public string Title { get; }

        /// <summary>
        /// Тип модуля для фильтрации строк.
        /// null — секция глобальных клавиш.
        /// </summary>
        public string? ModuleType { get; }

        /// <summary>Является ли секция глобальной (не модульной)</summary>
        public bool IsGlobal => ModuleType == null;

        /// <summary>Выбрана ли секция в навигации</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public HotKeySectionViewModel(string title, string? moduleType)
        {
            Title = title;
            ModuleType = moduleType;
        }
    }

    /// <summary>
    /// ViewModel вкладки настроек горячих клавиш.
    /// Левая панель — навигация по секциям (Global + каждый модуль отдельно).
    /// Правая панель — таблица клавиш выбранной секции.
    /// Поддерживает мульти-бинды, live display при вводе, префикс-бейджи, конфликты.
    /// </summary>
    public class HotKeySettingsViewModel : ReactiveObject
    {
        private readonly ILogger<HotKeySettingsViewModel> _logger;
        private readonly IHotKeyService _hotKeyService;

        private HotKeyBindingViewModel? _editingBinding;
        private HotKeyRowViewModel? _editingRow;
        private string _filterText = string.Empty;
        private HotKeySectionViewModel? _selectedSection;
        private string _liveInputDisplay = string.Empty;
        private bool _isLiveInputActive;
        private KeyModifiers _currentModifiers = KeyModifiers.None;

        /// <summary>Секции левой навигационной панели</summary>
        public ObservableCollection<HotKeySectionViewModel> Sections { get; } = new();

        /// <summary>Все строки всех секций</summary>
        public ObservableCollection<HotKeyRowViewModel> AllRows { get; } = new();

        /// <summary>Строки текущей секции с учётом фильтра</summary>
        public ObservableCollection<HotKeyRowViewModel> FilteredRows { get; } = new();

        /// <summary>Выбранная секция в левой панели</summary>
        public HotKeySectionViewModel? SelectedSection
        {
            get => _selectedSection;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedSection, value);
                ApplyFilter();
            }
        }

        /// <summary>Текст фильтра для поиска</summary>
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
        /// Например: "Ctrl + " или "Ctrl+Shift + " пока пользователь удерживает модификаторы.
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

        /// <summary>Команда отмены редактирования</summary>
        public ReactiveCommand<Unit, Unit> CancelEditCommand { get; }

        /// <summary>Команда выбора секции</summary>
        public ReactiveCommand<HotKeySectionViewModel, Unit> SelectSectionCommand { get; }

        /// <summary>
        /// Событие срабатывает когда биндинг переходит в режим редактирования.
        /// View подписывается чтобы захватить фокус для перехвата KeyDown/KeyUp.
        /// </summary>
        public event Action? EditingStarted;

        public HotKeySettingsViewModel()
        {
            _logger = App.Services.GetService<ILogger<HotKeySettingsViewModel>>()!;
            _hotKeyService = App.Services.GetRequiredService<IHotKeyService>();

            ResetRowCommand = ReactiveCommand.Create<HotKeyRowViewModel>(ResetRow);
            ResetAllCommand = ReactiveCommand.Create(ResetAll);
            StartEditBindingCommand = ReactiveCommand.Create<HotKeyBindingViewModel>(StartEditBinding);
            AddBindingCommand = ReactiveCommand.Create<HotKeyRowViewModel>(AddBinding);
            RemoveBindingCommand = ReactiveCommand.Create<HotKeyBindingViewModel>(RemoveBinding);
            CancelEditCommand = ReactiveCommand.Create(CancelEdit);
            SelectSectionCommand = ReactiveCommand.Create<HotKeySectionViewModel>(SelectSection);

            LoadAll();

            _hotKeyService.HotKeysChanged += OnHotKeysChanged;
        }

        /// <summary>
        /// Обработать нажатие клавиши в режиме редактирования.
        /// Вызывается из View при KeyDown.
        /// Одиночные модификаторы обновляют LiveInputDisplay но не завершают ввод.
        /// Escape — отменить. Delete/Backspace — сбросить к дефолту.
        /// Win/Meta клавиши игнорируются полностью.
        /// Любая другая клавиша — зафиксировать жест.
        /// </summary>
        public void HandleKeyDown(Key key, KeyModifiers modifiers)
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
                    UpdateLiveDisplay();
                }
                return;
            }

            if (key == Key.Escape)
            {
                CancelEdit();
                return;
            }

            if (key is Key.Delete or Key.Back)
            {
                var rowToReset = _editingRow;
                CancelEdit();
                ResetRow(rowToReset);
                return;
            }

            var cleanModifiers = modifiers & ~KeyModifiers.Meta;
            var gesture = new KeyGesture(key, cleanModifiers);
            var hotKeyGesture = new HotKeyGesture(gesture);

            var row = _editingRow;
            var binding = _editingBinding;
            bool isNewBinding = !binding.IsCustom && binding.GestureDisplay == Strings.HotKey_PressKey;
            CancelEdit();

            if (isNewBinding)
            {
                CommitNewBinding(row, hotKeyGesture);
            }
            else if (binding.IsCustom)
            {
                _hotKeyService.ReplaceCustomGesture(row.Id, binding.Index, hotKeyGesture);

                var updatedHotKey = _hotKeyService.GetHotKey(row.Id);
                if (updatedHotKey != null)
                    row.RebuildBindings(updatedHotKey);
            }
            else
            {
                _hotKeyService.SetCustomGestureSequence(row.Id, hotKeyGesture);

                var updatedHotKey = _hotKeyService.GetHotKey(row.Id);
                if (updatedHotKey != null)
                    row.RebuildBindings(updatedHotKey);
            }

            RefreshConflictsAndPrefixes();
        }

        /// <summary>
        /// Обработать отпускание клавиши.
        /// Вызывается из View при KeyUp для обновления отображения модификаторов.
        /// Win/Meta клавиши игнорируются.
        /// </summary>
        public void HandleKeyUp(Key key, KeyModifiers modifiers)
        {
            if (_editingBinding == null) return;

            bool isWinKey = key is Key.LWin or Key.RWin;
            if (isWinKey) return;

            _currentModifiers = modifiers & ~KeyModifiers.Meta;
            UpdateLiveDisplay();
        }

        /// <summary>
        /// Загрузить все секции и строки из HotKeyService.
        /// Вызывается при инициализации и при ResetAll.
        /// </summary>
        private void LoadAll()
        {
            Sections.Clear();
            AllRows.Clear();

            var hotKeys = _hotKeyService.GetAllHotKeys();

            var globalSection = new HotKeySectionViewModel(Strings.HotKey_Section_Global, null);
            Sections.Add(globalSection);

            var globalKeys = hotKeys
                .Where(hk => hk.ModuleType == null)
                .OrderBy(hk => hk.Category)
                .ThenBy(hk => hk.Id);

            foreach (var hk in globalKeys)
                AllRows.Add(new HotKeyRowViewModel(hk, true));

            var moduleTypes = hotKeys
                .Where(hk => hk.ModuleType != null)
                .Select(hk => hk.ModuleType!)
                .Distinct()
                .OrderBy(mt => mt);

            foreach (var moduleType in moduleTypes)
            {
                var moduleKeys = hotKeys
                    .Where(hk => hk.ModuleType == moduleType)
                    .OrderBy(hk => hk.Id)
                    .ToList();

                if (moduleKeys.Count == 0) continue;

                // Название секции тоже резолвим через ResourceManager
                var sectionTitle = Strings.ResourceManager.GetString(moduleType);
                var section = new HotKeySectionViewModel(
                    string.IsNullOrEmpty(sectionTitle) ? moduleType : sectionTitle,
                    moduleType);
                Sections.Add(section);

                bool executorBound = _hotKeyService.IsExecutorBound(moduleType);

                foreach (var hk in moduleKeys)
                    AllRows.Add(new HotKeyRowViewModel(hk, executorBound));
            }

            if (Sections.Count > 0)
            {
                Sections[0].IsSelected = true;
                _selectedSection = Sections[0];
            }

            RefreshConflictsAndPrefixes();
            ApplyFilter();
        }

        /// <summary>
        /// Пересчитать конфликты и префикс-флаги для всех строк.
        /// Не пересоздаёт строки — только обновляет свойства.
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

                foreach (var gesture in hotKey.ActiveGestures)
                {
                    if (gesture.IsSingle &&
                        reservedPrefixes.Any(p =>
                            p.Key == gesture.FirstStep.Key &&
                            p.KeyModifiers == gesture.FirstStep.KeyModifiers))
                    {
                        row.IsPrefix = true;
                        break;
                    }
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
                bool sectionMatch = hasFilter
                    || _selectedSection == null
                    || (_selectedSection.IsGlobal && row.ModuleType == null)
                    || (!_selectedSection.IsGlobal && row.ModuleType == _selectedSection.ModuleType);

                if (!sectionMatch) continue;

                bool textMatch = !hasFilter
                    || row.DisplayName.ToLowerInvariant().Contains(filter)
                    || row.Bindings.Any(b => b.GestureDisplay.ToLowerInvariant().Contains(filter))
                    || (row.ModuleType?.ToLowerInvariant().Contains(filter) ?? false);

                if (textMatch)
                    FilteredRows.Add(row);
            }
        }

        private void SelectSection(HotKeySectionViewModel section)
        {
            foreach (var s in Sections)
                s.IsSelected = false;

            section.IsSelected = true;
            SelectedSection = section;
        }

        private void StartEditBinding(HotKeyBindingViewModel binding)
        {
            CancelEdit();

            _editingRow = binding.ParentRow;
            _editingBinding = binding;
            _currentModifiers = KeyModifiers.None;
            binding.IsEditing = true;

            UpdateLiveDisplay();
            IsLiveInputActive = true;
            EditingStarted?.Invoke();
        }

        private void AddBinding(HotKeyRowViewModel row)
        {
            CancelEdit();

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

            UpdateLiveDisplay();
            IsLiveInputActive = true;
            EditingStarted?.Invoke();
        }

        private void CommitNewBinding(HotKeyRowViewModel row, HotKeyGesture gesture)
        {
            _hotKeyService.AddCustomGesture(row.Id, gesture);

            var updatedHotKey = _hotKeyService.GetHotKey(row.Id);
            if (updatedHotKey != null)
                row.RebuildBindings(updatedHotKey);

            RefreshConflictsAndPrefixes();
        }

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

        private void CancelEdit()
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
            LiveInputDisplay = string.Empty;
            IsLiveInputActive = false;
        }

        private void ResetRow(HotKeyRowViewModel row)
        {
            _hotKeyService.ResetToDefault(row.Id);

            var hotKey = _hotKeyService.GetHotKey(row.Id);
            if (hotKey != null)
                row.RebuildBindings(hotKey);

            RefreshConflictsAndPrefixes();
        }

        private void ResetAll()
        {
            _hotKeyService.ResetAllToDefaults();
            LoadAll();
        }

        private void UpdateLiveDisplay()
        {
            if (_editingBinding == null)
            {
                LiveInputDisplay = string.Empty;
                return;
            }

            if (_currentModifiers == KeyModifiers.None)
            {
                LiveInputDisplay = Strings.HotKey_PressKey;
                return;
            }

            var parts = new System.Collections.Generic.List<string>();

            if (_currentModifiers.HasFlag(KeyModifiers.Control))
                parts.Add("Ctrl");
            if (_currentModifiers.HasFlag(KeyModifiers.Alt))
                parts.Add("Alt");
            if (_currentModifiers.HasFlag(KeyModifiers.Shift))
                parts.Add("Shift");

            LiveInputDisplay = parts.Count > 0
                ? string.Join("+", parts) + " + "
                : Strings.HotKey_PressKey;
        }

        private void OnHotKeysChanged()
        {
            RefreshConflictsAndPrefixes();
            ApplyFilter();
        }
    }
}