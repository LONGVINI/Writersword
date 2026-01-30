using Avalonia.Controls;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.Notes.ViewModels;
using Writersword.Src.Modules.Notes.Resources;
using Writersword.ViewModels;

namespace Writersword.Modules.Notes
{
    /// <summary>
    /// Модуль заметок
    /// Позволяет вести заметки по проекту
    /// </summary>
    public class NotesModule : BaseModule
    {
        private NotesViewModel? _viewModel;
        private IDisposable? _notesSubscription;

        /// <summary>
        /// Конструктор модуля заметок
        /// </summary>
        /// <param name="instanceId">ID экземпляра модуля (если null - генерируется новый)</param>
        public NotesModule(string? instanceId = null) : base(instanceId)
        {

        }

        /// <summary>Идентификатор модуля</summary>
        public override string ModuleId => "Notes";

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Заметки";

        /// <summary>ViewModel модуля</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Метаданные модуля</summary>
        public override IModuleMetadata Metadata => new NotesMetadata();

        /// <summary>
        /// Инициализация модуля
        /// Создаёт ViewModel и подписывается на изменения заметок
        /// </summary>
        public override void Initialize()
        {
            _viewModel = new NotesViewModel();

            _notesSubscription = _viewModel.WhenAnyValue(x => x.NoteText)
                .Throttle(TimeSpan.FromSeconds(0.5))
                .Subscribe(text =>
                {
                    Console.WriteLine($"[NotesModule {InstanceId}] Notes updated: {text?.Length ?? 0} chars");
                });

            Console.WriteLine($"[NotesModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>
        /// Вызывается при изменении контекста
        /// Заметки остаются редактируемыми в любом режиме
        /// </summary>
        protected override void OnContextChanged(DocumentContext? context)
        {
            Console.WriteLine($"[NotesModule] Context changed - notes remain editable");
        }

        /// <summary>
        /// Получить основные данные модуля (текст заметок)
        /// Возвращает строку с текстом или null если заметки пустые
        /// </summary>
        public override object? GetCustomData()
        {
            var text = _viewModel?.NoteText ?? "";

            if (string.IsNullOrWhiteSpace(text))
                return null;

            return text;
        }

        /// <summary>
        /// Получить сессионные данные (позиция скролла)
        /// </summary>
        public override object? GetSessionData()
        {
            return new
            {
                scrollPosition = 0
            };
        }

        /// <summary>
        /// Установить основные данные модуля (текст заметок)
        /// Вызывается при открытии проекта или переключении версий
        /// </summary>
        public override void SetCustomData(object? data)
        {
            if (_viewModel == null)
            {
                Console.WriteLine($"[NotesModule] SetCustomData called but ViewModel is null (ID: {InstanceId})");
                return;
            }

            if (data is string notes && notes.Length > 0)
            {
                _viewModel.NoteText = notes;
                Console.WriteLine($"[NotesModule] Loaded {notes.Length} characters (ID: {InstanceId})");
            }
            else
            {
                _viewModel.NoteText = "";
                Console.WriteLine($"[NotesModule] Loaded empty notes (ID: {InstanceId})");
            }
        }

        /// <summary>
        /// Установить сессионные данные (позиция скролла)
        /// </summary>
        public override void SetSessionData(object? data)
        {
            Console.WriteLine($"[NotesModule] SessionData set (ID: {InstanceId})");
        }

        /// <summary>
        /// Очистка ресурсов
        /// Отписывается от событий
        /// </summary>
        public override void Dispose()
        {
            _notesSubscription?.Dispose();
            Console.WriteLine($"[NotesModule] Disposed (ID: {InstanceId})");
        }

        /// <summary>
        /// Создать View для модуля
        /// Возвращает NotesView с привязкой к ViewModel
        /// </summary>
        public override Control? CreateView()
        {
            return new Views.NotesView { DataContext = ViewModel };
        }
    }

    /// <summary>
    /// Метаданные модуля заметок
    /// Содержит информацию для отображения в UI
    /// </summary>
    internal class NotesMetadata : IModuleMetadata
    {
        /// <summary>Идентификатор модуля</summary>
        public string ModuleId => "Notes";

        /// <summary>Отображаемое имя (из локализации)</summary>
        public string DisplayName => NotesStrings.DisplayName;

        /// <summary>Описание модуля (из локализации)</summary>
        public string Description => NotesStrings.Description;

        /// <summary>Иконка модуля (emoji)</summary>
        public string Icon => "📝";

        /// <summary>Универсальный модуль (доступен везде)</summary>
        public bool IsUniversal => true;

        /// <summary>Позиция по умолчанию (снизу справа)</summary>
        public PreferredDockPosition DefaultPosition => PreferredDockPosition.BottomRight;
    }
}