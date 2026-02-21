using System;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Src.Core.Interfaces.Services.UI;
using Writersword.Views;

namespace Tests.Helpers
{
    /// <summary>
    /// Mock реализация IDialogService для тестов
    /// Эмулирует нажатия кнопок в диалогах без реального показа окон
    /// </summary>
    public class MockDialogService : IDialogService
    {
        /// <summary>
        /// Результат который вернёт следующий диалог с кнопками
        /// Установи перед вызовом ShowMessageAsync
        /// </summary>
        public MessageBoxResult NextMessageBoxResult { get; set; } = MessageBoxResult.OK;

        /// <summary>
        /// Результат который вернёт Recovery диалог
        /// Установи перед вызовом ShowRecoveryDialogAsync
        /// </summary>
        public RecoveryDialogResult NextRecoveryResult { get; set; } = RecoveryDialogResult.None;

        /// <summary>
        /// Путь к файлу который вернёт диалог открытия
        /// Установи перед вызовом OpenFileAsync
        /// </summary>
        public string? NextOpenFilePath { get; set; }

        /// <summary>
        /// Путь к файлу который вернёт диалог сохранения
        /// Установи перед вызовом SaveFileAsync
        /// </summary>
        public string? NextSaveFilePath { get; set; }

        // =============================================================================
        // СЧЁТЧИКИ ВЫЗОВОВ (для проверки в тестах)
        // =============================================================================

        /// <summary>Сколько раз вызывался ShowMessageAsync</summary>
        public int ShowMessageCallCount { get; private set; }

        /// <summary>Сколько раз вызывался ShowRecoveryDialogAsync</summary>
        public int ShowRecoveryDialogCallCount { get; private set; }

        /// <summary>Сколько раз вызывался OpenFileAsync</summary>
        public int OpenFileCallCount { get; private set; }

        /// <summary>Сколько раз вызывался SaveFileAsync</summary>
        public int SaveFileCallCount { get; private set; }

        /// <summary>Последний заголовок переданный в ShowMessageAsync</summary>
        public string? LastMessageTitle { get; private set; }

        /// <summary>Последнее сообщение переданное в ShowMessageAsync</summary>
        public string? LastMessageText { get; private set; }

        /// <summary>Последний тип переданный в ShowMessageAsync</summary>
        public MessageBoxType? LastMessageType { get; private set; }

        /// <summary>Последние кнопки переданные в ShowMessageAsync</summary>
        public MessageBoxButtons? LastMessageButtons { get; private set; }

        // =============================================================================
        // РЕАЛИЗАЦИЯ IDialogService
        // =============================================================================

        /// <summary>
        /// Диалог открытия файла
        /// Возвращает NextOpenFilePath
        /// </summary>
        public Task<string?> OpenFileAsync()
        {
            OpenFileCallCount++;
            Console.WriteLine($"[MockDialogService] OpenFileAsync called (count: {OpenFileCallCount})");
            Console.WriteLine($"[MockDialogService] Returning: {NextOpenFilePath ?? "null"}");

            return Task.FromResult(NextOpenFilePath);
        }

        /// <summary>
        /// Диалог сохранения файла
        /// Возвращает NextSaveFilePath
        /// </summary>
        public Task<string?> SaveFileAsync(string? defaultFileName = null)
        {
            SaveFileCallCount++;
            Console.WriteLine($"[MockDialogService] SaveFileAsync called (count: {SaveFileCallCount})");
            Console.WriteLine($"[MockDialogService] Returning: {NextSaveFilePath ?? "null"}");

            return Task.FromResult(NextSaveFilePath);
        }

        /// <summary>
        /// Показать сообщение (без кнопок)
        /// Просто логирует вызов
        /// </summary>
        public Task ShowMessageAsync(string title, string message)
        {
            ShowMessageCallCount++;
            LastMessageTitle = title;
            LastMessageText = message;

            Console.WriteLine($"[MockDialogService] ShowMessageAsync called (count: {ShowMessageCallCount})");
            Console.WriteLine($"[MockDialogService] Title: {title}");
            Console.WriteLine($"[MockDialogService] Message: {message}");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Показать сообщение с кнопками
        /// Возвращает NextMessageBoxResult
        /// </summary>
        public Task<MessageBoxResult> ShowMessageAsync(
            string title,
            string message,
            MessageBoxType type,
            MessageBoxButtons buttons)
        {
            ShowMessageCallCount++;
            LastMessageTitle = title;
            LastMessageText = message;
            LastMessageType = type;
            LastMessageButtons = buttons;

            Console.WriteLine($"[MockDialogService] ShowMessageAsync called (count: {ShowMessageCallCount})");
            Console.WriteLine($"[MockDialogService] Title: {title}");
            Console.WriteLine($"[MockDialogService] Message: {message}");
            Console.WriteLine($"[MockDialogService] Type: {type}");
            Console.WriteLine($"[MockDialogService] Buttons: {buttons}");
            Console.WriteLine($"[MockDialogService] Returning: {NextMessageBoxResult}");

            return Task.FromResult(NextMessageBoxResult);
        }

        /// <summary>
        /// Показать диалог восстановления из кеша
        /// Возвращает NextRecoveryResult
        /// </summary>
        public Task<RecoveryDialogResult> ShowRecoveryDialogAsync(DateTime cacheDate, DateTime saveDate)
        {
            ShowRecoveryDialogCallCount++;

            Console.WriteLine($"[MockDialogService] ShowRecoveryDialogAsync called (count: {ShowRecoveryDialogCallCount})");
            Console.WriteLine($"[MockDialogService] Cache date: {cacheDate}");
            Console.WriteLine($"[MockDialogService] Save date: {saveDate}");
            Console.WriteLine($"[MockDialogService] Returning: {NextRecoveryResult}");

            return Task.FromResult(NextRecoveryResult);
        }

        // =============================================================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ДЛЯ ТЕСТОВ
        // =============================================================================

        /// <summary>
        /// Сбросить все счётчики и состояния
        /// Вызывай в [SetUp] теста
        /// </summary>
        public void Reset()
        {
            NextMessageBoxResult = MessageBoxResult.OK;
            NextRecoveryResult = RecoveryDialogResult.None;
            NextOpenFilePath = null;
            NextSaveFilePath = null;

            ShowMessageCallCount = 0;
            ShowRecoveryDialogCallCount = 0;
            OpenFileCallCount = 0;
            SaveFileCallCount = 0;

            LastMessageTitle = null;
            LastMessageText = null;
            LastMessageType = null;
            LastMessageButtons = null;

            Console.WriteLine("[MockDialogService] Reset");
        }

        /// <summary>
        /// Проверить что ShowMessageAsync был вызван с определёнными параметрами
        /// </summary>
        public bool WasMessageShown(string titleContains, string messageContains)
        {
            var titleMatch = LastMessageTitle?.Contains(titleContains, StringComparison.OrdinalIgnoreCase) ?? false;
            var messageMatch = LastMessageText?.Contains(messageContains, StringComparison.OrdinalIgnoreCase) ?? false;

            return titleMatch && messageMatch;
        }

        /// <summary>
        /// Проверить что ShowMessageAsync был вызван с определёнными кнопками
        /// </summary>
        public bool WasMessageShownWithButtons(MessageBoxButtons buttons)
        {
            return LastMessageButtons == buttons;
        }
    }
}