using Avalonia.Controls;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Core.Models.Modules;
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

            // Подписка на изменения текста с задержкой (debounce)
            _textSubscription = _viewModel.WhenAnyValue(x => x.PlainText)
                .Throttle(TimeSpan.FromSeconds(0.5))
                .Subscribe(text =>
                {
                    // Сохраняем в проект
                    if (Context?.Project != null)
                    {
                        Context.Project.ModulesData[ModuleId] = text;
                    }

                    Console.WriteLine($"[TextEditorModule {InstanceId}] Text updated: {text?.Length ?? 0} chars");
                });

            Console.WriteLine($"[TextEditorModule] Initialized (ID: {InstanceId})");
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
        /// Сохранить состояние модуля
        /// Возвращает текст редактора в CustomData
        /// </summary>
        public override ModuleState SaveState()
        {
            var text = _viewModel?.PlainText ?? "";

            return new ModuleState
            {
                InstanceId = this.InstanceId,
                CustomData = text,
                SessionData = new
                {
                    lastEditTime = DateTime.Now,
                    scrollPosition = 0
                }
            };
        }

        /// <summary>
        /// Восстановить состояние модуля
        /// Загружает текст из CustomData в редактор
        /// </summary>
        public override void RestoreState(ModuleState state)
        {
            base.RestoreState(state);

            if (_viewModel != null && state.CustomData is string text)
            {
                _viewModel.LoadDocument(text);
                Console.WriteLine($"[TextEditorModule] Restored {text.Length} chars");
            }
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