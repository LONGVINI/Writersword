using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.Synonyms.ViewModels;
using Writersword.Src.Modules.Synonyms.Resources;

namespace Writersword.Modules.Synonyms
{
    /// <summary>
    /// Модуль подбора синонимов
    /// Доступен только в режиме Редактора и Поэзии
    /// </summary>
    public class SynonymsModule : BaseModule
    {
        /// <summary>ViewModel модуля синонимов</summary>
        private SynonymsViewModel? _viewModel;

        /// <summary>Тип модуля - Synonyms</summary>
        public override ModuleType ModuleType => ModuleType.Synonyms;

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Synonyms";

        /// <summary>ViewModel для привязки к View</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Метаданные модуля для UI</summary>
        public override IModuleMetadata Metadata => new SynonymsMetadata();

        /// <summary>Инициализация модуля - создаём ViewModel</summary>
        public override void Initialize()
        {
            _viewModel = new SynonymsViewModel();
            Console.WriteLine($"[SynonymsModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>Сохранить состояние модуля</summary>
        public override ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0,
                CustomData = null
            };
        }

        /// <summary>Восстановить состояние модуля</summary>
        public override void RestoreState(ModuleState state)
        {
            // Восстановление не требуется
        }

        /// <summary>Создать View синонимов</summary>
        public override Avalonia.Controls.Control? CreateView()
        {
            return new Views.SynonymsView
            {
                DataContext = ViewModel
            };
        }
    }

    /// <summary>Метаданные модуля Synonyms</summary>
    internal class SynonymsMetadata : IModuleMetadata
    {
        public ModuleType ModuleType => ModuleType.Synonyms;

        /// <summary>Название из локализации (.resx)</summary>
        public string DisplayName => SynonymsStrings.DisplayName;

        /// <summary>Описание из локализации (.resx)</summary>
        public string Description => SynonymsStrings.Description;

        /// <summary>Иконка (hardcoded, не переводится)</summary>
        public string Icon => "📚";

        public bool IsUniversal => false;

        /// <summary>Доступен в режимах Редактора и Поэзии</summary>
        public List<WorkModeType> AvailableInWorkModes => new()
        {
            WorkModeType.Editor,
            WorkModeType.Poetry
        };
    }
}