using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Models.WorkModes;
using Writersword.Src.Core.Interfaces.WorkModes;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для панели режимов работы (WorkModes)
    /// Отображает кнопки переключения между режимами: Editor, Planning, GameDesign и т.д.
    /// </summary>
    public class WorkModeBarViewModel : ViewModelBase
    {
        private readonly IWorkModeService _workModeService;
        private List<WorkMode> _workModes = new();
        private WorkMode? _activeWorkMode;

        /// <summary>Список всех WorkModes для текущего проекта</summary>
        public List<WorkMode> WorkModes
        {
            get => _workModes;
            set => this.RaiseAndSetIfChanged(ref _workModes, value);
        }

        /// <summary>Активный WorkMode</summary>
        public WorkMode? ActiveWorkMode
        {
            get => _activeWorkMode;
            set => this.RaiseAndSetIfChanged(ref _activeWorkMode, value);
        }

        /// <summary>Команда переключения WorkMode</summary>
        public ReactiveCommand<WorkMode, Unit> SwitchWorkModeCommand { get; }

        /// <summary>Функция переключения WorkMode (передаётся из MainWindowViewModel)</summary>
        private Func<WorkMode, Task>? _onWorkModeSwitched;

        public WorkModeBarViewModel(IWorkModeService workModeService)
        {
            _workModeService = workModeService;

            // Команда переключения WorkMode
            SwitchWorkModeCommand = ReactiveCommand.CreateFromTask<WorkMode>(SwitchWorkModeAsync);

            Console.WriteLine("[WorkModeBarViewModel] Initialized");
        }

        /// <summary>
        /// Установить обработчик переключения WorkMode
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetWorkModeSwitchedHandler(Func<WorkMode, Task> handler)
        {
            _onWorkModeSwitched = handler;
            Console.WriteLine("[WorkModeBarViewModel] WorkMode switch handler set");
        }

        /// <summary>
        /// Загрузить WorkModes для проекта
        /// Вызывается из MainWindowViewModel при открытии проекта
        /// </summary>
        public void LoadWorkModes(List<WorkMode> workModes)
        {
            WorkModes = workModes;
            ActiveWorkMode = workModes.FirstOrDefault(wm => wm.IsActive);

            Console.WriteLine($"[WorkModeBarViewModel] Loaded {workModes.Count} WorkModes");
        }

        /// <summary>Переключить WorkMode</summary>
        private async Task SwitchWorkModeAsync(WorkMode newWorkMode)
        {
            if (ActiveWorkMode == newWorkMode)
            {
                Console.WriteLine($"[WorkModeBarViewModel] Already in WorkMode: {newWorkMode.Title}");
                return;
            }

            Console.WriteLine($"[WorkModeBarViewModel] Switching WorkMode: {ActiveWorkMode?.Title} → {newWorkMode.Title}");

            // Активируем новый WorkMode через сервис
            _workModeService.SetActiveWorkMode(newWorkMode);

            // Обновляем локальное состояние
            ActiveWorkMode = newWorkMode;

            // Уведомляем MainWindowViewModel для перестройки UI
            if (_onWorkModeSwitched != null)
            {
                await _onWorkModeSwitched(newWorkMode);
            }

            Console.WriteLine($"[WorkModeBarViewModel] WorkMode switched to: {newWorkMode.Title}");
        }
    }
}