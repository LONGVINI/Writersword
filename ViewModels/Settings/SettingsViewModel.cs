using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.Common;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.Views.Settings;

namespace Writersword.ViewModels.Settings
{
    /// <summary>
    /// ViewModel окна настроек
    /// Собирает вкладки из глобальных настроек и настроек модулей
    /// Секция Global Module Settings и This Project Module Settings разделены и сворачиваемы
    /// </summary>
    public class SettingsViewModel : ViewModelBase
    {
        private readonly ILogger<SettingsViewModel> _logger;
        private readonly ISettingsService _settingsService;
        private SettingsTabItem? _selectedTab;

        /// <summary>Список вкладок настроек</summary>
        public ObservableCollection<SettingsTabItem> Tabs { get; } = new();

        /// <summary>Текущая выбранная вкладка</summary>
        public SettingsTabItem? SelectedTab
        {
            get => _selectedTab;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedTab, value);
                this.RaisePropertyChanged(nameof(ShowProjectBanner));
                this.RaisePropertyChanged(nameof(ShowGlobalBanner));
            }
        }

        /// <summary>Показывать ли янтарный баннер "This Project"</summary>
        public bool ShowProjectBanner => SelectedTab?.IsProjectTab == true;

        /// <summary>Показывать ли синий баннер "Global"</summary>
        public bool ShowGlobalBanner => SelectedTab?.IsProjectTab == false && SelectedTab?.IsModuleTab == true;

        /// <summary>Команда закрытия окна</summary>
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        /// <summary>Событие запроса на закрытие окна</summary>
        public event Action? CloseRequested;

        public SettingsViewModel()
        {
            _logger = App.Services.GetService<ILogger<SettingsViewModel>>()!;
            _settingsService = App.Services.GetRequiredService<ISettingsService>();
            CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
            LoadTabs();
        }

        /// <summary>Загрузить все вкладки настроек</summary>
        private void LoadTabs()
        {
            Tabs.Add(new SettingsTabItem
            {
                Title = Strings.Settings_Tab_General,
                Content = new GeneralSettingsView
                {
                    DataContext = new GeneralSettingsViewModel()
                },
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
                Content = new UserControl(),
                IsHeader = false,
                IsModuleTab = false
            });

            var moduleFactory = App.Services.GetRequiredService<ModuleFactory>();
            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var activeTab = tabCollection.ActiveTab;
            var hasActiveProject = activeTab != null && activeTab.Context?.FileStorage != null;

            var configurableModules = moduleFactory.GetConfigurableModules();

            var globalSectionHeader = new SettingsTabItem
            {
                Title = Strings.Settings_Section_GlobalModuleSettings,
                IsHeader = false,
                IsSectionHeader = true,
                IsGlobalSection = true,
                SectionKey = "global",
                IsExpanded = true
            };
            Tabs.Add(globalSectionHeader);

            foreach (var (moduleType, configurable) in configurableModules)
            {
                try
                {
                    var globalView = configurable.CreateSettingsView();
                    Tabs.Add(new SettingsTabItem
                    {
                        Title = configurable.SettingsTitle,
                        Content = globalView,
                        IsHeader = false,
                        IsModuleTab = true,
                        IsProjectTab = false,
                        SectionKey = "global",
                        IsVisible = true
                    });

                    _logger.LogDebug("Global settings tab added for module: {Title}", configurable.SettingsTitle);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading global settings tab for {ModuleType}", moduleType);
                }
            }

            var projectSectionHeader = new SettingsTabItem
            {
                Title = Strings.Settings_Section_ThisProjectSettings,
                IsHeader = false,
                IsSectionHeader = true,
                IsGlobalSection = false,
                SectionKey = "project",
                IsExpanded = true
            };
            Tabs.Add(projectSectionHeader);

            foreach (var (moduleType, configurable) in configurableModules)
            {
                try
                {
                    Control localView;
                    bool localEnabled = false;

                    if (hasActiveProject)
                    {
                        var liveModule = activeTab!.ModuleContext.GetModule(moduleType);
                        if (liveModule is IConfigurableModule liveConfigurable)
                        {
                            localView = liveConfigurable.CreateLocalSettingsView();
                            localEnabled = true;
                        }
                        else
                        {
                            var tempModule = moduleFactory.Create(moduleType);
                            if (tempModule is IConfigurableModule tempConfigurable)
                            {
                                tempModule.Initialize();
                                tempModule.Context = activeTab!.Context;
                                localView = tempConfigurable.CreateLocalSettingsView();
                                localEnabled = true;
                                _logger.LogDebug("Temp module created for project settings: {ModuleType}", moduleType);
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
                        IsHeader = false,
                        IsModuleTab = true,
                        IsProjectTab = true,
                        IsDisabled = !localEnabled,
                        SectionKey = "project",
                        IsVisible = true
                    });

                    _logger.LogDebug("Project settings tab added for module: {Title}", configurable.SettingsTitle);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading project settings tab for {ModuleType}", moduleType);
                }
            }

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

        /// <summary>Создать заглушку когда проект не открыт</summary>
        private static Control BuildNoProjectView()
        {
            return new UserControl
            {
                Content = new TextBlock
                {
                    Text = Strings.Settings_NoProjectOpen,
                    Foreground = Avalonia.Media.Brushes.Gray,
                    FontSize = 13,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                }
            };
        }

        /// <summary>Переключить свёрнутость секции модулей и обновить видимость дочерних вкладок</summary>
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
            {
                SelectedTab = null;
            }
        }

        /// <summary>Выбрать вкладку</summary>
        public void SelectTab(SettingsTabItem tab)
        {
            if (tab.IsHeader || tab.IsSectionHeader || tab.IsModuleHeader || tab.IsDisabled) return;

            foreach (var t in Tabs)
                t.IsSelected = false;

            tab.IsSelected = true;
            SelectedTab = tab;
        }
    }

    /// <summary>
    /// Элемент вкладки в окне настроек
    /// </summary>
    public class SettingsTabItem : ReactiveObject
    {
        private bool _isSelected;
        private bool _isVisible = true;
        private bool _isExpanded = true;

        /// <summary>Название вкладки</summary>
        public string Title { get; set; } = "";

        /// <summary>Содержимое вкладки</summary>
        public Control? Content { get; set; }

        /// <summary>Является ли элемент заголовком секции верхнего уровня (не кликабельный)</summary>
        public bool IsHeader { get; set; } = false;

        /// <summary>Является ли элемент цветным заголовком секции модулей (Global / This Project)</summary>
        public bool IsSectionHeader { get; set; } = false;

        /// <summary>Для IsSectionHeader: true — секция Global, false — секция This Project</summary>
        public bool IsGlobalSection { get; set; } = true;

        /// <summary>Ключ секции к которой принадлежит вкладка (global / project)</summary>
        public string SectionKey { get; set; } = "";

        /// <summary>Является ли элемент заголовком модуля</summary>
        public bool IsModuleHeader { get; set; } = false;

        /// <summary>Является ли вкладкой модуля (отображается с отступом)</summary>
        public bool IsModuleTab { get; set; } = false;

        /// <summary>Является ли вкладкой настроек проекта (показывает янтарный баннер)</summary>
        public bool IsProjectTab { get; set; } = false;

        /// <summary>Недоступна ли вкладка (нет открытого проекта или модуль не запущен)</summary>
        public bool IsDisabled { get; set; } = false;

        /// <summary>Показывать ли как кликабельную вкладку</summary>
        public bool IsClickable => !IsHeader && !IsSectionHeader && !IsModuleHeader && !IsDisabled;

        /// <summary>Развёрнута ли секция (для IsSectionHeader)</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                this.RaiseAndSetIfChanged(ref _isExpanded, value);
                this.RaisePropertyChanged(nameof(ExpandArrow));
            }
        }

        /// <summary>Видима ли вкладка (управляется через ToggleSection)</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                this.RaiseAndSetIfChanged(ref _isVisible, value);
                this.RaisePropertyChanged(nameof(IsVisibleInList));
            }
        }

        /// <summary>Видим ли элемент в списке — заголовки секций всегда видимы, остальные управляются через IsVisible</summary>
        public bool IsVisibleInList => IsSectionHeader || IsHeader || IsVisible;

        /// <summary>
        /// Цвет фона выделенной вкладки
        /// Используется отдельным слоем под hover-слоем чтобы не конфликтовать с pointerover стилем
        /// </summary>
        public IBrush TabSelectedBackground => IsSelected
            ? new SolidColorBrush(Color.Parse("#37373A"))
            : new SolidColorBrush(Colors.Transparent);

        /// <summary>
        /// Цвет текста кликабельной вкладки
        /// Выбранная — белая, остальные приглушённые
        /// </summary>
        public IBrush TabForeground => IsSelected
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(Color.Parse("#CCCCCC"));

        /// <summary>
        /// Цвет фона заголовка секции модулей
        /// Global — нейтральный тёмно-синий, This Project — янтарный
        /// </summary>
        public IBrush SectionHeaderBackground => IsGlobalSection
            ? new SolidColorBrush(Color.Parse("#1A1A2E"))
            : new SolidColorBrush(Color.Parse("#2A1F00"));

        /// <summary>
        /// Цвет текста заголовка секции модулей
        /// Global — синеватый, This Project — янтарный
        /// </summary>
        public IBrush SectionHeaderForeground => IsGlobalSection
            ? new SolidColorBrush(Color.Parse("#5B8DD9"))
            : new SolidColorBrush(Color.Parse("#C8881A"));

        /// <summary>
        /// Символ стрелки для заголовка секции
        /// Меняется в зависимости от IsExpanded
        /// </summary>
        public string ExpandArrow => IsExpanded ? "▾" : "▸";

        /// <summary>
        /// Отступ вкладки — для модульных вкладок добавляет левый отступ
        /// </summary>
        public Thickness Indent => IsModuleTab
            ? new Thickness(28, 7, 16, 7)
            : new Thickness(16, 7, 16, 7);

        /// <summary>Выбрана ли вкладка</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                this.RaisePropertyChanged(nameof(TabSelectedBackground));
                this.RaisePropertyChanged(nameof(TabForeground));
            }
        }
    }
}