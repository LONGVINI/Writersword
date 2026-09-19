using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// <summary>
    /// Модуль заметок: страницы со строками разного вида.
    /// Отвечает за жизненный цикл, формат хранения и переход между потоками;
    /// правила работы с содержимым живут в <see cref="NotesViewModel"/>.
    /// </summary>
    public sealed class NotesModule : BaseModule, IStateSnapshotModule
    {
        private readonly ILogger<NotesModule> _logger;
        private NotesViewModel? _viewModel;

        public NotesModule()
        {
            // Модуль создаётся и до подъёма контейнера — фабрика делает
            // временный экземпляр только ради метаданных. Обращение к
            // отсутствующему журналу не должно ронять запуск программы.
            _logger = CoreServices.GetService<ILogger<NotesModule>>()
                ?? NullLogger<NotesModule>.Instance;
        }

        public override string moduleType => "Notes";

        public override string Title { get; set; } = "Notes";

        public override object? ViewModel => _viewModel;

        public override IModuleMetadata Metadata { get; } = new NotesMetadata();

        public override void Initialize()
        {
            base.Initialize();
            _viewModel = new NotesViewModel { IsReadOnly = Context?.IsInCompareMode == true };
            _logger.LogDebug("Notes module initialized");
        }

        protected override void OnContextChanged(DocumentContext? context)
        {
            if (_viewModel != null)
                _viewModel.IsReadOnly = context?.IsInCompareMode == true;
        }

        public override Control? CreateView() => new Views.NotesView { DataContext = _viewModel };

        public override object? GetCustomData()
        {
            var snapshot = TakeStateSnapshot();
            return snapshot == null ? null : SerializeStateSnapshot(snapshot);
        }

        public object? TakeStateSnapshot()
        {
            var viewModel = _viewModel;
            if (viewModel == null)
                return null;

            NotesData? Snapshot()
            {
                // Нетронутый модуль не кладёт в файл проекта пустую страницу:
                // иначе каждое открытие проекта делало бы новую версию на
                // пустом месте.
                return viewModel.IsPristine ? null : viewModel.CreateSnapshot();
            }

            return Dispatcher.UIThread.CheckAccess()
                ? Snapshot()
                : Dispatcher.UIThread.InvokeAsync(Snapshot).GetAwaiter().GetResult();
        }

        public object? SerializeStateSnapshot(object snapshot) => snapshot as NotesData;

        public override object? GetSessionData()
        {
            var viewModel = _viewModel;
            if (viewModel == null)
                return null;

            NotesSessionData Snapshot() => viewModel.CreateSessionSnapshot();
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
                var converted = ConvertCustomData(data);

                // Коллекции и выбранная страница связаны с представлением:
                // загрузка из фонового потока применяется в потоке интерфейса.
                void Load()
                {
                    if (ReferenceEquals(_viewModel, viewModel))
                        viewModel.LoadData(converted);
                }

                if (Dispatcher.UIThread.CheckAccess())
                    Load();
                else
                    Dispatcher.UIThread.InvokeAsync(Load).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // При неизвестном или повреждённом формате текущая модель не
                // затирается: в ней лежит то, что человек уже написал.
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

        public override void Dispose()
        {
            _viewModel = null;
            base.Dispose();
            _logger.LogDebug("Notes module disposed");
        }

        /// <summary>
        /// Привести сохранённые данные к текущей модели.
        /// Разбирается только своя версия формата: разобрать чужую как свою и
        /// затем перезаписать значило бы стереть то, что записала более новая
        /// сборка.
        /// </summary>
        private static NotesData ConvertCustomData(object? data)
        {
            if (data == null)
                return new NotesData();

            // Строка приходит в двух случаях. Первый — старый формат, где все
            // заметки лежали одним куском текста. Второй — свой же документ,
            // который путь сохранения отдал строкой JSON, а не объектом.
            // Отличать обязательно: иначе документ раскладывается по строкам
            // заметки и человек видит собственный JSON вместо своих заметок.
            if (data is string text)
                return Recover(FromText(text));
            if (data is JValue value && value.Type == JTokenType.String)
                return Recover(FromText(value.Value<string>() ?? string.Empty));

            if (data is NotesData typed)
            {
                if (typed.FormatVersion != NotesData.CurrentFormatVersion)
                    throw new InvalidOperationException("Unsupported Notes format version");
                return typed;
            }

            var token = data as JToken ?? JToken.FromObject(data);

            // Разбор незнакомого объекта иначе даёт пустую NotesData со
            // значениями по умолчанию и подменяет ею сохранённые заметки.
            if (token is not JObject document ||
                document.GetValue(nameof(NotesData.Pages), StringComparison.OrdinalIgnoreCase) is not JArray)
            {
                throw new InvalidOperationException("Notes data must contain a Pages array");
            }

            // Отсутствие версии означает исходный блочный формат — он и есть
            // текущий. Явно указанную чужую версию читать нельзя.
            var version = document.GetValue(nameof(NotesData.FormatVersion), StringComparison.OrdinalIgnoreCase);
            if (version != null &&
                (version.Type != JTokenType.Integer || version.Value<int>() != NotesData.CurrentFormatVersion))
            {
                throw new InvalidOperationException("Unsupported Notes format version");
            }

            var result = document.ToObject<NotesData>()
                ?? throw new InvalidOperationException("Notes data is empty after deserialization");
            if (result.FormatVersion != NotesData.CurrentFormatVersion)
                throw new InvalidOperationException("Unsupported Notes format version");
            return Recover(result);
        }

        /// <summary>
        /// Разобрать строку: сначала как документ заметок, и только если это
        /// не он — как старый однострочный формат.
        /// </summary>
        private static NotesData FromText(string text) =>
            TryParseDocument(text, out var document) ? document : CreateLegacyData(text);

        /// <summary>
        /// Прочитать строку как документ заметок.
        /// Возвращает false для всего, что документом не является: чужой
        /// версии формата, обычного текста, обрывка JSON.
        /// </summary>
        private static bool TryParseDocument(string text, out NotesData result)
        {
            result = new NotesData();

            var trimmed = text.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return false;

            try
            {
                if (JToken.Parse(text) is not JObject document ||
                    document.GetValue(nameof(NotesData.Pages), StringComparison.OrdinalIgnoreCase) is not JArray)
                    return false;

                var parsed = document.ToObject<NotesData>();
                if (parsed == null || parsed.FormatVersion != NotesData.CurrentFormatVersion || parsed.Pages == null)
                    return false;

                result = parsed;
                return true;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Вернуть к жизни заметки, разложенные по строкам собственным JSON.
        ///
        /// Так выглядит уже сохранённый след прежней ошибки: одна страница, в
        /// строках которой лежит документ целиком. Строки склеиваются обратно
        /// и разбираются как документ. Настоящие заметки под это не подходят:
        /// склейка обязана дать разбираемый JSON со своей версией формата.
        /// </summary>
        private static NotesData Recover(NotesData data)
        {
            if (data.Pages.Count != 1)
                return data;

            var page = data.Pages[0];
            if (page.Blocks.Count < 3)
                return data;

            var text = string.Join("\n", page.Blocks.Select(block => block.Text));
            return TryParseDocument(text, out var restored) ? restored : data;
        }

        /// <summary>
        /// Перевести старый однострочный формат в страницы со строками.
        /// Разметка к строкам не применяется: перенос обязан сохранить текст
        /// ровно таким, каким его записали, символ в символ.
        /// </summary>
        private static NotesData CreateLegacyData(string text)
        {
            var blocks = new List<NoteBlock>();
            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                blocks.Add(new NoteBlock { Text = line });
            if (blocks.Count == 0)
                blocks.Add(new NoteBlock());

            var now = DateTime.UtcNow;
            return new NotesData
            {
                Pages = new List<NotePage>
                {
                    new()
                    {
                        Title = NotesStrings.Page_DefaultTitle,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        Blocks = blocks
                    }
                }
            };
        }
    }

    /// <summary>Метаданные модуля для списка доступных модулей.</summary>
    internal sealed class NotesMetadata : IModuleMetadata
    {
        public string ModuleType => "Notes";
        public string DisplayName => NotesStrings.DisplayName;
        public string Description => NotesStrings.Description;
    }
}
