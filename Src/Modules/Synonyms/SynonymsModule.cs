using Avalonia.Controls;
using System;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Core.Models.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.Synonyms.ViewModels;
using Writersword.Src.Modules.Synonyms.Resources;
using Writersword.ViewModels;

namespace Writersword.Modules.Synonyms
{
    /// <summary>
    /// Модуль синонимов
    /// Помогает подбирать синонимы для слов
    /// </summary>
    public class SynonymsModule : BaseModule
    {
        private SynonymsViewModel? _viewModel;

        /// <summary>Идентификатор модуля</summary>
        public override string ModuleId => "Synonyms";

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Synonyms";

        /// <summary>ViewModel модуля</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Метаданные модуля</summary>
        public override IModuleMetadata Metadata => new SynonymsMetadata();

        /// <summary>
        /// Инициализация модуля
        /// Создаёт ViewModel синонимов
        /// </summary>
        public override void Initialize()
        {
            _viewModel = new SynonymsViewModel();
            Console.WriteLine($"[SynonymsModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>
        /// Вызывается при изменении контекста
        /// Модуль-помощник не требует действий при смене контекста
        /// </summary>
        protected override void OnContextChanged(DocumentContext? context)
        {
            Console.WriteLine($"[SynonymsModule] Context changed - no action needed (helper module)");
        }

        /// <summary>
        /// Сохранить состояние модуля
        /// Модуль синонимов не сохраняет своё состояние
        /// </summary>
        public override ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0,
                CustomData = null
            };
        }

        /// <summary>
        /// Восстановить состояние модуля
        /// Модуль синонимов не восстанавливает своё состояние
        /// </summary>
        public override void RestoreState(ModuleState state)
        {
            // Вызываем базовый метод (сбрасывает IsDirty)
            base.RestoreState(state);
        }

        /// <summary>
        /// Создать View для модуля
        /// Возвращает SynonymsView с привязкой к ViewModel
        /// </summary>
        public override Control? CreateView()
        {
            return new Views.SynonymsView { DataContext = ViewModel };
        }
    }

    /// <summary>
    /// Метаданные модуля синонимов
    /// Содержит информацию для отображения в UI
    /// </summary>
    internal class SynonymsMetadata : IModuleMetadata
    {
        /// <summary>Идентификатор модуля</summary>
        public string ModuleId => "Synonyms";

        /// <summary>Отображаемое имя (из локализации)</summary>
        public string DisplayName => SynonymsStrings.DisplayName;

        /// <summary>Описание модуля (из локализации)</summary>
        public string Description => SynonymsStrings.Description;

        /// <summary>Иконка модуля (emoji)</summary>
        public string Icon => "📚";

        /// <summary>Универсальный модуль (доступен везде)</summary>
        public bool IsUniversal => false;

        /// <summary>Позиция по умолчанию (справа как вкладка)</summary>
        public PreferredDockPosition DefaultPosition => PreferredDockPosition.RightAsTab;
    }
}