using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using Writersword.Modules.TextEditor.Contracts;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Views.Dialogs;

namespace Writersword.Modules.TextEditor.ViewModels.Toolbar
{
    /// <summary>
    /// ViewModel контекстной вкладки «Таблица» в Ribbon.
    /// Появляется только когда каретка находится внутри таблицы.
    /// </summary>
    public sealed class RibbonTableTabViewModel : ReactiveObject
    {
        private readonly ITextEditorCommandTarget _target;

        private bool _isByCell;

        /// <summary>
        /// True — режим разбивки ByCell, False — ByRow.
        /// Биндится к тексту/иконке кнопки-тоггла в XAML.
        /// </summary>
        public bool IsByCell
        {
            get => _isByCell;
            private set => this.RaiseAndSetIfChanged(ref _isByCell, value);
        }

        // ── Строки ────────────────────────────────────────────────────────
        public ICommand AddRowAboveCommand { get; }
        public ICommand AddRowBelowCommand { get; }
        public ICommand DeleteRowCommand { get; }
        public ICommand DistributeRowsCommand { get; }

        // ── Столбцы ──────────────────────────────────────────────────────
        public ICommand AddColumnLeftCommand { get; }
        public ICommand AddColumnRightCommand { get; }
        public ICommand DeleteColumnCommand { get; }
        public ICommand DistributeColumnsCommand { get; }

        // ── Таблица целиком ───────────────────────────────────────────────
        public ICommand DeleteTableCommand { get; }
        public ICommand AutoFitCommand { get; }

        // ── Объединение / разбиение ───────────────────────────────────────
        public ICommand MergeCellsCommand { get; }
        public ICommand SplitCellCommand { get; }

        // ── Выравнивание текста ───────────────────────────────────────────
        public ICommand AlignTopLeftCommand { get; }
        public ICommand AlignTopCenterCommand { get; }
        public ICommand AlignTopRightCommand { get; }
        public ICommand AlignMiddleLeftCommand { get; }
        public ICommand AlignMiddleCenterCommand { get; }
        public ICommand AlignMiddleRightCommand { get; }
        public ICommand AlignBottomLeftCommand { get; }
        public ICommand AlignBottomCenterCommand { get; }
        public ICommand AlignBottomRightCommand { get; }

        // ── Заливка ячейки ────────────────────────────────────────────────
        public ICommand SetCellBackgroundNoneCommand { get; }
        public ICommand SetCellBackgroundBlueCommand { get; }
        public ICommand SetCellBackgroundGreenCommand { get; }
        public ICommand SetCellBackgroundYellowCommand { get; }
        public ICommand SetCellBackgroundRedCommand { get; }
        public ICommand SetCellBackgroundGrayCommand { get; }

        // ── Границы ───────────────────────────────────────────────────────
        public ICommand BorderAllCommand { get; }
        public ICommand BorderNoneCommand { get; }
        public ICommand BorderOuterCommand { get; }
        public ICommand BorderInnerCommand { get; }
        public ICommand BorderTopCommand { get; }
        public ICommand BorderBottomCommand { get; }
        public ICommand BorderLeftCommand { get; }
        public ICommand BorderRightCommand { get; }

        // ── Сортировка ────────────────────────────────────────────────────
        public ICommand SortAscCommand { get; }
        public ICommand SortDescCommand { get; }

        // ── Заголовок ─────────────────────────────────────────────────────
        public ICommand RepeatHeaderCommand { get; }

        // ── Режим разбивки ────────────────────────────────────────────────
        public ICommand ToggleSplitModeCommand { get; }

        // ── Метки продолжения ─────────────────────────────────────────────
        public ICommand SetBreakLabelCommand { get; }
        public ICommand SetContinuationLabelCommand { get; }

