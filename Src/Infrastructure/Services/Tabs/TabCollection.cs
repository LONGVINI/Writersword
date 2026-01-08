using System;
using System.Collections.ObjectModel;
using System.Linq;
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

                    Console.WriteLine($"[TabCollection] Active tab changed: {_activeTab?.Title ?? "none"}");
                    ActiveTabChanged?.Invoke(_activeTab);
                }
            }
        }

        /// <summary>Событие изменения активной вкладки</summary>
        public event Action<DocumentTabViewModel?>? ActiveTabChanged;

        /// <summary>Конструктор с dependency injection</summary>
        public TabCollection(ISettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        /// <summary>Добавить вкладку в коллекцию</summary>
        public void Add(DocumentTabViewModel tab)
        {
            if (!Tabs.Contains(tab))
            {
                Tabs.Add(tab);
                Console.WriteLine($"[TabCollection] Added tab: {tab.Title}");

                // Сохраняем обновлённый список в настройки
                SaveOpenProjectsToSettings();
            }
        }

        /// <summary>Удалить вкладку из коллекции</summary>
        public void Remove(DocumentTabViewModel tab)
        {
            if (Tabs.Remove(tab))
            {
                Console.WriteLine($"[TabCollection] Removed tab: {tab.Title}");

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
            Console.WriteLine("[TabCollection] Cleared all tabs");

            // Сохраняем пустой список в настройки
            SaveOpenProjectsToSettings();
        }

        /// <summary>
        /// Сохранить список открытых проектов в настройки
        /// Вызывается автоматически при добавлении/удалении вкладок
        /// </summary>
        private void SaveOpenProjectsToSettings()
        {
            Console.WriteLine($"[TabCollection] SaveOpenProjectsToSettings: Tabs.Count = {Tabs.Count}");

            foreach (var tab in Tabs)
            {
                Console.WriteLine($"[TabCollection] Tab FilePath: '{tab.FilePath}'");
            }

            var openPaths = Tabs
                .Select(t => t.FilePath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();

            Console.WriteLine($"[TabCollection] After filtering: openPaths.Count = {openPaths.Count}");

            _settingsService.SaveOpenProjects(openPaths!);
        }
    }
}