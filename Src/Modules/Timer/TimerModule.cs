using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.Timer.ViewModels;
using Writersword.Src.Core.Services;
using Writersword.Src.Modules.Timer.Resources;

namespace Writersword.Modules.Timer
{
    public class TimerModule : BaseModule
    {
        private readonly ILogger<TimerModule> _logger;
        private TimerViewModel? _viewModel;

        public TimerModule() : base()
        {
            _logger = App.Services.GetService<ILogger<TimerModule>>()!;
        }

        public override string moduleType => "Timer";
        public override string Title { get; set; } = "Timer";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata => new TimerMetadata();

        public override void Initialize()
        {
            _viewModel = new TimerViewModel();
            _logger.LogDebug("Initialized (moduleType: {moduleType})", moduleType);
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            _logger.LogDebug("Context changed - timer continues running");
        }

        public override object? GetCustomData() => null;
        public override object? GetSessionData() => null;

        public override Control? CreateView()
        {
            return new Views.TimerView { DataContext = ViewModel };
        }
    }

    internal class TimerMetadata : IModuleMetadata
    {
        public string ModuleType => "Timer";
        public string DisplayName => TimerStrings.DisplayName;
        public string Description => TimerStrings.Description;
    }
}