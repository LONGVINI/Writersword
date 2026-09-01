using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Writersword.Core.Services.Storage;
using Writersword.Mobile.Services;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Reading;

namespace Writersword.Mobile.Views
{
    /// <summary>
    /// Чтение книги на телефоне.
    ///
    /// Правки здесь нет намеренно. Ввод в редакторе написан под мышь и
    /// физическую клавиатуру, и переносить его на палец — отдельная работа;
    /// а прочитать написанное с телефона хочется уже сейчас.
    ///
    /// Показывает то же, что и настольная программа: тот же DocumentCanvas, та
    /// же раскладка, та же отрисовка через Skia. Управление тоже то же — худ
    /// висит на ReadingRibbonViewModel, на которой висит настольная лента.
    ///
    /// Книжного разворота нет: две страницы на телефонный экран дают кегль, при
    /// котором строка не разбирается.
    /// </summary>
    public partial class ReaderView : UserControl
    {
        /// <summary>Где в проекте лежат данные редактора.</summary>
        private const string EditorDataEntry = "modules/TextEditor/CustomData.json";

        private readonly DeltaHashService _hashService = new();
        private readonly ChunkManager _chunkManager;
        private readonly DocumentSerializer _serializer;
        private readonly AutoReplaceService _autoReplace = new();
        private readonly SpellCheckService _spellCheck = new();

        private DocumentViewModel? _document;
        private ReadingRibbonViewModel? _ribbon;

        public ReaderView()
        {
            InitializeComponent();

            _chunkManager = new ChunkManager(_hashService);
            _serializer = new DocumentSerializer(_hashService, _chunkManager);

            Hud.OpenRequested += OnOpenRequested;

            RefreshBooks();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // Список перечитывается при каждом возврате на вкладку: книгу могли
            // скачать на соседней, и уходить с экрана ради этого незачем.
            RefreshBooks();
        }

        // ── Список книг ───────────────────────────────────────────────────

        private void RefreshBooks()
        {
            var names = new List<string>();

            try
            {
                var dir = MobileSyncSession.ProjectsDirectory;

                if (Directory.Exists(dir))
                {
                    names.AddRange(Directory
                        .EnumerateFiles(dir, "*.writersword", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to list local projects");
            }

            Hud.SetBooks(names, _title.Length > 0 ? _title : null);
        }

        // ── Открытие ──────────────────────────────────────────────────────

        private void OnOpenRequested(string name)
        {
            var path = MobileSyncSession.LocalPathFor(name);

            if (!File.Exists(path))
            {
                ShowEmpty("Файла книги нет на телефоне: " + name);
                RefreshBooks();
                return;
            }

            try
            {
                var document = LoadDocument(path);

                if (document is null)
                {
                    ShowEmpty("В книге нет рукописи: " + name);
                    return;
                }

                Attach(document, name);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to open book {Path}", path);
                ShowEmpty("Не удалось открыть книгу: " + ex.Message);
            }
        }

        /// <summary>
        /// Достать рукопись из файла проекта.
        ///
        /// Данные редактора лежат в проекте одной записью, а внутри неё —
        /// конверт с полями: сама рукопись, локальные настройки и позиция
        /// каретки. Читаем только рукопись: настройки чтения на телефоне свои,
        /// а каретка нужна правке, которой здесь нет.
        ///
        /// Старый вид записи — рукопись без конверта — читается как есть:
        /// книги, сделанные до конверта, открываться обязаны.
        /// </summary>
        private DocumentModel? LoadDocument(string projectPath)
        {
            using var storage = new SqliteFileStorageService(projectPath, Serilog.Log.Logger);

            var bytes = storage.ReadFile(EditorDataEntry);
            if (bytes is null)
                return null;

            var raw = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string documentJson = raw;

            try
            {
                using var parsed = JsonDocument.Parse(raw);

                if (parsed.RootElement.ValueKind == JsonValueKind.Object
                    && parsed.RootElement.TryGetProperty("doc", out var docProperty)
                    && docProperty.ValueKind == JsonValueKind.String)
                {
                    documentJson = docProperty.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // Не конверт — значит рукопись лежит без обёртки.
            }

            if (string.IsNullOrWhiteSpace(documentJson))
                return null;

            return _serializer.Deserialize(documentJson);
        }

        private void Attach(DocumentModel document, string title)
        {
            var vm = new DocumentViewModel(document, _chunkManager, _autoReplace, _spellCheck);

            vm.ViewMode = EditorViewMode.Reading;

            // Лента, а не лист. Лист вписывается в экран целиком, и бумажная
            // страница, ужатая до ширины телефона, даёт кегль, которого не
            // видно. Лента страниц не знает: текст течёт по ширине экрана своим
            // размером. Подача выставляется до первой раскладки — иначе книга
            // успела бы разложиться листами и тут же переразложиться.
            vm.Reading.Flow = ReadingFlow.Column;
            vm.Reading.Format = ReadingSheetFormat.Pocket;

            _document = vm;
            _title = title;

            PageCanvas.DataContext = vm;

            var host = new MobileReadingHost(PageCanvas, () => _document);
            _ribbon = new ReadingRibbonViewModel(host);

            Hud.Attach(_ribbon);

            EmptyBlock.IsVisible = false;
            Scroll.IsVisible = true;

            // Номер страницы лента узнаёт от канваса, а не сама: раскладка
            // складывается уже после того, как книга отдана на показ.
            Dispatcher.UIThread.Post(SyncPageState, DispatcherPriority.Background);
        }

        private void ShowEmpty(string message)
        {
            _document = null;
            _ribbon = null;

            Hud.Detach();
            PageCanvas.DataContext = null;

            EmptyBlock.Text = message;
            EmptyBlock.IsVisible = true;
            Scroll.IsVisible = false;
        }

        private void SyncPageState()
        {
            if (_ribbon is null)
                return;

            _ribbon.SetPageState(PageCanvas.SpreadPageNumber, PageCanvas.SpreadPageCount);
            Hud.Refresh();
        }

        private string _title = string.Empty;
    }
}
