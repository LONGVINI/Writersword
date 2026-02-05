using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Models.WorkModes;

namespace Writersword.ViewModels.Components
{
    /// <summary>
    /// ViewModel для панели режимов работы (WorkModes)
    /// Отображает кнопки переключения между режимами: Editor, Planning, GameDesign и т.д.
    /// </summary>
    public class WorkModeBarViewModel : ViewModelBase
    {
        private readonly ILogger<WorkModeBarViewModel> _logger;
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

        /// <summary>Функция сохранения порядка WorkModes после drag-and-drop (передаётся из MainWindowViewModel)</summary>
        private Action? _onWorkModesReordered;

        public WorkModeBarViewModel()
        {
            _logger = App.Services.GetService<ILogger<WorkModeBarViewModel>>()!;

            SwitchWorkModeCommand = ReactiveCommand.CreateFromTask<WorkMode>(SwitchWorkModeAsync);

            _logger.LogDebug("Initialized");
        }

        /// <summary>
        /// Установить обработчик переключения WorkMode
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetWorkModeSwitchedHandler(Func<WorkMode, Task> handler)
        {
            _onWorkModeSwitched = handler;
            _logger.LogDebug("WorkMode switch handler set");
        }

        /// <summary>
        /// Установить обработчик сохранения порядка WorkModes после drag-and-drop
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetWorkModesReorderedHandler(Action handler)
        {
            _onWorkModesReordered = handler;
            _logger.LogDebug("WorkModes reordered handler set");
        }

        /// <summary>
        /// Загрузить WorkModes для проекта
        /// Вызывается из MainWindowViewModel при открытии проекта
        /// </summary>
        public void LoadWorkModes(List<WorkMode> workModes)
        {
            WorkModes = workModes;
            ActiveWorkMode = workModes.FirstOrDefault(wm => wm.IsActive);

            _logger.LogDebug("Loaded {Count} WorkModes", workModes.Count);
        }

        /// <summary>
        /// Поменять местами два WorkMode в списке по индексам
        /// Просто меняет внутри списка БЕЗ уведомления UI — UI обновится после SaveWorkModesOrder()
        /// Вызывается из WorkModeDragDropBehavior во время drag
        /// </summary>
        public void SwapWorkModes(int indexA, int indexB)
        {
            if (indexA < 0 || indexA >= WorkModes.Count || indexB < 0 || indexB >= WorkModes.Count)
            {
                _logger.LogWarning("SwapWorkModes: invalid indices {IndexA} <-> {IndexB}", indexA, indexB);
                return;
            }

            var temp = WorkModes[indexA];
            WorkModes[indexA] = WorkModes[indexB];
            WorkModes[indexB] = temp;

            _logger.LogDebug("SwapWorkModes: {IndexA} <-> {IndexB}", indexA, indexB);
        }

        /// <summary>
        /// Сохранить текущий порядок WorkModes после завершения drag-and-drop.
        /// Обновляет Order, триггерит UI обновление новым списком, и сохраняет в workspace.json.
        /// </summary>
        public void SaveWorkModesOrder()
        {
            for (int i = 0; i < WorkModes.Count; i++)
            {
                WorkModes[i].Order = i;
            }

            _logger.LogDebug("Saving WorkModes order:");
            for (int i = 0; i < WorkModes.Count; i++)
            {
                _logger.LogDebug("[{Index}] {Title} (Order={Order})", i, WorkModes[i].Title, WorkModes[i].Order);
            }

            WorkModes = new List<WorkMode>(WorkModes);

            _onWorkModesReordered?.Invoke();
        }

        /// <summary>Переключить WorkMode</summary>
        private async Task SwitchWorkModeAsync(WorkMode newWorkMode)
        {
            if (ActiveWorkMode == newWorkMode)
            {
                _logger.LogDebug("Already in WorkMode: {Title}", newWorkMode.Title);
                return;
            }

            _logger.LogDebug("Switching WorkMode: {OldTitle} → {NewTitle}", ActiveWorkMode?.Title, newWorkMode.Title);

            ActiveWorkMode = newWorkMode;

            if (_onWorkModeSwitched != null)
            {
                await _onWorkModeSwitched(newWorkMode);
            }

            _logger.LogDebug("WorkMode switched to: {Title}", newWorkMode.Title);
        }
    }
}