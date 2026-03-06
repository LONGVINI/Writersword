using Microsoft.Extensions.Logging;
using ReactiveUI;
using System.Reactive;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.ViewModels.Components.MenuBar
{
    public partial class MenuBarViewModel
    {
        // ── Команды: Edit ──────────────────────────────────────────────────────

        public ReactiveCommand<Unit, Unit> UndoCommand { get; private set; } = null!;
        public ReactiveCommand<Unit, Unit> RedoCommand { get; private set; } = null!;

        /// <summary>
        /// Вызвать из конструктора MenuBarViewModel после остальных команд.
        /// </summary>
        private void InitializeEditCommands()
        {
            UndoCommand = ReactiveCommand.Create(Undo);
            RedoCommand = ReactiveCommand.Create(Redo);
        }

        // ── Реализация ─────────────────────────────────────────────────────────

        private void Undo()
        {
            var module = GetFocusedUndoableModule();
            if (module == null)
            {
                _logger.LogDebug("Undo: no focused undoable module");
                return;
            }

            if (!module.CanUndo)
            {
                _logger.LogDebug("Undo: nothing to undo in {Module}", module.GetType().Name);
                return;
            }

            _logger.LogDebug("Undo: {Description}", module.UndoDescription);
            module.Undo();
        }

        private void Redo()
        {
            var module = GetFocusedUndoableModule();
            if (module == null)
            {
                _logger.LogDebug("Redo: no focused undoable module");
                return;
            }

            if (!module.CanRedo)
            {
                _logger.LogDebug("Redo: nothing to redo in {Module}", module.GetType().Name);
                return;
            }

            _logger.LogDebug("Redo: {Description}", module.RedoDescription);
            module.Redo();
        }

        // ── Вспомогательные ───────────────────────────────────────────────────

        private IUndoableModule? GetFocusedUndoableModule()
        {
            var mainVm = _mainViewModelProvider?.Invoke();
            return mainVm?.GetFocusedUndoableModule();
        }
    }
}