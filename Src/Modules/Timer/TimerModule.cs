using System;
using Writersword.Core.Enums;
using Writersword.Core.Models.Modules;
using Writersword.Modules.Common;
using Writersword.Modules.Timer.ViewModels;

namespace Writersword.Modules.Timer
{
    /// <summary>
    /// Модуль таймера работы
    /// Отслеживает сколько времени пользователь работает над документом
    /// Полезно для статистики и мотивации
    /// </summary>
    public class TimerModule : BaseModule
    {
        /// <summary>ViewModel модуля таймера</summary>
        private TimerViewModel? _viewModel;

        /// <summary>Тип модуля - Timer</summary>
        public override ModuleType ModuleType => ModuleType.Timer;

        /// <summary>Заголовок модуля</summary>
        public override string Title { get; set; } = "Таймер";

        /// <summary>ViewModel для привязки к View</summary>
        public override object? ViewModel => _viewModel;

        /// <summary>
        /// Инициализация модуля - создаём ViewModel и запускаем таймер
        /// </summary>
        public override void Initialize()
        {
            _viewModel = new TimerViewModel();
            Console.WriteLine($"[TimerModule] Initialized (ID: {InstanceId})");
        }

        /// <summary>
        /// Сохранить состояние модуля
        /// Сохраняем сколько секунд прошло
        /// </summary>
        /// <returns>Состояние с количеством секунд</returns>
        public override ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0,
                CustomData = _viewModel?.ElapsedSeconds.ToString() // Сохраняем время
            };
        }

        /// <summary>
        /// Восстановить состояние модуля
        /// Восстанавливаем время таймера
        /// </summary>
        /// <param name="state">Сохранённое состояние</param>
        public override void RestoreState(ModuleState state)
        {
            // Восстанавливаем время
            if (_viewModel != null && int.TryParse(state.CustomData, out int seconds))
            {
                _viewModel.ElapsedSeconds = seconds;
                Console.WriteLine($"[TimerModule] Restored time: {seconds} seconds");
            }
        }

        /// <summary>
        /// Уничтожение модуля - останавливаем таймер
        /// Важно! Иначе будет утечка памяти
        /// </summary>
        public override void Dispose()
        {
            _viewModel?.Dispose(); // Останавливаем таймер
            base.Dispose();
            Console.WriteLine($"[TimerModule] Disposed (ID: {InstanceId})");
        }
    }
}