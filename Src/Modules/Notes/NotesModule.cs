using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models;
using Writersword.Core.Services;
using Writersword.Modules.Common;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.Resources;
using Writersword.Modules.Notes.ViewModels;

namespace Writersword.Modules.Notes
{
    public sealed class NotesModule : BaseModule, IStateSnapshotModule
    {
        private readonly ILogger<NotesModule> _logger;
        private NotesViewModel? _viewModel;

        public NotesModule()
        {
            _logger = CoreServices.GetService<ILogger<NotesModule>>()!;
        }

        public override string moduleType => "Notes";
        public override string Title { get; set; } = "Notes";
        public override object? ViewModel => _viewModel;
        public override IModuleMetadata Metadata { get; } = new NotesMetadata();

        public override void Initialize()
        {
            base.Initialize();
            _viewModel = new NotesViewModel();
            _viewModel.IsReadOnly = Context?.IsInCompareMode == true;
            _logger.LogDebug("Notes module initialized");
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            if (_viewModel != null)
                _viewModel.IsReadOnly = context?.IsInCompareMode == true;
        }

        public override object? GetCustomData()
        {
            var snapshot = TakeStateSnapshot();
            return snapshot == null ? null : SerializeStateSnapshot(snapshot);
        }

        public object? TakeStateSnapshot()
        {
            if (_viewModel == null)
                return null;

            NotesData Snapshot() => _viewModel.CreateSnapshot();
            var data = Dispatcher.UIThread.CheckAccess()
                ? Snapshot()
                : Dispatcher.UIThread.InvokeAsync(Snapshot).GetAwaiter().GetResult();

            // Одна исходная пустая страница не несёт пользовательских данных.
            return HasMeaningfulData(data) ? data : null;
        }

        public object? SerializeStateSnapshot(object snapshot) => snapshot as NotesData;

        public override object? GetSessionData()
        {
            if (_viewModel == null)
                return null;

            NotesSessionData Snapshot() => _viewModel.CreateSessionSnapshot();
            return Dispatcher.UIThread.CheckAccess()
                ? Snapshot()
                : Dispatcher.UIThread.InvokeAsync(Snapshot).GetAwaiter().GetResult();
        }

        public override void SetCustomData(object? data)
        {
            if (_viewModel == null)
                return;

            try
            {
                _viewModel.LoadData(ConvertCustomData(data));
            }
            catch (Exception ex)
            {
                // При неизвестном или повреждённом формате текущая модель не
                // затирается: CustomData содержит критичные пользовательские данные.
                _logger.LogError(ex, "Failed to load Notes custom data");
            }
        }

        public override void SetSessionData(object? data)
        {
            if (_viewModel == null || data == null)
                return;

            try
            {
                var session = data switch
                {
                    NotesSessionData typed => typed,
                    JToken token => token.ToObject<NotesSessionData>(),
                    _ => JToken.FromObject(data).ToObject<NotesSessionData>()
                };
                _viewModel.RestoreSession(session);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore Notes session data");
            }
        }

        public override Control? CreateView() => new Views.NotesView { DataContext = _viewModel };

        public override void Dispose()
        {
            _viewModel = null;
            base.Dispose();
            _logger.LogDebug("Notes module disposed");
        }

        private static NotesData ConvertCustomData(object? data)
        {
            if (data == null)
                return new NotesData();

            // До блочного редактора Notes хранил весь текст одной строкой.
            // Миграция сохраняет порядок абзацев и не меняет исходный текст строк.
            if (data is string legacyText)
                return CreateLegacyData(legacyText);
            if (data is JValue value && value.Type == JTokenType.String)
                return CreateLegacyData(value.Value<string>() ?? string.Empty);

            var result = data switch
            {
                NotesData typed => typed,
                JToken token => token.ToObject<NotesData>(),
                _ => JToken.FromObject(data).ToObject<NotesData>()
            };
            return result ?? throw new InvalidOperationException("Notes data is empty after deserialization");
        }

        private static NotesData CreateLegacyData(string text)
        {
            var blocks = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => new NoteBlock { Text = line })
                .ToList();
            if (blocks.Count == 0)
                blocks.Add(new NoteBlock());

            return new NotesData
            {
                Pages = new List<NotePage>
                {
                    new() { Title = "Заметки", Blocks = blocks }
                }
            };
        }

        private static bool HasMeaningfulData(NotesData data)
        {
            if (data.Pages.Count != 1)
                return data.Pages.Count > 0;
            var page = data.Pages[0];
            return page.Title != "Заметки" || page.Blocks.Any(block =>
                !string.IsNullOrEmpty(block.Text) ||
                block.Type != NoteBlockType.Paragraph ||
                block.IsChecked || block.IsHighlighted || block.IsStruckThrough);
        }
    }

    internal sealed class NotesMetadata : IModuleMetadata
    {
        public string ModuleType => "Notes";
        public string DisplayName => NotesStrings.DisplayName;
        public string Description => NotesStrings.Description;
    }
}
