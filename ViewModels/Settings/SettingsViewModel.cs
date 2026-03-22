using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.Common;
using Writersword.Resources.Localization;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Views;
using Writersword.Views.Settings;

namespace Writersword.ViewModels.Settings
{
    /// <summary>
    /// ViewModel окна настроек.
    /// Глобальные настройки: сохраняются в ISettingsService при закрытии.
    /// Локальные настройки: сохраняются в {moduleType}/settings.json внутри project.zip при закрытии.
    /// </summary>
    public class SettingsViewModel : ViewModelBase
    {
        private readonly ILogger<SettingsViewModel> _logger;
        private readonly ISettingsService _settingsService;
        private readonly IDialogService _dialogService;
        private readonly ModuleFactory _moduleFactory;
        private SettingsTabItem? _selectedTab;

        public ObservableCollection<SettingsTabItem> Tabs { get; } = new();

        public SettingsTabItem? SelectedTab
        {
            get => _selectedTab;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedTab, value);
                this.RaisePropertyChanged(nameof(ShowProjectBanner));
                this.RaisePropertyChanged(nameof(ShowGlobalBanner));
                this.RaisePropertyChanged(nameof(ShowModuleToolbar));
                this.RaisePropertyChanged(nameof(IsSelectedTabLocal));
                this.RaisePropertyChanged(nameof(IsSelectedTabGlobal));
            }
        }

        public bool ShowProjectBanner => SelectedTab?.IsProjectTab == true;
        public bool ShowGlobalBanner => SelectedTab?.IsProjectTab == false && SelectedTab?.IsModuleTab == true;

        /// <summary>True когда нужно показывать toolbar над контентом модуля.</summary>
        public bool ShowModuleToolbar => SelectedTab?.IsModuleTab == true && SelectedTab?.IsDisabled == false;

        /// <summary>True когда выбрана локальная вкладка модуля.</summary>
        public bool IsSelectedTabLocal => SelectedTab?.IsProjectLocal == true;

        /// <summary>True когда выбрана глобальная вкладка модуля.</summary>
        public bool IsSelectedTabGlobal => SelectedTab?.IsProjectLocal == false && SelectedTab?.IsModuleTab == true;

        /// <summary>Команда закрытия — применяет все настройки и закрывает окно.</summary>
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        /// <summary>Сбросить все поля текущей вкладки до хардкод дефолтов.</summary>
        public ReactiveCommand<Unit, Unit> ResetAllToDefaultsCommand { get; }

        /// <summary>Сбросить все поля текущей локальной вкладки до глобальных значений.</summary>
        public ReactiveCommand<Unit, Unit> ResetAllToGlobalCommand { get; }

        /// <summary>Сохранить текущие глобальные настройки в файл и уведомить живой модуль.</summary>
        public ReactiveCommand<Unit, Unit> SaveAsGlobalCommand { get; }

        /// <summary>
        /// Применить текущие глобальные UI-значения к локальной VM того же модуля.
        /// Доступна только в глобальной вкладке.
        /// </summary>
        public ReactiveCommand<Unit, Unit> ApplyToProjectCommand { get; }

        /// <summary>
        /// Сохранить текущие локальные UI-значения как глобальные.
        /// Доступна только в локальной вкладке.
        /// </summary>
        public ReactiveCommand<Unit, Unit> PromoteLocalToGlobalCommand { get; }

        public event Action? CloseRequested;

        public SettingsViewModel()
        {
            _logger = App.Services.GetService<ILogger<SettingsViewModel>>()!;
            _settingsService = App.Services.GetRequiredService<ISettingsService>();
            _dialogService = App.Services.GetRequiredService<IDialogService>();
            _moduleFactory = App.Services.GetRequiredService<ModuleFactory>();

            CloseCommand = ReactiveCommand.Create(() =>
            {
                ApplyAllSettings();
                CloseRequested?.Invoke();
            });

            ResetAllToDefaultsCommand = ReactiveCommand.CreateFromTask(ResetAllToDefaultsAsync);
            ResetAllToGlobalCommand = ReactiveCommand.CreateFromTask(ResetAllToGlobalAsync);
            SaveAsGlobalCommand = ReactiveCommand.CreateFromTask(SaveAsGlobalAsync);
            ApplyToProjectCommand = ReactiveCommand.CreateFromTask(ApplyToProjectAsync);
            PromoteLocalToGlobalCommand = ReactiveCommand.CreateFromTask(PromoteLocalToGlobalAsync);

            LoadTabs();
        }

        // ── Toolbar команды ───────────────────────────────────────────────

        /// <summary>
        /// Сбросить все поля текущей вкладки до хардкод дефолтов.
        /// Работает и для глобальной и для локальной вкладки.
        /// </summary>
        private async Task ResetAllToDefaultsAsync()
        {
            if (SelectedTab?.Module is null) return;

            var result = await _dialogService.ShowMessageAsync(
                Strings.Settings_Confirm_ResetAllToDefaults_Title,
                Strings.Settings_Confirm_ResetAllToDefaults_Message,
                MessageBoxType.Warning,
                MessageBoxButtons.YesNo);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (SelectedTab.IsProjectLocal)
                {
                    SelectedTab.Module.ResetLocalSettingsToDefaults();
                    _logger.LogDebug("Local settings reset to defaults: {Title}", SelectedTab.Title);
                }
                else
                {
                    SelectedTab.Module.ResetSettingsToDefaults();
                    _logger.LogDebug("Global settings reset to defaults: {Title}", SelectedTab.Title);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting settings to defaults for tab {Title}", SelectedTab.Title);
            }
        }

        /// <summary>
        /// Сбросить все поля текущей локальной вкладки до глобальных значений.
        /// </summary>
        private async Task ResetAllToGlobalAsync()
        {
            if (SelectedTab?.Module is null || !SelectedTab.IsProjectLocal) return;

            var result = await _dialogService.ShowMessageAsync(
                Strings.Settings_Confirm_ResetAllToGlobal_Title,
                Strings.Settings_Confirm_ResetAllToGlobal_Message,
                MessageBoxType.Warning,
                MessageBoxButtons.YesNo);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                SelectedTab.Module.ResetLocalSettingsToGlobal();
                _logger.LogDebug("Local settings reset to global: {Title}", SelectedTab.Title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting local settings to global for tab {Title}", SelectedTab.Title);
            }
        }

        /// <summary>
        /// Сохранить текущие глобальные UI-значения в файл и уведомить живой модуль.
        /// Только для глобальных вкладок.
        /// </summary>
        private async Task SaveAsGlobalAsync()
        {
            if (SelectedTab?.Module is null || SelectedTab.IsProjectLocal) return;

            var result = await _dialogService.ShowMessageAsync(
                Strings.Settings_Confirm_SaveAsGlobal_Title,
                Strings.Settings_Confirm_SaveAsGlobal_Message,
                MessageBoxType.Question,
                MessageBoxButtons.YesNo);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                var dc = SelectedTab.Content?.DataContext;
                if (dc is null) return;

                var method = dc.GetType().GetMethod("GetSettings");
                if (method is null) return;

                var settings = method.Invoke(dc, null);
                if (settings is null) return;

                // Сохраняем в глобальный модуль
                SelectedTab.Module.ApplySettings(settings);
                _logger.LogDebug("Settings saved as global: {Title}", SelectedTab.Title);

                if (!string.IsNullOrEmpty(SelectedTab.ModuleType))
                {
                    // Обновляем локальную вкладку — GlobalValue поменялся
                    foreach (var tab in Tabs)
                    {
                        if (tab.ModuleType == SelectedTab.ModuleType && tab.IsProjectLocal && tab.IsModuleTab)
                        {
                            // Применяем новые глобальные к локальному модулю как глобальный ориентир
                            tab.Module?.ApplySettings(settings);
                            // Пересоздаём View с обновлёнными GlobalValue
                            if (tab.Module is not null)
                                tab.Content = tab.Module.CreateLocalSettingsView();
                            _logger.LogDebug("Local tab refreshed after SaveAsGlobal: {ModuleType}", SelectedTab.ModuleType);
                            break;
                        }
                    }

                    // Уведомляем живой модуль
                    var live = _moduleFactory.GetLive(SelectedTab.ModuleType);
                    if (live is not null && !ReferenceEquals(live, SelectedTab.Module))
                    {
                        live.ApplySettings(settings);
                        _logger.LogDebug("Live module notified after SaveAsGlobal: {ModuleType}", SelectedTab.ModuleType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving settings as global for tab {Title}", SelectedTab.Title);
            }
        }


        /// <summary>
        /// Применить текущие глобальные UI-значения к локальной VM того же модуля.
        /// Только для глобальных вкладок. Не сохраняет — применяется при закрытии.
        /// </summary>
        private async Task ApplyToProjectAsync()
        {
            if (SelectedTab?.Module is null || SelectedTab.IsProjectLocal) return;

            var result = await _dialogService.ShowMessageAsync(
                Strings.Settings_Confirm_ApplyToProject_Title,
                Strings.Settings_Confirm_ApplyToProject_Message,
                MessageBoxType.Question,
                MessageBoxButtons.YesNo);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                var dc = SelectedTab.Content?.DataContext;
                var method = dc?.GetType().GetMethod("GetSettings");
                var settings = method?.Invoke(dc, null);
                if (settings is null) return;

                // Находим локальную вкладку и применяем к ней напрямую
                foreach (var tab in Tabs)
                {
                    if (tab.ModuleType == SelectedTab.ModuleType && tab.IsProjectLocal && tab.IsModuleTab)
                    {
                        tab.Module?.ApplySettings(settings);       // обновляет _globalSettings
                        tab.Module?.ApplyLocalSettings(settings);  // обновляет _localSettings
                        if (tab.Module is not null)
                            tab.Content = tab.Module.CreateLocalSettingsView();
                        _logger.LogDebug("ApplyGlobalToLocal via local tab: {ModuleType}", SelectedTab.ModuleType);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ApplyToProject for {Title}", SelectedTab.Title);
            }
        }

        /// <summary>
        /// Сохранить текущие локальные UI-значения как глобальные.
        /// Только для локальных вкладок. Сохраняет в ISettingsService немедленно.
        /// </summary>
        private async Task PromoteLocalToGlobalAsync()
        {
            if (SelectedTab?.Module is null || !SelectedTab.IsProjectLocal) return;

            var result = await _dialogService.ShowMessageAsync(
                Strings.Settings_Confirm_PromoteToGlobal_Title,
                Strings.Settings_Confirm_PromoteToGlobal_Message,
                MessageBoxType.Question,
                MessageBoxButtons.YesNo);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                SelectedTab.Module.PromoteLocalToGlobal();
                _logger.LogDebug("PromoteLocalToGlobal: {ModuleType}", SelectedTab.ModuleType);

                if (!string.IsNullOrEmpty(SelectedTab.ModuleType))
                {
                    // Получаем промоутированные настройки из локального модуля
                    var promotedSettings = SelectedTab.Module.GetLocalSettings();

                    // Находим глобальную вкладку и обновляем её модуль напрямую
                    foreach (var tab in Tabs)
                    {
                        if (tab.ModuleType == SelectedTab.ModuleType && !tab.IsProjectLocal && tab.IsModuleTab)
                        {
                            // Применяем к глобальному модулю — обновляет его _globalSettings
                            tab.Module?.ApplySettings(promotedSettings);
                            // Пересоздаём View с обновлёнными значениями
                            if (tab.Module is not null)
                                tab.Content = tab.Module.CreateSettingsView();
                            _logger.LogDebug("Global tab refreshed after promote: {ModuleType}", SelectedTab.ModuleType);
                            break;
                        }
                    }

                    // Уведомляем живой модуль из контекста если он отличается
                    var live = _moduleFactory.GetLive(SelectedTab.ModuleType);
                    if (live is not null && !ReferenceEquals(live, SelectedTab.Module))
                    {
                        live.ApplySettings(promotedSettings);
                        _logger.LogDebug("Live module notified after PromoteLocalToGlobal: {ModuleType}", SelectedTab.ModuleType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PromoteLocalToGlobal for {Title}", SelectedTab.Title);
            }
        }

        /// <summary>
        /// Пересоздаёт View глобальной вкладки модуля после того как глобальные значения изменились.
        /// </summary>
        private void RefreshGlobalTab(string moduleType)
        {
            var globalTab = null as SettingsTabItem;
            foreach (var tab in Tabs)
            {
                if (tab.ModuleType == moduleType && !tab.IsProjectLocal && tab.IsModuleTab)
                {
                    globalTab = tab;
                    break;
                }
            }

            if (globalTab?.Module is null) return;

            try
            {
                globalTab.Content = globalTab.Module.CreateSettingsView();
                _logger.LogDebug("Global tab refreshed: {ModuleType}", moduleType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing global tab: {ModuleType}", moduleType);
            }
        }

        /// <summary>
        /// Пересоздаёт View локальной вкладки модуля после того как глобальные значения изменились.
        /// </summary>
        private void RefreshLocalTab(string moduleType)
        {
            var localTab = null as SettingsTabItem;
            foreach (var tab in Tabs)
            {
                if (tab.ModuleType == moduleType && tab.IsProjectLocal && tab.IsModuleTab)
                {
                    localTab = tab;
                    break;
                }
            }

            if (localTab?.Module is null) return;

            try
            {
                localTab.Content = localTab.Module.CreateLocalSettingsView();
                _logger.LogDebug("Local tab refreshed: {ModuleType}", moduleType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing local tab: {ModuleType}", moduleType);
            }
        }
        // ── Применение настроек при закрытии ─────────────────────────────

        /// <summary>
        /// Применяет настройки всех вкладок при закрытии окна.
        /// Глобальные: сохраняет в ISettingsService + уведомляет живой модуль.
        /// Локальные: сохраняет в {moduleType}/settings.json внутри project.zip
        ///            + применяет на живой модуль.
        /// </summary>
        private void ApplyAllSettings()
        {
            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var storage = tabCollection.ActiveTab?.Context?.FileStorage;
            var localSettingsService = App.Services.GetRequiredService<ILocalSettingsStorageService>();

            foreach (var tab in Tabs)
            {
                if (tab.Module is null) continue;

                try
                {
                    if (tab.IsProjectLocal)
                    {
                        // Локальные — читаем из UI как раньше
                        if (tab.Content?.DataContext is null) continue;
                        var dc = tab.Content.DataContext;
                        var method = dc.GetType().GetMethod("GetSettings");
                        if (method is null) continue;
                        var settings = method.Invoke(dc, null);
                        if (settings is null) continue;

                        tab.Module.ApplyLocalSettings(settings);
                        _logger.LogDebug("Local settings applied: {Title}", tab.Title);

                        if (storage != null && !string.IsNullOrEmpty(tab.ModuleType))
                        {
                            localSettingsService.Save(storage, tab.ModuleType, settings);
                            _logger.LogDebug("Local settings saved to ZIP: {ModuleType}", tab.ModuleType);
                        }
                        else
                        {
                            _logger.LogWarning("Cannot save local settings — no storage for {ModuleType}", tab.ModuleType);
                        }
                    }
                    else if (tab.IsModuleTab)
                    {
                        // Глобальные модульные — берём из модуля, не из UI
                        // Модуль уже обновил _globalSettings через SaveAsGlobal или PromoteLocalToGlobal
                        var settings = tab.Module.GetSettings();
                        tab.Module.ApplySettings(settings);
                        _logger.LogDebug("Global settings saved from module: {Title}", tab.Title);

                        if (!string.IsNullOrEmpty(tab.ModuleType))
                        {
                            var live = _moduleFactory.GetLive(tab.ModuleType);
                            if (live is not null && !ReferenceEquals(live, tab.Module))
                            {
                                live.ApplySettings(settings);
                                _logger.LogDebug("Live module notified: {ModuleType}", tab.ModuleType);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error applying settings for tab {Title}", tab.Title);
                }
            }
        }

        // ── Загрузка вкладок ──────────────────────────────────────────────

        private void LoadTabs()
        {
            // ── Системные вкладки ─────────────────────────────────────────
            Tabs.Add(new SettingsTabItem
            {
                Title = Strings.Settings_Tab_General,
                Content = new GeneralSettingsView { DataContext = new GeneralSettingsViewModel() },
                IsHeader = false,
                IsModuleTab = false
            });

            Tabs.Add(new SettingsTabItem
            {
                Title = Strings.Settings_Tab_Project,
                Content = new UserControl(),
                IsHeader = false,
                IsModuleTab = false
            });

            Tabs.Add(new SettingsTabItem
            {
                Title = Strings.Settings_Tab_Keybindings,
                Content = new HotKeySettingsView { DataContext = new HotKeySettingsViewModel() },
                IsHeader = false,
                IsModuleTab = false
            });

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var activeTab = tabCollection.ActiveTab;
            var hasActiveProject = activeTab?.Context?.FileStorage != null;

            var configurableModules = _moduleFactory.GetConfigurableModules();

            // ── Секция глобальных настроек ────────────────────────────────
            Tabs.Add(new SettingsTabItem
            {
                Title = Strings.Settings_Section_GlobalModuleSettings,
                IsHeader = false,
                IsSectionHeader = true,
                IsGlobalSection = true,
                SectionKey = "global",
                IsExpanded = true
            });

            foreach (var (moduleType, configurable) in configurableModules)
            {
                try
                {
                    var globalView = configurable.CreateSettingsView();

                    Tabs.Add(new SettingsTabItem
                    {
                        Title = configurable.SettingsTitle,
                        Content = globalView,
                        Module = configurable,
                        ModuleType = moduleType,
                        IsProjectLocal = false,
                        IsHeader = false,
                        IsModuleTab = true,
                        IsProjectTab = false,
                        SectionKey = "global",
                        IsVisible = true
                    });

                    _logger.LogDebug("Global settings tab added: {Title}", configurable.SettingsTitle);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading global settings tab for {ModuleType}", moduleType);
                }
            }

            // ── Секция настроек проекта ───────────────────────────────────
            Tabs.Add(new SettingsTabItem
            {
                Title = Strings.Settings_Section_ThisProjectSettings,
                IsHeader = false,
                IsSectionHeader = true,
                IsGlobalSection = false,
                SectionKey = "project",
                IsExpanded = true
            });

            foreach (var (moduleType, configurable) in configurableModules)
            {
                try
                {
                    Control localView;
                    bool localEnabled = false;
                    IConfigurableModule? localModule = null;

                    if (hasActiveProject)
                    {
                        var liveModule = activeTab!.ModuleContext.GetModule(moduleType);

                        if (liveModule is IConfigurableModule liveConfigurable)
                        {
                            localView = liveConfigurable.CreateLocalSettingsView();
                            localModule = liveConfigurable;
                            localEnabled = true;
                        }
                        else
                        {
                            var tempModule = _moduleFactory.Create(moduleType);
                            if (tempModule is IConfigurableModule tempConfigurable)
                            {
                                tempModule.Initialize();
                                tempModule.Context = activeTab!.Context;
                                localView = tempConfigurable.CreateLocalSettingsView();
                                localModule = tempConfigurable;
                                localEnabled = true;
                                _logger.LogDebug("Temp module for project settings: {ModuleType}", moduleType);
                            }
                            else
                            {
                                localView = BuildNoProjectView();
                                tempModule?.Dispose();
                            }
                        }
                    }
                    else
                    {
                        localView = BuildNoProjectView();
                    }

                    Tabs.Add(new SettingsTabItem
                    {
                        Title = configurable.SettingsTitle,
                        Content = localView,
                        Module = localModule,
                        ModuleType = moduleType,
                        IsProjectLocal = true,
                        IsHeader = false,
                        IsModuleTab = true,
                        IsProjectTab = true,
                        IsDisabled = !localEnabled,
                        SectionKey = "project",
                        IsVisible = true
                    });

                    _logger.LogDebug("Project settings tab added: {Title}", configurable.SettingsTitle);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading project settings tab for {ModuleType}", moduleType);
                }
            }

            // ── Выбираем первую доступную вкладку ────────────────────────
            foreach (var tab in Tabs)
            {
                if (!tab.IsHeader && !tab.IsSectionHeader && !tab.IsModuleHeader && !tab.IsDisabled)
                {
                    SelectTab(tab);
                    break;
                }
            }

            _logger.LogDebug("Loaded {Count} settings tabs", Tabs.Count);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static Control BuildNoProjectView() => new UserControl
        {
            Content = new TextBlock
            {
                Text = Strings.Settings_NoProjectOpen,
                Foreground = Brushes.Gray,
                FontSize = 13,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            }
        };

        public void ToggleSection(SettingsTabItem sectionHeader)
        {
            if (!sectionHeader.IsSectionHeader) return;

            sectionHeader.IsExpanded = !sectionHeader.IsExpanded;

            foreach (var tab in Tabs)
            {
                if (tab.SectionKey == sectionHeader.SectionKey && !tab.IsSectionHeader)
                    tab.IsVisible = sectionHeader.IsExpanded;
            }

            if (SelectedTab?.SectionKey == sectionHeader.SectionKey && !sectionHeader.IsExpanded)
                SelectedTab = null;
        }

        public void SelectTab(SettingsTabItem tab)
        {
            if (tab.IsHeader || tab.IsSectionHeader || tab.IsModuleHeader || tab.IsDisabled) return;

            foreach (var t in Tabs)
                t.IsSelected = false;

            tab.IsSelected = true;
            SelectedTab = tab;
        }
    }

    // ── SettingsTabItem ───────────────────────────────────────────────────

    public class SettingsTabItem : ReactiveObject
    {
        private bool _isSelected;
        private bool _isVisible = true;
        private bool _isExpanded = true;

        public string Title { get; set; } = "";

        private Control? _content;
        public Control? Content
        {
            get => _content;
            set => this.RaiseAndSetIfChanged(ref _content, value);
        }

        /// <summary>Модуль которому принадлежит вкладка. Null для системных вкладок.</summary>
        public IConfigurableModule? Module { get; set; }

        /// <summary>Тип модуля — используется для поиска живого экземпляра через ModuleFactory.</summary>
        public string ModuleType { get; set; } = "";

        /// <summary>True — локальные настройки проекта, False — глобальные.</summary>
        public bool IsProjectLocal { get; set; } = false;

        public bool IsHeader { get; set; } = false;
        public bool IsSectionHeader { get; set; } = false;
        public bool IsGlobalSection { get; set; } = true;
        public string SectionKey { get; set; } = "";
        public bool IsModuleHeader { get; set; } = false;
        public bool IsModuleTab { get; set; } = false;
        public bool IsProjectTab { get; set; } = false;
        public bool IsDisabled { get; set; } = false;
        public bool IsClickable => !IsHeader && !IsSectionHeader && !IsModuleHeader && !IsDisabled;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                this.RaiseAndSetIfChanged(ref _isExpanded, value);
                this.RaisePropertyChanged(nameof(ExpandArrow));
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                this.RaiseAndSetIfChanged(ref _isVisible, value);
                this.RaisePropertyChanged(nameof(IsVisibleInList));
            }
        }

        public bool IsVisibleInList => IsSectionHeader || IsHeader || IsVisible;
        public string ExpandArrow => IsExpanded ? "▾" : "▸";
        public Thickness Indent => IsModuleTab
            ? new Thickness(28, 7, 16, 7)
            : new Thickness(16, 7, 16, 7);

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }
    }
}