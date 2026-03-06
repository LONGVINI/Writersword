using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System.Collections.ObjectModel;
using System.Reactive;

namespace Writersword.ViewModels.Components.MenuBar
{
    public partial class MenuBarViewModel
    {
        /// <summary>Список всех доступных WorkModes (для меню View)</summary>
        public ObservableCollection<MainWindowViewModel.WorkModeMenuItem> AllWorkModes
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.AllWorkModes ?? new ObservableCollection<MainWindowViewModel.WorkModeMenuItem>();
            }
        }

        /// <summary>Список всех доступных модулей (для меню View)</summary>
        public ObservableCollection<MainWindowViewModel.ModuleMenuItem> AllModules
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.AllModules ?? new ObservableCollection<MainWindowViewModel.ModuleMenuItem>();
            }
        }

        /// <summary>Команда переключения WorkMode (делегируется в MainWindowViewModel)</summary>
        public ReactiveCommand<string, Unit> ToggleWorkModeCommand
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.ToggleWorkModeCommand ?? ReactiveCommand.Create<string>(_ => { });
            }
        }

        /// <summary>Команда переключения модуля (делегируется в MainWindowViewModel)</summary>
        public ReactiveCommand<string, Unit> ToggleModuleCommand
        {
            get
            {
                var mainVM = _mainViewModelProvider?.Invoke();
                return mainVM?.ToggleModuleCommand ?? ReactiveCommand.Create<string>(_ => { });
            }
        }

        /// <summary>Переключить полноэкранный режим</summary>
        private void ToggleFullscreen()
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow != null)
            {
                if (desktop.MainWindow.WindowState == WindowState.FullScreen)
                {
                    desktop.MainWindow.WindowState = WindowState.Maximized;
                    IsFullscreen = false;
                }
                else
                {
                    desktop.MainWindow.WindowState = WindowState.FullScreen;
                    IsFullscreen = true;
                }

                _logger.LogDebug("Fullscreen toggled: {IsFullscreen}", IsFullscreen);
            }
        }
    }
}