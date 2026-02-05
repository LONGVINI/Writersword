using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Services;
using Writersword.Src.Modules.TextEditor.Resources;
using Writersword.ViewModels;

namespace Writersword.Modules.TextEditor
{
    /// <summary>
    /// Модуль текстового редактора
    /// Основной модуль для работы с текстом
    /// </summary>
    public class TextEditorModule : BaseModule
    {
        private readonly ILogger<TextEditorModule> _logger;
        private TextEditorViewModel? _viewModel;
        private IDisposable? _textSubscription;

        /// <summary>
        /// Конструктор модуля текстового редактора
        /// </summary>
        /// <param name="instanceId">ID экземпляра модуля (если null - генерируется новый)</param>
        public TextEditorModule(string? instanceId = null) : base(instanceId)
        {
            _logger = App.Services.GetService<ILogger<TextEditorModule>>()!;
        }

        /// <summary>Идентификатор модуля</summary>
        public override string ModuleId => "TextEditor";

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Text Editor";

        /// <summary>ViewModel модуля</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Метаданные модуля</summary>
        public override IModuleMetadata Metadata => new TextEditorMetadata();

        /// <summary>
        /// Инициализация модуля
        /// Создаёт ViewModel и подписывается на изменения текста
        /// </summary>
        public override void Initialize()
        {
            _logger.LogDebug("Initialize START (ID: {InstanceId})", InstanceId);

            _viewModel = new TextEditorViewModel();

            CreateSubscription();

            _logger.LogDebug("Initialized (ID: {InstanceId})", InstanceId);
        }

        /// <summary>
        /// Создать подписку на изменения текста
        /// </summary>
        private void CreateSubscription()
        {
            _textSubscription?.Dispose();

            _textSubscription = _viewModel.WhenAnyValue(x => x.PlainText)
                .Throttle(TimeSpan.FromSeconds(0.5))
                .Subscribe(text =>
                {
                    _logger.LogDebug("Text updated: {Length} chars (ID: {InstanceId})", text?.Length ?? 0, InstanceId);
                });
        }

        /// <summary>
        /// Вызывается при изменении контекста
        /// Устанавливает режим ReadOnly в зависимости от IsInCompareMode
        /// </summary>
        protected override void OnContextChanged(DocumentContext? context)
        {
            if (context != null && _viewModel != null)
            {
                _viewModel.IsReadOnly = context.IsInCompareMode;
                _logger.LogDebug("Context changed - IsReadOnly: {IsReadOnly}", _viewModel.IsReadOnly);
            }
        }

        /// <summary>
        /// Получить основные данные модуля (текст редактора)
        /// Возвращает строку с текстом или null если редактор пустой
        /// </summary>
        public override object? GetCustomData()
        {
            var text = _viewModel?.PlainText ?? "";

            if (string.IsNullOrWhiteSpace(text))
                return null;

            return text;
        }

        /// <summary>
        /// Получить сессионные данные (позиция курсора, скролл)
        /// </summary>
        public override object? GetSessionData()
        {
            return new
            {
                lastEditTime = DateTime.Now,
                scrollPosition = 0
            };
        }

        /// <summary>
        /// Установить основные данные модуля (текст редактора)
        /// Вызывается при открытии проекта или переключении версий
        /// </summary>
        public override void SetCustomData(object? data)
        {
            if (_viewModel == null)
            {
                _logger.LogWarning("SetCustomData called but ViewModel is null (ID: {InstanceId})", InstanceId);
                return;
            }

            _textSubscription?.Dispose();

            if (data != null)
            {
                string text = "";

                if (data is string str)
                {
                    text = str;
                }
                else if (data is JValue jValue)
                {
                    text = jValue.Value?.ToString() ?? "";
                }
                else
                {
                    text = data.ToString() ?? "";
                }

                _viewModel.LoadDocument(text);
                _logger.LogDebug("Loaded {Length} chars (ID: {InstanceId})", text.Length, InstanceId);
            }
            else
            {
                _viewModel.LoadDocument("");
                _logger.LogDebug("Loaded empty document (ID: {InstanceId})", InstanceId);
            }

            CreateSubscription();
            _logger.LogDebug("Subscription recreated after SetCustomData");
        }

        /// <summary>
        /// Установить сессионные данные (позиция курсора, скролл)
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
            _textSubscription?.Dispose();
            _logger.LogDebug("Disposed (ID: {InstanceId})", InstanceId);
        }

        /// <summary>
        /// Создать View для модуля
        /// Возвращает TextEditorView с привязкой к ViewModel
        /// </summary>
        public override Control? CreateView()
        {
            return new Views.TextEditorView
            {
                DataContext = ViewModel
            };
        }
    }

    /// <summary>
    /// Метаданные модуля текстового редактора
    /// Содержит информацию для отображения в UI
    /// </summary>
    internal class TextEditorMetadata : IModuleMetadata
    {
        /// <summary>Идентификатор модуля</summary>
        public string ModuleId => "TextEditor";

        /// <summary>Отображаемое имя (из локализации)</summary>
        public string DisplayName => TextEditorStrings.DisplayName;

        /// <summary>Описание модуля (из локализации)</summary>
        public string Description => TextEditorStrings.Description;

        /// <summary>Иконка модуля (emoji)</summary>
        public string Icon => "📝";

        /// <summary>Универсальный модуль (доступен везде)</summary>
        public bool IsUniversal => false;

        /// <summary>Позиция по умолчанию (слева)</summary>
        public PreferredDockPosition DefaultPosition => PreferredDockPosition.Left;
    }
}