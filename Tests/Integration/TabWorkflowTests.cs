using FluentAssertions;
using Moq;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Models.Project;
using Writersword.Src.Core.Interfaces.Services;
using Writersword.Src.Core.Interfaces.Services.Storage;
using Writersword.Src.Infrastructure.Services.Storage;
using Writersword.Src.Infrastructure.Services.Tabs;
using Writersword.ViewModels;
using System;

namespace Tests.Integration
{
    /*
     * ============================================================================
     * ИНТЕГРАЦИОННЫЕ ТЕСТЫ: Работа с вкладками (TabCollection + SettingsService)
     * ============================================================================
     * 
     * Проверяемые сценарии:
     * 
     * [OpenAndCloseAllTabs_ShouldClearSettings]
     *    - Открыть 3 вкладки
     *    - Закрыть все вкладки
     *    - Проверить что TabCollection пустой
     *    - Проверить что settings.json содержит пустой список
     * 
     * [OpenTabs_RestartApp_ShouldRestoreTabs]
     *    - Открыть 2 вкладки
     *    - Эмулировать перезапуск приложения (новый экземпляр SettingsService)
     *    - Проверить что вкладки восстановились из settings.json
     * 
     * [OpenTabs_CloseOne_Restart_ShouldRestoreOnlyRemaining]
     *    - Открыть 3 вкладки
     *    - Закрыть 1 вкладку (оставить 2)
     *    - Эмулировать перезапуск
     *    - Проверить что восстановились только 2 оставшиеся вкладки
     * 
     * ============================================================================
     */

    [TestFixture]
    public class TabWorkflowTests
    {
        /// <summary>
        /// Создаёт mock IAutoSaveService (пустышку для тестов)
        /// </summary>
        private IAutoSaveService CreateMockAutoSaveService()
        {
            var mock = new Mock<IAutoSaveService>();
            return mock.Object;
        }

        /// <summary>
        /// Тест проверяет что после закрытия всех вкладок:
        /// 1. Коллекция вкладок пустая
        /// 2. Список путей в settings.json тоже пустой
        /// </summary>
       [Test]
public void OpenAndCloseAllTabs_ShouldClearSettings()
{
    var settingsService = new SettingsService();
    settingsService.Load();
    var tabCollection = new TabCollection(settingsService);
    var autoSave = CreateMockAutoSaveService();

    var tab1 = new DocumentTabViewModel(
        new ProjectFile { Title = "Проект 1" },
        "project1.writersword",
        autoSaveService: autoSave);
    var tab2 = new DocumentTabViewModel(
        new ProjectFile { Title = "Проект 2" },
        "project2.writersword",
        autoSaveService: autoSave);
    var tab3 = new DocumentTabViewModel(
        new ProjectFile { Title = "Проект 3" },
        "project3.writersword",
        autoSaveService: autoSave);

    // ОТЛАДКА: Выводим FilePath
    Console.WriteLine($"DEBUG: tab1.FilePath = '{tab1.FilePath}'");
    Console.WriteLine($"DEBUG: tab2.FilePath = '{tab2.FilePath}'");
    Console.WriteLine($"DEBUG: tab3.FilePath = '{tab3.FilePath}'");

    tabCollection.Add(tab1);
    tabCollection.Add(tab2);
    tabCollection.Add(tab3);

    // ОТЛАДКА: Выводим что в settings
    Console.WriteLine($"DEBUG: settingsService.OpenProjectPaths.Count = {settingsService.OpenProjectPaths.Count}");
    foreach (var path in settingsService.OpenProjectPaths)
    {
        Console.WriteLine($"DEBUG: Path in settings: '{path}'");
    }

    tabCollection.Tabs.Should().HaveCount(3);
    settingsService.OpenProjectPaths.Should().HaveCount(3);

    tabCollection.Remove(tab1);
    tabCollection.Remove(tab2);
    tabCollection.Remove(tab3);

    tabCollection.Tabs.Should().BeEmpty();
    settingsService.OpenProjectPaths.Should().BeEmpty();
}

