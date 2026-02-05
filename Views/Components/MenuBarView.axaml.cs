using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace Writersword.Views.Components
{
    /// <summary>
    /// Code-behind для MenuBarView
    /// Главное меню приложения (File, Edit, View)
    /// </summary>
    public partial class MenuBarView : UserControl
    {
        private readonly ILogger<MenuBarView> _logger;

        public MenuBarView()
        {
            _logger = App.Services.GetService<ILogger<MenuBarView>>()!;

            InitializeComponent();

            _logger.LogDebug("MenuBarView created");
        }
    }
}