        public RibbonTableTabViewModel(ITextEditorCommandTarget target)
        {
            _target = target;

            // Строки
            AddRowAboveCommand = ReactiveCommand.Create(() => _target.TableAddRow(above: true));
            AddRowBelowCommand = ReactiveCommand.Create(() => _target.TableAddRow(above: false));
            DeleteRowCommand = ReactiveCommand.Create(() => _target.TableDeleteRow());
            DistributeRowsCommand = ReactiveCommand.Create(() => _target.TableDistributeRows());

            // Столбцы
            AddColumnLeftCommand = ReactiveCommand.Create(() => _target.TableAddColumn(left: true));
            AddColumnRightCommand = ReactiveCommand.Create(() => _target.TableAddColumn(left: false));
            DeleteColumnCommand = ReactiveCommand.Create(() => _target.TableDeleteColumn());
            DistributeColumnsCommand = ReactiveCommand.Create(() => _target.TableDistributeColumns());

            // Таблица целиком
            DeleteTableCommand = ReactiveCommand.Create(() => _target.TableDelete());
            AutoFitCommand = ReactiveCommand.Create(() => _target.TableAutoFit());

            // Объединение / разбиение
            MergeCellsCommand = ReactiveCommand.Create(() => _target.TableMergeCells());
            SplitCellCommand = ReactiveCommand.Create(() => _target.TableSplitCell());

            // Выравнивание — vAlign 0=Top 1=Middle 2=Bottom
            AlignTopLeftCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(0); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Left); });
            AlignTopCenterCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(0); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Center); });
            AlignTopRightCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(0); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Right); });
            AlignMiddleLeftCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(1); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Left); });
            AlignMiddleCenterCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(1); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Center); });
            AlignMiddleRightCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(1); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Right); });
            AlignBottomLeftCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(2); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Left); });
            AlignBottomCenterCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(2); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Center); });
            AlignBottomRightCommand = ReactiveCommand.Create(() => { _target.TableSetCellVAlign(2); _target.TableSetCellHAlign(Writersword.Modules.TextEditor.Models.Styles.TextAlignment.Right); });

            // Заливка
            SetCellBackgroundNoneCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground(null));
            SetCellBackgroundBlueCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#BDD7EE"));
            SetCellBackgroundGreenCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#C6EFCE"));
            SetCellBackgroundYellowCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#FFEB9C"));
            SetCellBackgroundRedCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#FFC7CE"));
            SetCellBackgroundGrayCommand = ReactiveCommand.Create(() => _target.TableSetCellBackground("#D9D9D9"));

            // Границы
            BorderAllCommand = ReactiveCommand.Create(() => { _target.TableSetCellBorder("outer", BorderStyle.Single, 0.5, null); _target.TableSetCellBorder("inner", BorderStyle.Single, 0.5, null); });
            BorderNoneCommand = ReactiveCommand.Create(() => _target.TableSetCellBorder("all", BorderStyle.None, 0, null));
            BorderOuterCommand = ReactiveCommand.Create(() => _target.TableSetCellBorder("outer", BorderStyle.Single, 1.0, null));
            BorderInnerCommand = ReactiveCommand.Create(() => _target.TableSetCellBorder("inner", BorderStyle.Single, 0.5, null));
            BorderTopCommand = ReactiveCommand.Create(() => _target.TableSetCellBorder("top", BorderStyle.Single, 0.5, null));
            BorderBottomCommand = ReactiveCommand.Create(() => _target.TableSetCellBorder("bottom", BorderStyle.Single, 0.5, null));
            BorderLeftCommand = ReactiveCommand.Create(() => _target.TableSetCellBorder("left", BorderStyle.Single, 0.5, null));
            BorderRightCommand = ReactiveCommand.Create(() => _target.TableSetCellBorder("right", BorderStyle.Single, 0.5, null));

            // Сортировка
            SortAscCommand = ReactiveCommand.Create(() => _target.TableSort(-1, ascending: true));
            SortDescCommand = ReactiveCommand.Create(() => _target.TableSort(-1, ascending: false));

            // Заголовок
            RepeatHeaderCommand = ReactiveCommand.Create(() => _target.TableToggleRepeatHeader());

            // Режим разбивки — тоггл с обновлением реактивного свойства IsByCell
            ToggleSplitModeCommand = ReactiveCommand.Create(() =>
            {
                _target.TableToggleSplitMode();
                IsByCell = _target.TableGetSplitModeByCell();
            });

            // Метки разрыва и продолжения с диалогом ввода
            SetBreakLabelCommand = ReactiveCommand.CreateFromTask(SetBreakLabelAsync);
            SetContinuationLabelCommand = ReactiveCommand.CreateFromTask(SetContinuationLabelAsync);
        }

        /// <summary>
        /// Синхронизирует IsByCell из текущей таблицы под кареткой.
        /// Вызывается из TextEditorViewModel.NotifyCaretEnteredTable
        /// чтобы кнопка-тоггл сразу показывала правильный режим.
        /// </summary>
        public void SyncFromTarget()
        {
            IsByCell = _target.TableGetSplitModeByCell();
        }

        // ── Приватные методы ──────────────────────────────────────────────

        /// <summary>
        /// Первый клик — ставит дефолтный текст.
        /// Последующие клики — открывают диалог редактирования.
        /// Пустая строка в диалоге — убирает метку.
        /// </summary>
        private async Task SetBreakLabelAsync()
        {
            string? current = _target.TableGetBreakLabel();
            if (current is null)
            {
                _target.TableSetBreakLabel("Продолжение на следующей странице");
            }
            else
            {
                string? result = await ShowInputDialogAsync(
                    "Надпись разрыва",
                    "Введите текст под таблицей перед разрывом страницы.\nОставьте пустым — убрать надпись.",
                    current);

                if (result is not null)
                    _target.TableSetBreakLabel(string.IsNullOrWhiteSpace(result) ? null : result);
            }
        }

        /// <summary>
        /// Первый клик — ставит дефолтный текст.
        /// Последующие клики — открывают диалог редактирования.
        /// Пустая строка в диалоге — убирает метку.
        /// </summary>
        private async Task SetContinuationLabelAsync()
        {
            string? current = _target.TableGetContinuationLabel();
            if (current is null)
            {
                _target.TableSetContinuationLabel("Таблица (продолжение)");
            }
            else
            {
                string? result = await ShowInputDialogAsync(
                    "Надпись продолжения",
                    "Введите текст над продолжением таблицы на следующей странице.\nОставьте пустым — убрать надпись.",
                    current);

                if (result is not null)
                    _target.TableSetContinuationLabel(string.IsNullOrWhiteSpace(result) ? null : result);
            }
        }

        /// <summary>
        /// Открывает InputDialog (собственный диалог модуля) через главное окно Avalonia.
        /// Не зависит от основного приложения — использует только публичное Avalonia API.
        /// </summary>
        private static async Task<string?> ShowInputDialogAsync(string title, string prompt, string current)
        {
            var lifetime = Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime;

            var owner = lifetime?.MainWindow;
            if (owner is null) return null;

            var dialog = new InputDialog(title, prompt, current);
            return await dialog.ShowDialog<string?>(owner);
        }
    }
}