using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Src.Modules.TextEditor.Resources;

namespace Writersword.Modules.TextEditor
{
    /// <summary>
    /// Модуль текстового редактора
    /// Обёртка над TextEditorViewModel для интеграции с модульной системой
    /// Этот модуль нельзя закрыть (IsCloseable = false)
    /// </summary>
    public class TextEditorModule : BaseModule
    {
        /// <summary>ViewModel текстового редактора</summary>
        private TextEditorViewModel? _viewModel;

        /// <summary>Тип модуля - TextEditor</summary>
        public override ModuleType ModuleType => ModuleType.TextEditor;

        /// <summary>Заголовок модуля (отображается в UI)</summary>
        public override string Title { get; set; } = "Text Editor";

        /// <summary>ViewModel для привязки к View</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Редактор нельзя закрыть - это основной модуль</summary>
        public override bool IsCloseable => false;

        /// <summary>Метаданные модуля для UI</summary>
        public override IModuleMetadata Metadata => new TextEditorMetadata();

        /// <summary>
        /// Инициализация модуля - создаём ViewModel
        /// Вызывается при первом создании модуля
        /// </summary>
        public override void Initialize()
        {
            _viewModel = new TextEditorViewModel();
            Console.WriteLine($"[TextEditorModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>
        /// Сохранить состояние модуля для записи в файл проекта
        /// Сохраняем текущий текст документа
        /// </summary>
        /// <returns>Состояние модуля с текстом документа</returns>
        public override ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0, // TODO: Позже добавим сохранение позиции скролла
                CustomData = _viewModel?.PlainText // Сохраняем текст
            };
        }

        /// <summary>
        /// Восстановить состояние модуля при загрузке проекта
        /// Загружаем сохранённый текст в редактор
        /// </summary>
        /// <param name="state">Сохранённое состояние модуля</param>
        public override void RestoreState(ModuleState state)
        {
            // Если есть сохранённый текст - загружаем его
            if (_viewModel != null && !string.IsNullOrEmpty(state.CustomData))
            {
                _viewModel.LoadDocument(state.CustomData);
                Console.WriteLine($"[TextEditorModule] Restored {state.CustomData.Length} characters");
            }
        }

        /// <summary>Создать View текстового редактора</summary>
        public override Avalonia.Controls.Control? CreateView()
        {
            return new Views.TextEditorView
            {
                DataContext = ViewModel
            };
        }

    }

    /// <summary>Метаданные модуля TextEditor</summary>
    internal class TextEditorMetadata : IModuleMetadata
    {
        public ModuleType ModuleType => ModuleType.TextEditor;

        /// <summary>Название из локализации (.resx)</summary>
        public string DisplayName => TextEditorStrings.DisplayName;

        /// <summary>Описание из локализации (.resx)</summary>
        public string Description => TextEditorStrings.Description;

        /// <summary>Иконка (hardcoded, не переводится)</summary>
        public string Icon => "📝";

        public bool IsUniversal => false;

        /// <summary>Доступен только в режиме Редактора</summary>
        public List<WorkModeType> AvailableInWorkModes => new()
        {
            WorkModeType.Editor
        };
    }
}