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
        /// Установить обработчик сохранения порядка WorkModes после drag-and-drop
        /// Вызывается из MainWindowViewModel после создания
        /// </summary>
        public void SetWorkModesReorderedHandler(Action handler)
        {
            _onWorkModesReordered = handler;
            Console.WriteLine("[WorkModeBarViewModel] WorkModes reordered handler set");
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

        /// <summary>
        /// Поменять местами два WorkMode в списке по индексам
        /// Просто меняет внутри списка БЕЗ уведомления UI — UI обновится после SaveWorkModesOrder()
        /// Вызывается из WorkModeDragDropBehavior во время drag
        /// </summary>
        public void SwapWorkModes(int indexA, int indexB)
        {
            if (indexA < 0 || indexA >= WorkModes.Count || indexB < 0 || indexB >= WorkModes.Count)
            {
                Console.WriteLine($"[WorkModeBarViewModel] SwapWorkModes: invalid indices {indexA} <-> {indexB}");
                return;
            }

            var temp = WorkModes[indexA];
            WorkModes[indexA] = WorkModes[indexB];
            WorkModes[indexB] = temp;

            Console.WriteLine($"[WorkModeBarViewModel] SwapWorkModes: {indexA} <-> {indexB}");
        }

        /// <summary>
        /// Сохранить текущий порядок WorkModes после завершения drag-and-drop.
        /// Обновляет Order, триггерит UI обновление новым списком, и сохраняет в workspace.json.
        /// </summary>
        public void SaveWorkModesOrder()
        {
            // Обновляем Order у каждого WorkMode согласно текущей позиции
            for (int i = 0; i < WorkModes.Count; i++)
            {
                WorkModes[i].Order = i;
            }

            Console.WriteLine($"[WorkModeBarViewModel] Saving WorkModes order:");
            for (int i = 0; i < WorkModes.Count; i++)
            {
                Console.WriteLine($"[WorkModeBarViewModel]   [{i}] {WorkModes[i].Title} (Order={WorkModes[i].Order})");
            }

            // Создаём новый список чтобы RaiseAndSetIfChanged сработал
            // и ItemsControl пересоздал кнопки в новом порядке
            // Это вызывается ПОСЛЕ завершения drag, поэтому безопасно
            WorkModes = new List<WorkMode>(WorkModes);

            // Уведомляем MainWindowViewModel для сохранения в workspace.json
            _onWorkModesReordered?.Invoke();
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

            // Обновляем локальное состояние
            ActiveWorkMode = newWorkMode;

            // Уведомляем MainWindowViewModel для перестройки UI
            // MainWindowViewModel сам вызовет WorkspaceController.SwitchWorkMode()
            if (_onWorkModeSwitched != null)
            {
                await _onWorkModeSwitched(newWorkMode);
            }

            Console.WriteLine($"[WorkModeBarViewModel] WorkMode switched to: {newWorkMode.Title}");
        }
    }
}