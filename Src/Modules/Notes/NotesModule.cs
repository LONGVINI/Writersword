using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using System;
using System.Reactive.Linq;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Modules.Common;
using Writersword.Modules.Notes.ViewModels;
using Writersword.Core.Services;
using Writersword.Modules.Notes.Resources;

namespace Writersword.Modules.Notes
{
    public class NotesModule : BaseModule
    {
        private readonly ILogger<NotesModule> _logger;
        private NotesViewModel? _viewModel;
        private IDisposable? _notesSubscription;

        public NotesModule() : base()
        {
            _logger = CoreServices.GetService<ILogger<NotesModule>>()!;
        }

        public override string moduleType => "Notes";
        public override string Title { get; set; } = "Notes";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata => new NotesMetadata();

        public override void Initialize()
        {
            _logger.LogDebug("Initialize START (moduleType: {moduleType})", moduleType);
            _viewModel = new NotesViewModel();
            CreateSubscription();
            _logger.LogDebug("Initialized (moduleType: {moduleType})", moduleType);
        }

        private void CreateSubscription()
        {
            _notesSubscription?.Dispose();
            _notesSubscription = _viewModel.WhenAnyValue(x => x.NoteText)
                .Throttle(TimeSpan.FromSeconds(0.5))
                .Subscribe(text =>
                {
                    _logger.LogDebug("Notes updated: {Length} chars", text?.Length ?? 0);
                });
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            _logger.LogDebug("Context changed - notes remain editable");
        }

        public override object? GetCustomData()
        {
            var text = _viewModel?.NoteText ?? "";
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        public override object? GetSessionData()
        {
            return new { scrollPosition = 0 };
        }

        public override void SetCustomData(object? data)
        {
            if (_viewModel == null)
            {
                _logger.LogWarning("SetCustomData called but ViewModel is null");
                return;
            }

            _notesSubscription?.Dispose();

            string text = data switch
            {
                string str => str,
                JValue jValue => jValue.Value?.ToString() ?? "",
                not null => data.ToString() ?? "",
                _ => ""
            };

            _viewModel.LoadNotes(text);
            _logger.LogDebug("Loaded {Length} chars", text.Length);

            CreateSubscription();
        }

        public override void SetSessionData(object? data)
        {
            _logger.LogDebug("SessionData set");
        }

        public override void Dispose()
        {
            _notesSubscription?.Dispose();
            _notesSubscription = null;
            _viewModel = null;
            base.Dispose();
            _logger.LogDebug("Disposed (moduleType: {moduleType})", moduleType);
        }

        public override Control? CreateView()
        {
            return new Views.NotesView { DataContext = ViewModel };
        }
    }

    internal class NotesMetadata : IModuleMetadata
    {
        public string ModuleType => "Notes";
        public string DisplayName => NotesStrings.DisplayName;
        public string Description => NotesStrings.Description;
    }
}