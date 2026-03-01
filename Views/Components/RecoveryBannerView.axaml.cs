using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Writersword.Views.Components
{
    /// <summary>
    /// Баннер восстановления версий проекта
    /// Показывается когда есть несохранённая версия
    /// </summary>
    public partial class RecoveryBannerView : UserControl
    {
        private readonly ILogger<RecoveryBannerView> _logger;

        public RecoveryBannerView()
        {
            _logger = App.Services.GetService<ILogger<RecoveryBannerView>>()!;

            InitializeComponent();

            _logger.LogDebug("RecoveryBanner created");
        }
    }
}