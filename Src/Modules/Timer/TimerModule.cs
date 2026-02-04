using Avalonia.Controls;
using System;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.Timer.ViewModels;
using Writersword.Src.Modules.Timer.Resources;

namespace Writersword.Modules.Timer
{
    /// <summary>
    /// Модуль таймера
    /// Отслеживает время работы над проектом
    /// </summary>
    public class TimerModule : BaseModule
    {
        private TimerViewModel? _viewModel;

        /// <summary>
        /// Конструктор модуля таймера
        /// </summary>
        /// <param name="instanceId">ID экземпляра модуля (если null - генерируется новый)</param>
        public TimerModule(string? instanceId = null) : base(instanceId)
        {

        }

        /// <summary>Идентификатор модуля</summary>
        public override string ModuleId => "Timer";

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Timer";

        /// <summary>ViewModel модуля</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>Метаданные модуля</summary>
        public override IModuleMetadata Metadata => new TimerMetadata();

        /// <summary>
        /// Инициализация модуля
        /// Создаёт ViewModel таймера
        /// </summary>
        public override void Initialize()
        {
            _viewModel = new TimerViewModel();
            Console.WriteLine($"[TimerModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>
        /// Вызывается при изменении контекста
        /// Таймер продолжает работать при смене контекста
        /// </summary>
        protected override void OnContextChanged(DocumentContext? context)
        {
            Console.WriteLine($"[TimerModule] Context changed - timer continues running");
        }

        /// <summary>
        /// Получить основные данные модуля
        /// Таймер пока не сохраняет данные
        /// </summary>
        public override object? GetCustomData()
        {
            return null;
        }

        /// <summary>
        /// Получить сессионные данные модуля
        /// Таймер пока не сохраняет сессионные данные
        /// </summary>
        public override object? GetSessionData()
        {
            return null;
        }

        /// <summary>
        /// Создать View для модуля
        /// Возвращает TimerView с привязкой к ViewModel
        /// </summary>
        public override Control? CreateView()
        {
            return new Views.TimerView { DataContext = ViewModel };
        }
    }

    /// <summary>
    /// Метаданные модуля таймера
    /// Содержит информацию для отображения в UI
    /// </summary>
    internal class TimerMetadata : IModuleMetadata
    {
        /// <summary>Идентификатор модуля</summary>
        public string ModuleId => "Timer";

        /// <summary>Отображаемое имя (из локализации)</summary>
        public string DisplayName => TimerStrings.DisplayName;

        /// <summary>Описание модуля (из локализации)</summary>
        public string Description => TimerStrings.Description;

        /// <summary>Иконка модуля (emoji)</summary>
        public string Icon => "⏱️";

        /// <summary>Универсальный модуль (доступен везде)</summary>
        public bool IsUniversal => true;

        /// <summary>Позиция по умолчанию (справа как вкладка)</summary>
        public PreferredDockPosition DefaultPosition => PreferredDockPosition.RightAsTab;
    }
}