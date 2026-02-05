using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Writersword.Views.Components
{
    /// <summary>
    /// Баннер восстановления версий проекта
    /// Показывается когда есть несохранённая версия
    /// </summary>
    public partial class RecoveryBanner : UserControl
    {
        private readonly ILogger<RecoveryBanner> _logger;

        public RecoveryBanner()
        {
            _logger = App.Services.GetService<ILogger<RecoveryBanner>>()!;

            InitializeComponent();

            _logger.LogDebug("RecoveryBanner created");
        }
    }
}