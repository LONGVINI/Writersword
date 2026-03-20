using System;
using Avalonia.Controls;
using ReactiveUI;
using Writersword.ViewModels.Print;

namespace Writersword.Views.Print
{
    public partial class PrintPreviewView : Window
    {
        public PrintPreviewView()
        {
            InitializeComponent();
            WindowState = WindowState.Normal;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (DataContext is PrintPreviewViewModel vm)
            {
                (vm.CloseCommand as IObservable<System.Reactive.Unit>)
                    ?.Subscribe(_ => Close());
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (DataContext is PrintPreviewViewModel vm)
                vm.FitToWidth(ClientSize.Width);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (DataContext is PrintPreviewViewModel vm)
                vm.Dispose();
        }
    }
}