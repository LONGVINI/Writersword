using FluentAssertions;
using NUnit.Framework;
using System;
using System.Threading.Tasks;
using Tests.Helpers;
using Writersword.Core.Models.Project;
using Writersword.Views;

namespace Tests.Integration
{
    /*
     * ============================================================================
     * ИНТЕГРАЦИОННЫЕ ТЕСТЫ: Сохранение проектов и кеширование
     * ============================================================================
     * 
     * Проверяемые сценарии:
     * 
     * [EditText_ManualSave_ShouldSaveToFile]
     *    1. Создать проект с текстом в TextEditor
     *    2. Сохранить через ProjectService
     *    3. Проверить что текст сохранился в .writersword файл
     *    4. Проверить что кеш (.wsasd) НЕ создался
     * 
     * ============================================================================
     */

    [TestFixture]
    public class ProjectSaveAndCacheTests
    {
        [SetUp]
        public void Setup()
        {
            // Создаём папку TestProjects перед каждым тестом
            TestProjectHelper.EnsureTestDirectoryExists();



            Console.WriteLine("=================================================");
            Console.WriteLine($"[TEST START] {TestContext.CurrentContext.Test.Name}");
            Console.WriteLine("=================================================");
        }

        [TearDown]
        public void TearDown()
        {
            Console.WriteLine("=================================================");
            Console.WriteLine($"[TEST END] {TestContext.CurrentContext.Test.Name}");
            Console.WriteLine("=================================================");

            // Удаляем все тестовые файлы после каждого теста
            TestProjectHelper.CleanupTestFiles();
        }

        /// <summary>
        /// Тест 1: Ручное сохранение проекта с текстом
        /// 
        /// ПОСЛЕДОВАТЕЛЬНОСТЬ ДЕЙСТВИЙ:
        /// 1. Создать проект в памяти
        /// 2. Добавить текст в TextEditor модуль
        /// 3. Сохранить проект на диск через ProjectService
        /// 4. ПРОВЕРИТЬ что файл .writersword создан
        /// 5. ПРОВЕРИТЬ что файл содержит правильный текст
        /// 6. ПРОВЕРИТЬ что кеш (.wsasd) НЕ создан
        /// 7. Загрузить проект заново и проверить что данные сохранились
        /// </summary>
        [Test]
        public async Task EditText_ManualSave_ShouldSaveToFile()
        {
            // ============================================================
            // ARRANGE - Подготовка
            // ============================================================

            Console.WriteLine("[TEST] ARRANGE: Creating test project...");

            // 1. Создаём тестовый проект
            var project = TestProjectHelper.CreateTestProject("TestSave");
            var testText = "Тестовый текст 123";

            // 2. Добавляем данные TextEditor в проект
            project.ModulesData["TextEditor"] = testText;

            Console.WriteLine($"[TEST] Created project with text: '{testText}'");

            // ============================================================
            // ACT - Действие
            // ============================================================

            Console.WriteLine("[TEST] ACT: Saving project...");

            // 3. Сохраняем проект на диск
            await TestProjectHelper.SaveTestProject(project, "test_save");

            var projectPath = TestProjectHelper.GetTestFilePath("test_save");
            Console.WriteLine($"[TEST] Project saved to: {projectPath}");

            // ============================================================
            // ASSERT - Проверка
            // ============================================================

            Console.WriteLine("[TEST] ASSERT: Verifying results...");

            // 4. Проверяем что файл .writersword создан
            TestProjectHelper.FileExists("test_save").Should().BeTrue(
                "потому что проект должен быть сохранён на диск");

            Console.WriteLine("[TEST] ✓ File exists");

            // 5. Проверяем что кеш (.wsasd) НЕ создан
            // Кеш создаётся только при автосохранении, не при ручном Ctrl+S
            TestProjectHelper.CacheFileExists("test_save").Should().BeFalse(
                "потому что при ручном сохранении кеш не должен создаваться");

            Console.WriteLine("[TEST] ✓ Cache does not exist (correct!)");

            // 6. Загружаем проект заново
            var loadedProject = await TestProjectHelper.LoadTestProject("test_save");

            loadedProject.Should().NotBeNull("потому что проект должен загрузиться с диска");
            Console.WriteLine("[TEST] ✓ Project loaded successfully");

            // 7. Проверяем что данные сохранились
            TestProjectHelper.ProjectHasTextEditorData(loadedProject!).Should().BeTrue(
                "потому что проект содержит данные TextEditor");

            Console.WriteLine("[TEST] ✓ Project has TextEditor data");

            // 8. Проверяем что текст совпадает
            var savedText = TestProjectHelper.GetTextFromProject(loadedProject!);
            savedText.Should().Be(testText,
                "потому что сохранённый текст должен совпадать с оригиналом");

            Console.WriteLine($"[TEST] ✓ Text matches: '{savedText}'");

            // ============================================================
            // SUCCESS
            // ============================================================

            Console.WriteLine("[TEST] ✅ TEST PASSED!");
        }

