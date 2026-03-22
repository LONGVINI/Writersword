using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel контекстной вкладки «Таблица» в Ribbon.
    /// Появляется только когда каретка находится внутри таблицы.
    ///
    /// Группы команд:
    ///   • Строки — добавить/удалить строку
    ///   • Столбцы — добавить/удалить столбец
    ///   • Таблица — удалить всю таблицу
    /// </summary>
    public sealed class RibbonTableTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        // ── Строки ────────────────────────────────────────────────────────
        public ICommand AddRowAboveCommand { get; }
        public ICommand AddRowBelowCommand { get; }
        public ICommand DeleteRowCommand { get; }

        // ── Столбцы ──────────────────────────────────────────────────────
        public ICommand AddColumnLeftCommand { get; }
        public ICommand AddColumnRightCommand { get; }
        public ICommand DeleteColumnCommand { get; }

        // ── Таблица целиком ───────────────────────────────────────────────
        public ICommand DeleteTableCommand { get; }

        public RibbonTableTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            AddRowAboveCommand = ReactiveCommand.Create(() => _target.TableAddRow(above: true));
            AddRowBelowCommand = ReactiveCommand.Create(() => _target.TableAddRow(above: false));
            DeleteRowCommand = ReactiveCommand.Create(() => _target.TableDeleteRow());

            AddColumnLeftCommand = ReactiveCommand.Create(() => _target.TableAddColumn(left: true));
            AddColumnRightCommand = ReactiveCommand.Create(() => _target.TableAddColumn(left: false));
            DeleteColumnCommand = ReactiveCommand.Create(() => _target.TableDeleteColumn());

            DeleteTableCommand = ReactiveCommand.Create(() => _target.TableDelete());
        }
    }
}