using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Writersword.Views.Components
{
    /// <summary>
    /// Code-behind для ModulePanelView
    /// Боковая панель со списком доступных модулей
    /// </summary>
    public partial class ModulePanelView : UserControl
    {
        private readonly ILogger<ModulePanelView> _logger;

        public ModulePanelView()
        {
            _logger = App.Services.GetService<ILogger<ModulePanelView>>()!;

            InitializeComponent();

            _logger.LogDebug("ModulePanelView created");
        }
    }
}