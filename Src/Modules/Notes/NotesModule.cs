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
        private const int SupportedFormatVersion = 1;
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
            var viewModel = _viewModel;
            if (viewModel == null)
                return;

            try
            {
                var convertedData = ConvertCustomData(data);

                // Коллекции и выбранная страница связаны с представлением;
                // загрузка из фонового потока применяется в потоке интерфейса.
                void Load()
                {
                    if (ReferenceEquals(_viewModel, viewModel))
                        viewModel.LoadData(convertedData);
                }

                if (Dispatcher.UIThread.CheckAccess())
                    Load();
                else
                    Dispatcher.UIThread.InvokeAsync(Load).GetAwaiter().GetResult();
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
            var viewModel = _viewModel;
            if (viewModel == null || data == null)
                return;

            try
            {
                var session = data switch
                {
                    NotesSessionData typed => typed,
                    JToken token => token.ToObject<NotesSessionData>(),
                    _ => JToken.FromObject(data).ToObject<NotesSessionData>()
                };
                void Restore()
                {
                    if (ReferenceEquals(_viewModel, viewModel))
                        viewModel.RestoreSession(session);
                }

                if (Dispatcher.UIThread.CheckAccess())
                    Restore();
                else
                    Dispatcher.UIThread.InvokeAsync(Restore).GetAwaiter().GetResult();
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

            if (data is NotesData typed)
            {
                if (typed.FormatVersion != SupportedFormatVersion)
                    throw new InvalidOperationException("Unsupported Notes format version");
                return typed;
            }

            var token = data as JToken ?? JToken.FromObject(data);

            // Десериализация неизвестного объекта иначе создаёт пустую NotesData
            // со значениями по умолчанию и подменяет сохранённые заметки.
            if (token is not JObject document ||
                document.GetValue(nameof(NotesData.Pages), StringComparison.OrdinalIgnoreCase) is not JArray)
            {
                throw new InvalidOperationException("Notes data must contain a Pages array");
            }

            // Отсутствующая версия соответствует исходному блочному формату.
            // Явную чужую версию нельзя читать как текущую и затем перезаписывать.
            var version = document.GetValue(nameof(NotesData.FormatVersion), StringComparison.OrdinalIgnoreCase);
            if (version != null &&
                (version.Type != JTokenType.Integer || version.Value<int>() != SupportedFormatVersion))
            {
                throw new InvalidOperationException("Unsupported Notes format version");
            }

            var result = document.ToObject<NotesData>()
                ?? throw new InvalidOperationException("Notes data is empty after deserialization");
            if (result.FormatVersion != SupportedFormatVersion)
                throw new InvalidOperationException("Unsupported Notes format version");
            return result;
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
