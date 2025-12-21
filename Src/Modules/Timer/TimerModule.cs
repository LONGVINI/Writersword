using System;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.Timer.ViewModels;
using Writersword.Src.Modules.Timer.Resources;

namespace Writersword.Modules.Timer
{
    /// <summary>
    /// Модуль таймера работы
    /// Универсальный модуль - доступен во всех WorkMode
    /// </summary>
    public class TimerModule : BaseModule
    {
        /// <summary>ViewModel таймера</summary>
        private TimerViewModel? _viewModel;

        /// <summary>Тип модуля - Timer</summary>
        public override ModuleType ModuleType => ModuleType.Timer;

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Timer";

        /// <summary>ViewModel для привязки к View</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Метаданные модуля для UI</summary>
        public override IModuleMetadata Metadata => new TimerMetadata();

        /// <summary>Инициализация модуля - создаём ViewModel</summary>
        public override void Initialize()
        {
            _viewModel = new TimerViewModel();
            Console.WriteLine($"[TimerModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>Сохранить состояние модуля</summary>
        public override ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0,
                CustomData = null // TODO: Сохранить состояние таймера
            };
        }

        /// <summary>Восстановить состояние модуля</summary>
        public override void RestoreState(ModuleState state)
        {
            // TODO: Восстановить состояние таймера
        }

        /// <summary>Создать View таймера</summary>
        public override Avalonia.Controls.Control? CreateView()
        {
            return new Views.TimerView
            {
                DataContext = ViewModel
            };
        }
    }

    /// <summary>Метаданные модуля Timer</summary>
    internal class TimerMetadata : IModuleMetadata
    {
        public ModuleType ModuleType => ModuleType.Timer;

        /// <summary>Название из локализации (.resx)</summary>
        public string DisplayName => TimerStrings.DisplayName;

        /// <summary>Описание из локализации (.resx)</summary>
        public string Description => TimerStrings.Description;

        /// <summary>Иконка (hardcoded, не переводится)</summary>
        public string Icon => "⏱️";

        /// <summary>Универсальный модуль - доступен везде</summary>
        public bool IsUniversal => true;

        /// <summary>Пустой список = доступен во всех WorkMode</summary>
        public List<WorkModeType> AvailableInWorkModes => new();
    }
}