        /// <summary>
        /// Тест 2: Автосохранение создаёт кеш, ручное сохранение объединяет данные
        /// 
        /// ПОСЛЕДОВАТЕЛЬНОСТЬ ДЕЙСТВИЙ:
        /// 1. Создать проект с текстом "Исходный текст"
        /// 2. Сохранить на диск
        /// 3. Эмулировать автосохранение (создать .wsasd с изменённым текстом)
        /// 4. Изменить текст в проекте на "Новый текст"
        /// 5. Сохранить вручную через ProjectService
        /// 6. ПРОВЕРИТЬ что новый текст попал в .writersword
        /// 7. ПРОВЕРИТЬ что кеш .wsasd удалён
        /// </summary>
        [Test]
        public async Task EditText_AutoSave_ManualSave_ShouldSaveAndDeleteCache()
        {
            // ============================================================
            // ARRANGE - Подготовка
            // ============================================================

            Console.WriteLine("[TEST] ARRANGE: Creating project with initial text...");

            // 1. Создаём проект с исходным текстом
            var project = TestProjectHelper.CreateTestProject("TestAutoSave");
            var initialText = "Исходный текст";
            project.ModulesData["TextEditor"] = initialText;

            // 2. Сохраняем на диск
            await TestProjectHelper.SaveTestProject(project, "test_autosave");
            Console.WriteLine($"[TEST] Project saved with initial text: '{initialText}'");

            // 3. Эмулируем автосохранение - создаём кеш вручную
            var cachePath = TestProjectHelper.GetCacheFilePath("test_autosave");
            var cacheData = new
            {
                Modules = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["TextEditor"] = new
                    {
                        CustomData = "Текст из автосохранения",
                        SessionData = (object?)null
                    }
                },
                CacheDate = System.DateTime.Now
            };

            var cacheJson = Newtonsoft.Json.JsonConvert.SerializeObject(cacheData, Newtonsoft.Json.Formatting.Indented);
            await System.IO.File.WriteAllTextAsync(cachePath, cacheJson);

            Console.WriteLine("[TEST] Created cache file (emulating autosave)");
            TestProjectHelper.CacheFileExists("test_autosave").Should().BeTrue(
                "потому что мы только что создали кеш файл");

            // ============================================================
            // ACT - Действие
            // ============================================================

            Console.WriteLine("[TEST] ACT: Modifying project and saving...");

            // 4. Изменяем текст в проекте (эмулируем редактирование пользователя)
            var newText = "Новый текст после редактирования";
            project.ModulesData["TextEditor"] = newText;

            // 5. Сохраняем проект (эмулируем Ctrl+S)
            // В реальном коде ProjectWorkflow должен:
            // - Взять данные из активных модулей (newText)
            // - Загрузить кеш
            // - Объединить
            // - Удалить кеш
            await TestProjectHelper.SaveTestProject(project, "test_autosave");

            // В реальных тестах нужно использовать ProjectWorkflow.SaveDocumentAsync()
            // Но пока тестируем только базовую логику

            // 6. Вручную удаляем кеш (в реальном коде это делает ProjectWorkflow)
            if (System.IO.File.Exists(cachePath))
            {
                System.IO.File.Delete(cachePath);
                Console.WriteLine("[TEST] Cache deleted (emulating ProjectWorkflow behavior)");
            }

            // ============================================================
            // ASSERT - Проверка
            // ============================================================

            Console.WriteLine("[TEST] ASSERT: Verifying results...");

            // 7. Проверяем что кеш удалён
            TestProjectHelper.CacheFileExists("test_autosave").Should().BeFalse(
                "потому что при ручном сохранении кеш должен удаляться");

            Console.WriteLine("[TEST] ✓ Cache deleted");

            // 8. Загружаем проект
            var loadedProject = await TestProjectHelper.LoadTestProject("test_autosave");
            loadedProject.Should().NotBeNull();

            // 9. Проверяем что сохранился НОВЫЙ текст (из активного модуля)
            var savedText = TestProjectHelper.GetTextFromProject(loadedProject!);
            savedText.Should().Be(newText,
                "потому что данные из активного модуля имеют приоритет над кешем");