        /// <summary>
        /// Тест проверяет что после перезапуска приложения:
        /// 1. Пути к вкладкам восстанавливаются из settings.json
        /// 2. Все открытые вкладки сохраняются
        /// </summary>
        [Test]
        public void OpenTabs_RestartApp_ShouldRestoreTabs()
        {
            // === СЕССИЯ 1 (до перезапуска) ===

            var settingsService1 = new SettingsService();
            settingsService1.Load();
            var tabCollection1 = new TabCollection(settingsService1);
            var autoSave = CreateMockAutoSaveService();

            // Создаём 2 тестовые вкладки с абсолютными путями
            var tab1 = new DocumentTabViewModel(
                new ProjectFile { Title = "Test1" },
                "E:\\test1.writersword",
                autoSaveService: autoSave);
            var tab2 = new DocumentTabViewModel(
                new ProjectFile { Title = "Test2" },
                "E:\\test2.writersword",
                autoSaveService: autoSave);

            // Добавляем вкладки (пути автоматически сохраняются в settings.json)
            tabCollection1.Add(tab1);
            tabCollection1.Add(tab2);

            // Проверяем что пути сохранились
            settingsService1.OpenProjectPaths.Should().Contain("E:\\test1.writersword");
            settingsService1.OpenProjectPaths.Should().Contain("E:\\test2.writersword");

            // === СЕССИЯ 2 (после перезапуска) ===

            // Создаём НОВЫЙ экземпляр сервиса (эмулируем перезапуск приложения)
            var settingsService2 = new SettingsService();
            settingsService2.Load();

            // Проверяем что пути восстановились из файла
            settingsService2.OpenProjectPaths.Should().HaveCount(2);
            settingsService2.OpenProjectPaths.Should().Contain("E:\\test1.writersword");
            settingsService2.OpenProjectPaths.Should().Contain("E:\\test2.writersword");

            // Очищаем settings.json после теста (чтобы не мешать другим тестам)
            settingsService2.SaveOpenProjects(new List<string>());
        }

        /// <summary>
        /// Тест проверяет что после закрытия одной вкладки и перезапуска:
        /// 1. Восстанавливаются только оставшиеся открытые вкладки
        /// 2. Закрытые вкладки НЕ восстанавливаются
        /// </summary>
        [Test]
        public void OpenTabs_CloseOne_Restart_ShouldRestoreOnlyRemaining()
        {
            // === СЕССИЯ 1 (до перезапуска) ===

            var settingsService1 = new SettingsService();
            settingsService1.Load();
            var tabCollection1 = new TabCollection(settingsService1);
            var autoSave = CreateMockAutoSaveService();

            // Создаём 3 тестовые вкладки
            var tab1 = new DocumentTabViewModel(new ProjectFile(), "E:\\tab1.writersword", autoSaveService: autoSave);
            var tab2 = new DocumentTabViewModel(new ProjectFile(), "E:\\tab2.writersword", autoSaveService: autoSave);
            var tab3 = new DocumentTabViewModel(new ProjectFile(), "E:\\tab3.writersword", autoSaveService: autoSave);

            // Добавляем все 3 вкладки
            tabCollection1.Add(tab1);
            tabCollection1.Add(tab2);
            tabCollection1.Add(tab3);

            // Проверяем что все 3 пути сохранились
            settingsService1.OpenProjectPaths.Should().HaveCount(3);

            // Закрываем ОДНУ вкладку (вторую)
            tabCollection1.Remove(tab2);

            // Проверяем что осталось только 2 пути
            settingsService1.OpenProjectPaths.Should().HaveCount(2);
            settingsService1.OpenProjectPaths.Should().NotContain("E:\\tab2.writersword");

            // === СЕССИЯ 2 (после перезапуска) ===

            var settingsService2 = new SettingsService();
            settingsService2.Load();

            // Проверяем что восстановились ТОЛЬКО 2 оставшиеся вкладки
            settingsService2.OpenProjectPaths.Should().HaveCount(2);
            settingsService2.OpenProjectPaths.Should().Contain("E:\\tab1.writersword");
            settingsService2.OpenProjectPaths.Should().Contain("E:\\tab3.writersword");
            settingsService2.OpenProjectPaths.Should().NotContain("E:\\tab2.writersword");

            // Очищаем settings.json после теста
            settingsService2.SaveOpenProjects(new List<string>());
        }
    }
}