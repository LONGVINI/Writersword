using Avalonia.Controls;
using Writersword.Mobile.Services;

namespace Writersword.Mobile.Views
{
    public partial class MainView : UserControl
    {
        public MainView()
        {
            InitializeComponent();

            // Автоматика поднимается здесь, а не в читалке: книги обновляются
            // независимо от того, открыта ли сейчас какая-нибудь из них, и
            // привязывать это к экрану значило бы обновлять их только когда на них
            // смотрят.
            MobileAutoSync.Instance.Start();
        }
    }
}
