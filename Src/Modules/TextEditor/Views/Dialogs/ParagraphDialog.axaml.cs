using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Writersword.Modules.TextEditor.Models.Styles;
using StyleAlignment = Writersword.Modules.TextEditor.Models.Styles.TextAlignment;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Модальное окно "Абзац" (аналог диалога Word). Вкладка "Отступы и интервалы":
    /// выравнивание, уровень структуры, отступы, первая строка, интервалы до/после, междустрочный.
    /// Результат — отредактированный ParagraphProperties через Close(result); null при отмене.
    /// Отступы в окне задаются в сантиметрах, интервалы — в пунктах (как в Word). Модель хранит
    /// всё в пунктах, поэтому отступы конвертируются туда-обратно.
    /// </summary>
    public partial class ParagraphDialog : Window
    {
        private const double PtPerCm = 28.3464567;

        public ParagraphDialog()
        {
            InitializeComponent();
        }

        public ParagraphDialog(ParagraphProperties current) : this()
        {
            var okBtn = this.FindControl<Button>("OkBtn")!;
            var cancelBtn = this.FindControl<Button>("CancelBtn")!;
            okBtn.Click += OnOk;
            cancelBtn.Click += OnCancel;

            var firstLineMode = this.FindControl<ComboBox>("FirstLineModeCombo")!;
            var lineSpacing = this.FindControl<ComboBox>("LineSpacingCombo")!;
            firstLineMode.SelectionChanged += (_, _) => UpdateEnabledStates();
            lineSpacing.SelectionChanged += (_, _) => UpdateEnabledStates();

            LoadFrom(current);
            UpdateEnabledStates();
        }

        // ── Загрузка значений из модели ───────────────────────────────────
        private void LoadFrom(ParagraphProperties p)
        {
            this.FindControl<ComboBox>("AlignmentCombo")!.SelectedIndex = (int)(p.Alignment ?? StyleAlignment.Left);

            int lvl = p.OutlineLevel;
            this.FindControl<ComboBox>("LevelCombo")!.SelectedIndex = lvl >= 0 && lvl <= 9 ? lvl : 0;

            this.FindControl<NumericUpDown>("LeftIndentBox")!.Value = (decimal)PtToCm(p.LeftIndent ?? 0);
            this.FindControl<NumericUpDown>("RightIndentBox")!.Value = (decimal)PtToCm(p.RightIndent ?? 0);

            var firstLineMode = this.FindControl<ComboBox>("FirstLineModeCombo")!;
            var firstLineValue = this.FindControl<NumericUpDown>("FirstLineValueBox")!;
            double fl = p.FirstLineIndent ?? 0;
            if (fl > 0)
            {
                firstLineMode.SelectedIndex = 1; // Отступ
                firstLineValue.Value = (decimal)PtToCm(fl);
            }
            else if (fl < 0)
            {
                firstLineMode.SelectedIndex = 2; // Выступ
                firstLineValue.Value = (decimal)PtToCm(-fl);
            }
            else
            {
                firstLineMode.SelectedIndex = 0; // нет
                firstLineValue.Value = 0m;
            }

            this.FindControl<NumericUpDown>("SpaceBeforeBox")!.Value = (decimal)(p.SpaceBefore ?? 0);
            this.FindControl<NumericUpDown>("SpaceAfterBox")!.Value = (decimal)(p.SpaceAfter ?? 0);

            var lineSpacing = this.FindControl<ComboBox>("LineSpacingCombo")!;
            var lineSpacingValue = this.FindControl<NumericUpDown>("LineSpacingValueBox")!;
            var rule = p.LineSpacingRule ?? LineSpacingRule.Auto;
            double val = p.LineSpacingValue ?? 1.0;
            if (rule == LineSpacingRule.Exact)
            {
                lineSpacing.SelectedIndex = 4; // Точно
                lineSpacingValue.Value = (decimal)val;
            }
            else if (rule == LineSpacingRule.AtLeast)
            {
                lineSpacing.SelectedIndex = 5; // Минимум
                lineSpacingValue.Value = (decimal)val;
            }
            else
            {
                if (Math.Abs(val - 1.0) < 0.001) lineSpacing.SelectedIndex = 0;
                else if (Math.Abs(val - 1.5) < 0.001) lineSpacing.SelectedIndex = 1;
                else if (Math.Abs(val - 2.0) < 0.001) lineSpacing.SelectedIndex = 2;
                else lineSpacing.SelectedIndex = 3; // Множитель
                lineSpacingValue.Value = (decimal)val;
            }
        }

        // ── Сбор значений в модель ────────────────────────────────────────
        private ParagraphProperties BuildResult()
        {
            var p = new ParagraphProperties
            {
                Alignment = (StyleAlignment)Math.Max(0, this.FindControl<ComboBox>("AlignmentCombo")!.SelectedIndex),
                OutlineLevel = Math.Max(0, this.FindControl<ComboBox>("LevelCombo")!.SelectedIndex)
            };

            p.LeftIndent = CmToPt(this.FindControl<NumericUpDown>("LeftIndentBox")!.Value);
            p.RightIndent = CmToPt(this.FindControl<NumericUpDown>("RightIndentBox")!.Value);

            double flCm = (double)(this.FindControl<NumericUpDown>("FirstLineValueBox")!.Value ?? 0m);
            double flPt = flCm * PtPerCm;
            p.FirstLineIndent = this.FindControl<ComboBox>("FirstLineModeCombo")!.SelectedIndex switch
            {
                1 => flPt,   // Отступ
                2 => -flPt,  // Выступ
                _ => 0       // нет
            };

            p.SpaceBefore = (double)(this.FindControl<NumericUpDown>("SpaceBeforeBox")!.Value ?? 0m);
            p.SpaceAfter = (double)(this.FindControl<NumericUpDown>("SpaceAfterBox")!.Value ?? 0m);

            double lsVal = (double)(this.FindControl<NumericUpDown>("LineSpacingValueBox")!.Value ?? 0m);
            switch (this.FindControl<ComboBox>("LineSpacingCombo")!.SelectedIndex)
            {
                case 0: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = 1.0; break;
                case 1: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = 1.5; break;
                case 2: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = 2.0; break;
                case 3: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = lsVal > 0 ? lsVal : 1.0; break;
                case 4: p.LineSpacingRule = LineSpacingRule.Exact; p.LineSpacingValue = lsVal; break;
                case 5: p.LineSpacingRule = LineSpacingRule.AtLeast; p.LineSpacingValue = lsVal; break;
                default: p.LineSpacingRule = LineSpacingRule.Auto; p.LineSpacingValue = 1.0; break;
            }

            return p;
        }

        // Значение первой строки активно только для "Отступ"/"Выступ"; значение междустрочного —
        // только для "Множитель"/"Точно"/"Минимум".
        private void UpdateEnabledStates()
        {
            int flMode = this.FindControl<ComboBox>("FirstLineModeCombo")!.SelectedIndex;
            this.FindControl<NumericUpDown>("FirstLineValueBox")!.IsEnabled = flMode == 1 || flMode == 2;

            int lsMode = this.FindControl<ComboBox>("LineSpacingCombo")!.SelectedIndex;
            this.FindControl<NumericUpDown>("LineSpacingValueBox")!.IsEnabled = lsMode >= 3;
        }

        private void OnOk(object? sender, RoutedEventArgs e) => Close(BuildResult());

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private static double PtToCm(double pt) => Math.Round(pt / PtPerCm, 2);
        private static double CmToPt(decimal? cm) => (double)(cm ?? 0m) * PtPerCm;
    }
}