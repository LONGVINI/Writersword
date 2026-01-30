using Avalonia.Controls;
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
        private TextEditorViewModel? _viewModel;
        private IDisposable? _textSubscription;

        /// <summary>
        /// Конструктор модуля текстового редактора
        /// </summary>
        /// <param name="instanceId">ID экземпляра модуля (если null - генерируется новый)</param>
        public TextEditorModule(string? instanceId = null) : base(instanceId)
        {

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
            Console.WriteLine($"[TextEditorModule] Initialize START (ID: {InstanceId})");

            _viewModel = new TextEditorViewModel();

            CreateSubscription();

            Console.WriteLine($"[TextEditorModule] Initialized (ID: {InstanceId})");
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
                    Console.WriteLine($"[TextEditorModule {InstanceId}] Text updated: {text?.Length ?? 0} chars");
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
                Console.WriteLine($"[TextEditorModule] Context changed - IsReadOnly: {_viewModel.IsReadOnly}");
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
                Console.WriteLine($"[TextEditorModule] SetCustomData called but ViewModel is null (ID: {InstanceId})");
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
                Console.WriteLine($"[TextEditorModule] Loaded {text.Length} chars (ID: {InstanceId})");
            }
            else
            {
                _viewModel.LoadDocument("");
                Console.WriteLine($"[TextEditorModule] Loaded empty document (ID: {InstanceId})");
            }

            CreateSubscription();
            Console.WriteLine($"[TextEditorModule] Subscription recreated after SetCustomData");
        }

        /// <summary>
        /// Установить сессионные данные (позиция курсора, скролл)
        /// </summary>
        public override void SetSessionData(object? data)
        {
            Console.WriteLine($"[TextEditorModule] SessionData set (ID: {InstanceId})");
        }

        /// <summary>
        /// Очистка ресурсов
        /// Отписывается от событий
        /// </summary>
        public override void Dispose()
        {
            _textSubscription?.Dispose();
            Console.WriteLine($"[TextEditorModule] Disposed (ID: {InstanceId})");
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

        /// <summary>Описание модуля (из локализации)</summary>а
        public string Description => TextEditorStrings.Description;

        /// <summary>Иконка модуля (emoji)</summary>
        public string Icon => "📝";

        /// <summary>Универсальный модуль (доступен везде)</summary>
        public bool IsUniversal => false;

        /// <summary>Позиция по умолчанию (слева)</summary>
        public PreferredDockPosition DefaultPosition => PreferredDockPosition.Left;
    }
}