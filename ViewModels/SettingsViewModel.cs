using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.Common;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.WorkFlows;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel окна настроек
    /// Собирает вкладки из глобальных настроек и настроек модулей
    /// Для каждого модуля создаёт два раздела: Global и This Project
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
            set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
        }

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
                Title = "General",
                Content = new UserControl(),
                IsHeader = false,
                IsModuleTab = false
            });

            Tabs.Add(new SettingsTabItem
            {
                Title = "Project",
                Content = new UserControl(),
                IsHeader = false,
                IsModuleTab = false
            });

            Tabs.Add(new SettingsTabItem
            {
                Title = "MODULES",
                IsHeader = true
            });

            var moduleFactory = App.Services.GetRequiredService<ModuleFactory>();
            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            var activeTab = tabCollection.ActiveTab;
            var hasActiveProject = activeTab != null;

            var configurableModules = moduleFactory.GetConfigurableModules();

            foreach (var (moduleType, configurable) in configurableModules)
            {
                try
                {
                    // Заголовок модуля
                    Tabs.Add(new SettingsTabItem
                    {
                        Title = configurable.SettingsTitle,
                        IsHeader = true,
                        IsModuleHeader = true
                    });

                    // Вкладка Global
                    var globalView = configurable.CreateSettingsView();
                    Tabs.Add(new SettingsTabItem
                    {
                        Title = "Global",
                        Content = globalView,
                        IsHeader = false,
                        IsModuleTab = true
                    });

                    // Вкладка This Project — активна только если есть открытый проект
                    Control localView;
                    bool localEnabled = false;

                    if (hasActiveProject)
                    {
                        var liveModule = activeTab!.ModuleContext.GetModule(moduleType);
                        if (liveModule is IConfigurableModule liveConfigurable
                            && activeTab.Context?.FileStorage != null)
                        {
                            localView = liveConfigurable.CreateLocalSettingsView();
                            localEnabled = true;
                        }
                        else
                        {
                            localView = BuildNoProjectView();
                        }
                    }
                    else
                    {
                        localView = BuildNoProjectView();
                    }

                    Tabs.Add(new SettingsTabItem
                    {
                        Title = "This Project",
                        Content = localView,
                        IsHeader = false,
                        IsModuleTab = true,
                        IsDisabled = !localEnabled
                    });

                    _logger.LogDebug("Settings tabs added for module: {Title}", configurable.SettingsTitle);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading settings tabs for {ModuleType}", moduleType);
                }
            }

            foreach (var tab in Tabs)
            {
                if (!tab.IsHeader && !tab.IsDisabled)
                {
                    SelectTab(tab);
                    break;
                }
            }

            _logger.LogDebug("Loaded {Count} settings tabs", Tabs.Count);
        }

        /// <summary>Создать заглушку когда проект не открыт или модуль не запущен</summary>
        private static Control BuildNoProjectView()
        {
            return new UserControl
            {
                Content = new TextBlock
                {
                    Text = "No project is open",
                    Foreground = Avalonia.Media.Brushes.Gray,
                    FontSize = 13,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                }
            };
        }

        /// <summary>Выбрать вкладку</summary>
        public void SelectTab(SettingsTabItem tab)
        {
            if (tab.IsHeader || tab.IsDisabled) return;

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

        /// <summary>Название вкладки</summary>
        public string Title { get; set; } = "";

        /// <summary>Содержимое вкладки</summary>
        public Control? Content { get; set; }

        /// <summary>Является ли элемент заголовком секции верхнего уровня (не кликабельный)</summary>
        public bool IsHeader { get; set; } = false;

        /// <summary>Является ли элемент заголовком модуля</summary>
        public bool IsModuleHeader { get; set; } = false;

        /// <summary>Является ли вкладкой модуля (отображается с отступом)</summary>
        public bool IsModuleTab { get; set; } = false;

        /// <summary>Недоступна ли вкладка (нет открытого проекта или модуль не запущен)</summary>
        public bool IsDisabled { get; set; } = false;

        /// <summary>Показывать ли как кликабельную вкладку</summary>
        public bool IsClickable => !IsHeader && !IsModuleHeader && !IsDisabled;

        /// <summary>
        /// Отступ вкладки — для модульных вкладок добавляет левый отступ
        /// </summary>
        public Thickness Indent => IsModuleTab
            ? new Thickness(28, 9, 16, 9)
            : new Thickness(16, 9, 16, 9);

        /// <summary>Выбрана ли вкладка</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }
    }
}