using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Writersword.Views.Components
{
    /// <summary>
    /// Code-behind для TabBarView
    /// Панель вкладок документов
    /// </summary>
    public partial class TabBarView : UserControl
    {
        private readonly ILogger<TabBarView> _logger;

        public TabBarView()
        {
            _logger = App.Services.GetService<ILogger<TabBarView>>()!;

            InitializeComponent();

            _logger.LogDebug("TabBarView created");
        }
    }
}