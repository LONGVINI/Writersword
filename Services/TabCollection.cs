using System;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Services.Interfaces;
using Writersword.ViewModels;

namespace Writersword.Services
{
    /// <summary>
    /// Реализация сервиса управления коллекцией вкладок
    /// </summary>
    public class TabCollection : ITabCollection
    {
        private DocumentTabViewModel? _activeTab;

        public ObservableCollection<DocumentTabViewModel> Tabs { get; } = new();

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

        public event Action<DocumentTabViewModel?>? ActiveTabChanged;

        public void Add(DocumentTabViewModel tab)
        {
            if (!Tabs.Contains(tab))
            {
                Tabs.Add(tab);
                Console.WriteLine($"[TabCollection] Added tab: {tab.Title}");
            }
        }

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
            }
        }

        public DocumentTabViewModel? FindByPath(string filePath)
        {
            return Tabs.FirstOrDefault(t => t.FilePath == filePath);
        }

        public void Clear()
        {
            Tabs.Clear();
            ActiveTab = null;
            Console.WriteLine("[TabCollection] Cleared all tabs");
        }
    }
}