using System;
using Writersword.Core.Enums;
using Writersword.Core.Models.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.Notes.ViewModels;

namespace Writersword.Modules.Notes
{
    /// <summary>
    /// Модуль быстрых заметок
    /// Позволяет делать пометки и напоминания во время работы
    /// Заметки сохраняются вместе с проектом
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

        /// <summary>
        /// Инициализация модуля - создаём ViewModel
        /// </summary>
        public override void Initialize()
        {
            _viewModel = new NotesViewModel();
            Console.WriteLine($"[NotesModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>
        /// Сохранить состояние модуля
        /// Сохраняем текст заметок
        /// </summary>
        /// <returns>Состояние с текстом заметок</returns>
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
    }
}