using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;

namespace Writersword.Infrastructure.Services.Tabs
{
    /// <summary>
    /// Реализация сервиса управления коллекцией вкладок
    /// Автоматически сохраняет список открытых вкладок в настройки
    /// </summary>
    public class TabCollection : ITabCollection
    {
        private readonly ILogger<TabCollection> _logger;
        private readonly ISettingsService _settingsService;
        private DocumentTabViewModel? _activeTab;

        /// <summary>Список всех открытых вкладок</summary>
        public ObservableCollection<DocumentTabViewModel> Tabs { get; } = new();

        /// <summary>Активная вкладка (текущая)</summary>
        public DocumentTabViewModel? ActiveTab
        {
            get => _activeTab;
            set
            {
                if (_activeTab != value)
                {
                    var previousTab = _activeTab;

                    if (_activeTab != null)
                        _activeTab.IsActive = false;

                    _activeTab = value;

                    if (_activeTab != null)
                        _activeTab.IsActive = true;

                    _logger.LogDebug("Active tab changed: {NewTitle} (previous: {PreviousTitle})",
                        _activeTab?.Title ?? "none",
                        previousTab?.Title ?? "none");

                    ActiveTabChanged?.Invoke(_activeTab, previousTab);
                }
            }
        }

        /// <summary>
        /// Событие изменения активной вкладки
        /// Передаёт (newTab, previousTab)
        /// </summary>
        public event Action<DocumentTabViewModel?, DocumentTabViewModel?>? ActiveTabChanged;

        /// <summary>Конструктор</summary>
        public TabCollection(ISettingsService settingsService)
        {
            _logger = App.Services.GetService<ILogger<TabCollection>>()!;
            _settingsService = settingsService;
        }

        /// <summary>Добавить вкладку в коллекцию</summary>
        public void Add(DocumentTabViewModel tab)
        {
            if (!Tabs.Contains(tab))
            {
                Tabs.Add(tab);
                _logger.LogDebug("Added tab: {Title}", tab.Title);

                // КРИТИЧЕСКИ ВАЖНО: Установить как активную если это первая вкладка
                if (Tabs.Count == 1 && ActiveTab == null)
                {
                    _logger.LogDebug("First tab added, setting as active: {Title}", tab.Title);
                    ActiveTab = tab;
                }

                SaveOpenProjectsToSettings();
            }
        }

        /// <summary>Удалить вкладку из коллекции</summary>
        public void Remove(DocumentTabViewModel tab)
        {
            if (Tabs.Remove(tab))
            {
                _logger.LogDebug("Removed tab: {Title}", tab.Title);

                if (ActiveTab == tab)
                {
                    ActiveTab = Tabs.FirstOrDefault();
                }

                SaveOpenProjectsToSettings();
            }
        }

        /// <summary>Найти вкладку по пути к файлу проекта</summary>
        public DocumentTabViewModel? FindByPath(string filePath)
        {
            return Tabs.FirstOrDefault(t => t.FilePath == filePath);
        }

        /// <summary>Очистить все вкладки</summary>
        public void Clear()
        {
            Tabs.Clear();
            ActiveTab = null;
            _logger.LogDebug("Cleared all tabs");

            SaveOpenProjectsToSettings();
        }

        /// <summary>
        /// Сохранить список открытых проектов в настройки
        /// Вызывается автоматически при добавлении/удалении вкладок
        /// </summary>
        private void SaveOpenProjectsToSettings()
        {
            _logger.LogDebug("SaveOpenProjectsToSettings: Tabs.Count = {Count}", Tabs.Count);

            foreach (var tab in Tabs)
            {
                _logger.LogDebug("Tab FilePath: '{FilePath}'", tab.FilePath);
            }

            var openPaths = Tabs
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();

            _logger.LogDebug("After filtering: openPaths.Count = {Count}", openPaths.Count);

            _settingsService.SaveOpenProjects(openPaths!);
        }

        /// <summary>
        /// Получить FileStorage для указанного таба
        /// </summary>
        public IProjectFileStorage? GetFileStorageForTab(DocumentTabViewModel tab)
        {
            return null;
        }

        /// <summary>
        /// Получить ProjectFile для указанного таба
        /// </summary>
        public ProjectFile? GetProjectForTab(DocumentTabViewModel tab)
        {
            return null;
        }
    }
}