using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// Вкладка «Формулы»: новая и готовые формулы, конструкции с галереями заготовок,
    /// наборы знаков и справка по записи. Заготовка или знак открывают окно формулы
    /// уже с ними — дальше формула дописывается там.
    /// </summary>
    public sealed class RibbonFormulaTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        /// <summary>Пустое окно новой формулы.</summary>
        public ICommand InsertFormulaCommand { get; }

        /// <summary>Готовая формула без окна; параметр — запись LaTeX.</summary>
        public ICommand InsertReadyFormulaCommand { get; }

        /// <summary>Окно новой формулы с заготовкой или знаком; параметр — запись LaTeX.</summary>
        public ICommand InsertTemplateCommand { get; }

        /// <summary>Справка по записи формул.</summary>
        public ICommand ShowHelpCommand { get; }

        public RibbonFormulaTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            InsertFormulaCommand = ReactiveCommand.Create(() => _target.InsertFormula());
            InsertReadyFormulaCommand = ReactiveCommand.Create<string>(latex => _target.InsertReadyFormula(latex));
            InsertTemplateCommand = ReactiveCommand.Create<string>(latex => _target.InsertFormulaTemplate(latex));
            ShowHelpCommand = ReactiveCommand.Create(() => _target.ShowFormulaHelp());
        }
    }
}
