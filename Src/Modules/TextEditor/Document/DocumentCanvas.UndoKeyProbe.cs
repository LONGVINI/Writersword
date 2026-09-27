using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Диагностика Ctrl+Z / Ctrl+Y: куда уходит нажатие и дошло ли оно до отмены.
    ///
    /// Нажатие проходит окно раньше полотна. Окно отдаёт его службе горячих клавиш,
    /// а если та ничего не выполнила, помечает Ctrl+Z обработанным, чтобы не сработала
    /// встроенная отмена текстовых полей. В этом случае до полотна клавиша не доходит,
    /// и в логе не остаётся ни строчки — отмена просто молчит.
    ///
    /// Поэтому слушатель стоит на самом окне и видит нажатие дважды: на спуске сразу
    /// после обработчика окна и на подъёме, когда разбор закончен. Во второй раз он
    /// пишет, была ли за это нажатие вызвана отмена полотна.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private TopLevel? _undoProbeTopLevel;

        // Сколько раз полотно входило в отмену и повтор. Сравнение до и после нажатия
        // показывает, дошла ли до них именно эта клавиша.
        private int _undoInvocations;
        private int _redoInvocations;
        private int _undoInvocationsAtKey;
        private int _redoInvocationsAtKey;

        private void AttachUndoKeyProbe()
        {
            DetachUndoKeyProbe();

            _undoProbeTopLevel = TopLevel.GetTopLevel(this);
            if (_undoProbeTopLevel is null) return;

            _undoProbeTopLevel.AddHandler(
                KeyDownEvent, OnUndoProbeTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
            _undoProbeTopLevel.AddHandler(
                KeyDownEvent, OnUndoProbeBubble, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        private void DetachUndoKeyProbe()
        {
            if (_undoProbeTopLevel is null) return;

            _undoProbeTopLevel.RemoveHandler(KeyDownEvent, OnUndoProbeTunnel);
            _undoProbeTopLevel.RemoveHandler(KeyDownEvent, OnUndoProbeBubble);
            _undoProbeTopLevel = null;
        }

        private static bool IsUndoRedoGesture(KeyEventArgs e)
            => e.KeyModifiers == KeyModifiers.Control && (e.Key == Key.Z || e.Key == Key.Y);

        private void OnUndoProbeTunnel(object? sender, KeyEventArgs e)
        {
            if (!IsUndoRedoGesture(e)) return;

            _undoInvocationsAtKey = _undoInvocations;
            _redoInvocationsAtKey = _redoInvocations;

            _logger.Debug(
                "[UNDO-KEY] Ctrl+{Key} на спуске: окно уже пометило обработанным {Handled}; фокус клавиатуры у полотна {CanvasFocus}; в фокусе {Focused}",
                e.Key, e.Handled, IsKeyboardFocusWithin, DescribeFocusedElement());
        }

        private void OnUndoProbeBubble(object? sender, KeyEventArgs e)
        {
            if (!IsUndoRedoGesture(e)) return;

            bool undoCalled = _undoInvocations != _undoInvocationsAtKey;
            bool redoCalled = _redoInvocations != _redoInvocationsAtKey;
            bool reached = e.Key == Key.Z ? undoCalled : redoCalled;

            if (reached)
            {
                _logger.Debug("[UNDO-KEY] Ctrl+{Key} дошло до полотна", e.Key);
                return;
            }

            _logger.Warning(
                "[UNDO-KEY] Ctrl+{Key} не дошло до полотна: помечено обработанным {Handled}; фокус клавиатуры у полотна {CanvasFocus}; в фокусе {Focused}",
                e.Key, e.Handled, IsKeyboardFocusWithin, DescribeFocusedElement());
        }

        /// <summary>
        /// Элемент в фокусе и несколько его предков с метками модулей: по ним видно,
        /// в каком модуле и в каком контроле стоит фокус клавиатуры.
        /// </summary>
        private string DescribeFocusedElement()
        {
            var focused = _undoProbeTopLevel?.FocusManager?.GetFocusedElement();
            if (focused is null) return "ничего";

            var parts = new List<string>();
            Visual? v = focused as Visual;
            for (int i = 0; i < 6 && v is not null; i++)
            {
                if (v is Control ctl)
                {
                    string name = string.IsNullOrEmpty(ctl.Name) ? string.Empty : " #" + ctl.Name;
                    string tag = ctl.Tag is string moduleTag && moduleTag.Length > 0
                        ? " [модуль " + moduleTag + "]"
                        : string.Empty;
                    parts.Add(ctl.GetType().Name + name + tag);
                }
                else
                {
                    parts.Add(v.GetType().Name);
                }

                v = v.GetVisualParent();
            }

            return string.Join(" < ", parts);
        }
    }
}
