using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.Notes.ViewModels;
using Writersword.Src.Modules.Notes.Resources;

namespace Writersword.Modules.Notes
{
    /// <summary>
    /// Модуль быстрых заметок
    /// Позволяет делать пометки и напоминания во время работы
    /// Заметки сохраняются вместе с проектом
    /// Универсальный модуль - доступен везде
    /// </summary>
    public class NotesModule : BaseModule
    {
        /// <summary>ViewModel модуля заметок</summary>
        private NotesViewModel? _viewModel;

        /// <summary>Тип модуля - Notes</summary>
        public override ModuleType ModuleType => ModuleType.Notes;

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Заметки";

        /// <summary>ViewModel для привязки к View</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Метаданные модуля для UI</summary>
        public override IModuleMetadata Metadata => new NotesMetadata();

        /// <summary>Инициализация модуля - создаём ViewModel</summary>
        public override void Initialize()
        {
            _viewModel = new NotesViewModel();
            Console.WriteLine($"[NotesModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>
        /// Сохранить состояние модуля
        /// Сохраняем текст заметок
        /// </summary>
        public override ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0,
                CustomData = _viewModel?.NoteText // Сохраняем заметки
            };
        }

        /// <summary>
        /// Восстановить состояние модуля
        /// Восстанавливаем текст заметок
        /// </summary>
        /// <param name="state">Сохранённое состояние</param>
        public override void RestoreState(ModuleState state)
        {
            // Восстанавливаем заметки
            if (_viewModel != null && !string.IsNullOrEmpty(state.CustomData))
            {
                _viewModel.NoteText = state.CustomData;
                Console.WriteLine($"[NotesModule] Restored {state.CustomData.Length} characters");
            }
        }

        /// <summary>Создать View заметок</summary>
        public override Avalonia.Controls.Control? CreateView()
        {
            return new Views.NotesView
            {
                DataContext = ViewModel
            };
        }
    }

    /// <summary>Метаданные модуля Notes</summary>
    internal class NotesMetadata : IModuleMetadata
    {
        public ModuleType ModuleType => ModuleType.Notes;

        /// <summary>Название из локализации (.resx)</summary>
        public string DisplayName => NotesStrings.DisplayName;

        /// <summary>Описание из локализации (.resx)</summary>
        public string Description => NotesStrings.Description;

        /// <summary>Иконка (hardcoded, не переводится)</summary>
        public string Icon => "📝";

        /// <summary>Универсальный модуль - доступен везде</summary>
        public bool IsUniversal => true;

        /// <summary>Пустой список = доступен во всех WorkMode</summary>
        public List<WorkModeType> AvailableInWorkModes => new();
    }
}