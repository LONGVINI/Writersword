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
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Resources.Localization;
using Writersword.Src.Core.Services;
using Writersword.Src.Modules.TextEditor.Resources;
using Writersword.ViewModels;

namespace Writersword.Modules.TextEditor
{
    public class TextEditorModule : BaseModule
    {
        private readonly ILogger<TextEditorModule> _logger;
        private TextEditorViewModel? _viewModel;
        private IDisposable? _textSubscription;

        public TextEditorModule() : base()
        {
            _logger = App.Services.GetService<ILogger<TextEditorModule>>()!;
        }

        public override string moduleType => "TextEditor";
        public override string Title { get; set; } = "Text Editor";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata => new TextEditorMetadata();

        public override void Initialize()
        {
            _logger.LogDebug("Initialize START (moduleType: {moduleType})", moduleType);
            _viewModel = new TextEditorViewModel();
            CreateSubscription();
            _logger.LogDebug("Initialized (moduleType: {moduleType})", moduleType);
        }

        private void CreateSubscription()
        {
            _textSubscription?.Dispose();
            _textSubscription = _viewModel.WhenAnyValue(x => x.PlainText)
                .Throttle(TimeSpan.FromSeconds(0.5))
                .Subscribe(text =>
                {
                    _logger.LogDebug("Text updated: {Length} chars", text?.Length ?? 0);
                });
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            if (context != null && _viewModel != null)
            {
                _viewModel.IsReadOnly = context.IsInCompareMode;
                _logger.LogDebug("Context changed - IsReadOnly: {IsReadOnly}", _viewModel.IsReadOnly);
            }
        }

        public override object? GetCustomData()
        {
            var text = _viewModel?.PlainText ?? "";
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        public override object? GetSessionData()
        {
            return new
            {
                lastEditTime = DateTime.Now,
                scrollPosition = 0
            };
        }

        public override void SetCustomData(object? data)
        {
            if (_viewModel == null)
            {
                _logger.LogWarning("SetCustomData called but ViewModel is null");
                return;
            }

            _textSubscription?.Dispose();

            string text = data switch
            {
                string str => str,
                JValue jValue => jValue.Value?.ToString() ?? "",
                not null => data.ToString() ?? "",
                _ => ""
            };

            _viewModel.LoadDocument(text);
            _logger.LogDebug("Loaded {Length} chars", text.Length);

            CreateSubscription();
        }

        public override void SetSessionData(object? data)
        {
            _logger.LogDebug("SessionData set");
        }

        public override void Dispose()
        {
            _textSubscription?.Dispose();
            _logger.LogDebug("Disposed (moduleType: {moduleType})", moduleType);
        }

        public override Control? CreateView()
        {
            return new Views.TextEditorView { DataContext = ViewModel };
        }
    }

    internal class TextEditorMetadata : IModuleMetadata
    {
        public string ModuleType => "TextEditor";
        public string DisplayName => TextEditorStrings.DisplayName;
        public string Description => TextEditorStrings.Description;
    }
}