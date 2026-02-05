using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Writersword.Views.Components
{
    /// <summary>
    /// Code-behind для NotificationView
    /// Всплывающее уведомление в правом нижнем углу экрана
    /// </summary>
    public partial class NotificationView : UserControl
    {
        private readonly ILogger<NotificationView> _logger;

        public NotificationView()
        {
            _logger = App.Services.GetService<ILogger<NotificationView>>()!;

            InitializeComponent();

            _logger.LogDebug("NotificationView created");
        }
    }
}