using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.Notes.ViewModels;
using Writersword.Src.Core.Services;
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
        private readonly ILogger<NotesModule> _logger;
        private NotesViewModel? _viewModel;
        private IDisposable? _notesSubscription;

        /// <summary>
        /// Конструктор модуля заметок
        /// </summary>
        /// <param name="instanceId">ID экземпляра модуля (если null - генерируется новый)</param>
        public NotesModule(string? instanceId = null) : base(instanceId)
        {
            _logger = App.Services.GetService<ILogger<NotesModule>>()!;
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
                    _logger.LogDebug("Notes updated: {Length} chars (ID: {InstanceId})", text?.Length ?? 0, InstanceId);
                });

            _logger.LogDebug("Initialized (ID: {InstanceId})", InstanceId);
        }

        /// <summary>
        /// Вызывается при изменении контекста
        /// Заметки остаются редактируемыми в любом режиме
        /// </summary>
        protected override void OnContextChanged(DocumentContext? context)
        {
            _logger.LogDebug("Context changed - notes remain editable");
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
                _logger.LogWarning("SetCustomData called but ViewModel is null (ID: {InstanceId})", InstanceId);
                return;
            }

            if (data is string notes && notes.Length > 0)
            {
                _viewModel.NoteText = notes;
                _logger.LogDebug("Loaded {Length} characters (ID: {InstanceId})", notes.Length, InstanceId);
            }
            else
            {
                _viewModel.NoteText = "";
                _logger.LogDebug("Loaded empty notes (ID: {InstanceId})", InstanceId);
            }
        }

        /// <summary>
        /// Установить сессионные данные (позиция скролла)
        /// </summary>
        public override void SetSessionData(object? data)
        {
            _logger.LogDebug("SessionData set (ID: {InstanceId})", InstanceId);
        }

        /// <summary>
        /// Очистка ресурсов
        /// Отписывается от событий
        /// </summary>
        public override void Dispose()
        {
            _notesSubscription?.Dispose();
            _logger.LogDebug("Disposed (ID: {InstanceId})", InstanceId);
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