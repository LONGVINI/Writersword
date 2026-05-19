using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.Synonyms.ViewModels;
using Writersword.Core.Services;
using Writersword.Modules.Synonyms.Resources;

namespace Writersword.Modules.Synonyms
{
    public class SynonymsModule : BaseModule
    {
        private readonly ILogger<SynonymsModule> _logger;
        private SynonymsViewModel? _viewModel;

        public SynonymsModule() : base()
        {
            _logger = CoreServices.GetService<ILogger<SynonymsModule>>()!;
        }

        public override string moduleType => "Synonyms";
        public override string Title { get; set; } = "Synonyms";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata => new SynonymsMetadata();

        public override void Initialize()
        {
            _viewModel = new SynonymsViewModel();
            _logger.LogDebug("Initialized (moduleType: {moduleType})", moduleType);
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            _logger.LogDebug("Context changed - no action needed (helper module)");
        }

        public override object? GetCustomData() => null;
        public override object? GetSessionData() => null;

        public override Control? CreateView()
        {
            return new Views.SynonymsView { DataContext = ViewModel };
        }
    }

    internal class SynonymsMetadata : IModuleMetadata
    {
        public string ModuleType => "Synonyms";
        public string DisplayName => SynonymsStrings.DisplayName;
        public string Description => SynonymsStrings.Description;
    }
}