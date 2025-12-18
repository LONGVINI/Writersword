using System;
using Writersword.Core.Enums;
using Writersword.Core.Models.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.Synonyms.ViewModels;

namespace Writersword.Modules.Synonyms
{
    /// <summary>
    /// Модуль поиска синонимов
    /// Помогает писателю находить альтернативные слова
    /// Пока это заглушка с примерами, позже добавим реальный API
    /// </summary>
    public class SynonymsModule : BaseModule
    {
        /// <summary>ViewModel модуля синонимов</summary>
        private SynonymsViewModel? _viewModel;

        /// <summary>Тип модуля - Synonyms</summary>
        public override ModuleType ModuleType => ModuleType.Synonyms;

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Синонимы";

        /// <summary>ViewModel для привязки к View</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>
        /// Инициализация модуля - создаём ViewModel
        /// </summary>
        public override void Initialize()
        {
            _viewModel = new SynonymsViewModel();
            Console.WriteLine($"[SynonymsModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>
        /// Сохранить состояние модуля
        /// Сохраняем последний поисковый запрос
        /// </summary>
        /// <returns>Состояние с поисковым запросом</returns>
        public override ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0,
                CustomData = _viewModel?.SearchText // Сохраняем что искал пользователь
            };
        }

        /// <summary>
        /// Восстановить состояние модуля
        /// Восстанавливаем последний поисковый запрос
        /// </summary>
        /// <param name="state">Сохранённое состояние</param>
        public override void RestoreState(ModuleState state)
        {
            // Восстанавливаем поисковый запрос
            if (_viewModel != null && !string.IsNullOrEmpty(state.CustomData))
            {
                _viewModel.SearchText = state.CustomData;
                Console.WriteLine($"[SynonymsModule] Restored search: {state.CustomData}");
            }
        }
    }
}