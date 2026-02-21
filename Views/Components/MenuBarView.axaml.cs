using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using Writersword.ViewModels.Components;

namespace Writersword.Views.Components
{
    public partial class MenuBarView : UserControl
    {
        private readonly ILogger<MenuBarView> _logger;

        public MenuBarView()
        {
            _logger = App.Services.GetService<ILogger<MenuBarView>>()!;

            InitializeComponent();

            DataContextChanged += OnDataContextChanged;

            _logger.LogDebug("MenuBarView created");
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is MenuBarViewModel vm)
            {
                vm.OpenRecentProjectCommand.Subscribe(_ =>
                {
                    Dispatcher.UIThread.Post(() => MainMenu.Close());
                });
            }
        }
    }
}