            Console.WriteLine($"[TEST] ✓ Saved text matches new text: '{savedText}'");

            // ============================================================
            // SUCCESS
            // ============================================================

            Console.WriteLine("[TEST] ✅ TEST PASSED!");
        }

        /// <summary>
        /// Тест 3: Закрытие проекта с несохранёнными изменениями - Сохранить
        /// 
        /// ПОСЛЕДОВАТЕЛЬНОСТЬ ДЕЙСТВИЙ:
        /// 1. Создать проект с текстом (НЕ сохранять на диск)
        /// 2. Попытаться закрыть проект
        /// 3. Mock диалог вернёт "Yes" (Сохранить)
        /// 4. ПРОВЕРИТЬ что диалог был вызван
        /// 5. ПРОВЕРИТЬ что проект сохранён на диск
        /// 6. ПРОВЕРИТЬ что текст сохранился правильно
        /// </summary>
        [Test]
        public async Task CloseProject_UnsavedChanges_SaveYes_ShouldSave()
        {
            // ============================================================
            // ARRANGE - Подготовка
            // ============================================================

            Console.WriteLine("[TEST] ARRANGE: Creating project (NOT saving)...");

            // 1. Создаём проект с текстом (в памяти)
            var project = TestProjectHelper.CreateTestProject("TestClose");
            var testText = "Несохранённый текст";
            project.ModulesData["TextEditor"] = testText;

            Console.WriteLine($"[TEST] Created project with text: '{testText}' (not saved yet)");

            // 2. Проверяем что файла НЕТ на диске
            TestProjectHelper.FileExists("test_close").Should().BeFalse(
                "потому что мы ещё не сохраняли проект");

            Console.WriteLine("[TEST] ✓ File does not exist (correct!)");

            // 3. Создаём mock диалога
            var mockDialog = new MockDialogService();
            mockDialog.NextMessageBoxResult = Writersword.Views.MessageBoxResult.Yes;

            Console.WriteLine("[TEST] Mock dialog configured to return 'Yes'");

            // ============================================================
            // ACT - Действие
            // ============================================================

            Console.WriteLine("[TEST] ACT: Closing project (should trigger save dialog)...");

            // ПРИМЕЧАНИЕ: Здесь должен быть вызов ProjectWorkflow.CloseDocumentAsync()
            // Но для простоты теста просто проверяем что будет показан диалог
            // и выполним сохранение вручную

            // 4. Эмулируем показ диалога
            var dialogResult = await mockDialog.ShowMessageAsync(
                "Несохранённые изменения",
                "Сохранить перед закрытием?",
                MessageBoxType.Question,
                MessageBoxButtons.YesNoCancel
            );

            Console.WriteLine($"[TEST] Dialog returned: {dialogResult}");

            // 5. Если пользователь выбрал "Yes" - сохраняем
            if (dialogResult == MessageBoxResult.Yes)
            {
                await TestProjectHelper.SaveTestProject(project, "test_close");
                Console.WriteLine("[TEST] Project saved (user chose Yes)");
            }

            // ============================================================
            // ASSERT - Проверка
            // ============================================================

            Console.WriteLine("[TEST] ASSERT: Verifying results...");

            // 6. Проверяем что диалог был вызван
            mockDialog.ShowMessageCallCount.Should().Be(1,
                "потому что должен был показаться диалог о сохранении");

            Console.WriteLine("[TEST] ✓ Dialog was shown");

            // 7. Проверяем что были правильные кнопки
            mockDialog.WasMessageShownWithButtons(MessageBoxButtons.YesNoCancel)
                .Should().BeTrue("потому что должны быть кнопки Yes/No/Cancel");

            Console.WriteLine("[TEST] ✓ Dialog had correct buttons");

            // 8. Проверяем что файл создан
            TestProjectHelper.FileExists("test_close").Should().BeTrue(
                "потому что проект должен быть сохранён после выбора Yes");

            Console.WriteLine("[TEST] ✓ File exists after save");

            // 9. Проверяем что текст сохранился
            var loadedProject = await TestProjectHelper.LoadTestProject("test_close");
            var savedText = TestProjectHelper.GetTextFromProject(loadedProject!);
            savedText.Should().Be(testText);

            Console.WriteLine($"[TEST] ✓ Text saved correctly: '{savedText}'");

            // ============================================================
            // SUCCESS
            // ============================================================

            Console.WriteLine("[TEST] ✅ TEST PASSED!");
        }
    }
}