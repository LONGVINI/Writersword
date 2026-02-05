using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Core.Interfaces.WorkFlows;
using Writersword.ViewModels;

namespace Writersword.Src.Infrastructure.Services.Tabs
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
                    // Деактивируем старую вкладку
                    if (_activeTab != null)
                        _activeTab.IsActive = false;

                    _activeTab = value;

                    // Активируем новую вкладку
                    if (_activeTab != null)
                        _activeTab.IsActive = true;

                    _logger.LogDebug("Active tab changed: {Title}", _activeTab?.Title ?? "none");
                    ActiveTabChanged?.Invoke(_activeTab);
                }
            }
        }

        /// <summary>Событие изменения активной вкладки</summary>
        public event Action<DocumentTabViewModel?>? ActiveTabChanged;

        /// <summary>Конструктор с dependency injection</summary>
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

                // Сохраняем обновлённый список в настройки
                SaveOpenProjectsToSettings();
            }
        }

        /// <summary>Удалить вкладку из коллекции</summary>
        public void Remove(DocumentTabViewModel tab)
        {
            if (Tabs.Remove(tab))
            {
                _logger.LogDebug("Removed tab: {Title}", tab.Title);

                // Если удалили активную вкладку - активируем другую
                if (ActiveTab == tab)
                {
                    ActiveTab = Tabs.FirstOrDefault();
                }

                // Сохраняем обновлённый список в настройки
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

            // Сохраняем пустой список в настройки
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
            // FileStorage хранится в tab или в ProjectWorkflow
            // Нужно посмотреть где именно у тебя он хранится
            // ВРЕМЕННО возвращаю null, ты заменишь на реальный код
            return null; // TODO: вернуть реальный FileStorage
        }

        /// <summary>
        /// Получить ProjectFile для указанного таба
        /// </summary>
        public ProjectFile? GetProjectForTab(DocumentTabViewModel tab)
        {
            // ProjectFile должен быть в табе
            // ВРЕМЕННО возвращаю null, ты заменишь на реальный код
            return null; // TODO: вернуть реальный Project
        }
    }
}