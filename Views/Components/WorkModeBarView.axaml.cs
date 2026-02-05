using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Writersword.Views.Components
{
    /// <summary>
    /// Code-behind для WorkModeBarView
    /// Вертикальная панель переключения режимов работы
    /// </summary>
    public partial class WorkModeBarView : UserControl
    {
        private readonly ILogger<WorkModeBarView> _logger;

        public WorkModeBarView()
        {
            _logger = App.Services.GetService<ILogger<WorkModeBarView>>()!;

            InitializeComponent();

            _logger.LogDebug("WorkModeBarView created");
        }
    }
}