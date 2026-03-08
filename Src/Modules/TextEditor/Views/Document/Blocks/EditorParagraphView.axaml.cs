using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;
using Writersword.Modules.TextEditor.ViewModels.Blocks;

namespace Writersword.Modules.TextEditor.Views.Document
{
    public partial class EditorParagraphView : UserControl
    {
        public EditorParagraphView()
        {
            InitializeComponent();
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// ѕри смене DataContext подписываемс€ на событи€ фокуса от ViewModel.
        /// ¬ызываетс€ Avalonia когда ItemsControl присваивает DataContext новому View.
        /// </summary>
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (DataContext is ParagraphViewModel vm)
            {
                vm.FocusRequested += OnFocusRequested;
                vm.RequestFocusAtPosition = OnFocusAtPositionRequested;
            }
        }

        /// <summary>
        /// ѕереводит фокус на TextBox параграфа.
        /// ≈сли контрол ещЄ не смонтирован в визуальное дерево Ч ждЄм AttachedToVisualTree.
        /// </summary>
        private void OnFocusRequested()
        {
            var box = this.FindControl<TextBox>("ParagraphBox");
            if (box is null) return;

            if (box.IsAttachedToVisualTree())
            {
                box.Focus();
            }
            else
            {
                void OnAttached(object? s, Avalonia.VisualTreeAttachmentEventArgs args)
                {
                    box.AttachedToVisualTree -= OnAttached;
                    box.Focus();
                }
                box.AttachedToVisualTree += OnAttached;
            }
        }

        /// <summary>
        /// ѕереводит фокус и устанавливает каретку на указанную позицию.
        /// »спользуетс€ после мержа параграфов чтобы каретка встала в точку сли€ни€.
        /// </summary>
        private void OnFocusAtPositionRequested(int caretPosition)
        {
            var box = this.FindControl<TextBox>("ParagraphBox");
            if (box is null) return;

            void SetFocus()
            {
                box.Focus();
                box.CaretIndex = caretPosition;
            }

            if (box.IsAttachedToVisualTree())
            {
                SetFocus();
            }
            else
            {
                void OnAttached(object? s, Avalonia.VisualTreeAttachmentEventArgs args)
                {
                    box.AttachedToVisualTree -= OnAttached;
                    SetFocus();
                }
                box.AttachedToVisualTree += OnAttached;
            }
        }

        /// <summary>
        /// ѕерехватываем Enter и Backspace на уровне параграфа.
        /// Enter Ч разбивает текст по позиции каретки:
        ///   текст до каретки остаЄтс€ в текущем параграфе,
        ///   текст после каретки переноситс€ в новый параграф.
        /// Backspace в позиции 0 Ч сливает текст текущего параграфа с предыдущим.
        /// </summary>
        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not ParagraphViewModel vm) return;

            var box = this.FindControl<TextBox>("ParagraphBox");

            if (e.Key == Key.Enter)
            {
                e.Handled = true;

                int caretIndex = box?.CaretIndex ?? vm.PlainText.Length;
                string textBefore = vm.PlainText[..caretIndex];
                string textAfter = vm.PlainText[caretIndex..];

                vm.PlainText = textBefore;

                var newVm = vm.RequestAddAfter?.Invoke(vm);
                if (newVm is not null)
                    newVm.PlainText = textAfter;

                return;
            }

            if (e.Key == Key.Back)
            {
                int caretIndex = box?.CaretIndex ?? 0;

                // Backspace в начале параграфа Ч мЄрж с предыдущим
                if (caretIndex == 0)
                {
                    e.Handled = true;
                    vm.RequestMergeWithPrevious?.Invoke(vm, vm.PlainText);
                }
            }
        }
    }
}