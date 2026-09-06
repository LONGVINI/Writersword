using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Writersword.Core.Models.Sync;
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

        /// <summary>
        /// Открытая книга. Держится всё время, пока она на экране: картинки видов
        /// чтения читаются по требованию, во время отрисовки, а не разом при
        /// открытии — подложка может весить мегабайты, и поднимать её в память
        /// ради вида, который читатель, может, и не выберет, незачем.
        /// </summary>
        private SqliteFileStorageService? _storage;

        public ReaderView()
        {
            InitializeComponent();

            _chunkManager = new ChunkManager(_hashService);
            _serializer = new DocumentSerializer(_hashService, _chunkManager);

            Hud.OpenRequested += OnOpenRequested;
            Hud.EditModeChanged += OnEditModeChanged;

            // Жесты слушаются в фазе туннеля — раньше канваса. Канвас разбирает
            // указатель под мышь: нажал, повёл, отпустил — это выделение текста.
            // Пальцу нужно другое, и решить, что это было, можно только увидев
            // событие первым.
            Scroll.AddHandler(PointerPressedEvent, OnReaderPointerPressed, RoutingStrategies.Tunnel);
            Scroll.AddHandler(PointerReleasedEvent, OnReaderPointerReleased, RoutingStrategies.Tunnel);

            Scroll.AddHandler(Gestures.PinchEvent, OnPinch);
            Scroll.AddHandler(Gestures.PinchEndedEvent, OnPinchEnded);

            MobileAutoSync.Instance.BookUpdated += OnBookUpdated;
            MobileAutoSync.Instance.ForeignEditing += OnForeignEditing;
            MobileAutoSync.Instance.DesktopLockChanged += OnDesktopLockChanged;

            RefreshBooks();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            // Список перечитывается при каждом возврате на вкладку: книгу могли
            // скачать на соседней, и уходить с экрана ради этого незачем.
            RefreshBooks();

            if (_title.Length > 0)
                MobileAutoSync.Instance.WatchBook(_title);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            StopPositionWatch();

            // Опрос про старшинство платит батареей и нужен, только пока книга
            // на экране: с вкладки ушли — правки нет, и спрашивать не о чем.
            MobileAutoSync.Instance.StopWatching();

            // Хранилище не закрывается: вкладку покидают и возвращаются к ней, а
            // книга при этом остаётся открытой. Закроет его следующая книга или
            // сообщение о том, что открывать нечего.
            base.OnDetachedFromVisualTree(e);
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

        /// <summary>
        /// Правка: книга переводится из чтения в черновик и обратно. Черновик — это
        /// вся ширина экрана без листов и полей: на телефоне узкая колонка чтения при
        /// поднятой клавиатуре оставила бы под текст полосу в несколько строк.
        ///
        /// Написанное на телефоне пока никуда не уходит: обратной отправки книги на
        /// хранилище нет, синхронизация работает только на приём.
        /// </summary>
        private void OnEditModeChanged(bool on)
        {
            if (_document is null)
                return;

            // Старшинство компьютера проверяется до входа в правку, а не только
            // по приходу опроса: иначе человек успел бы набрать абзац и потерять
            // его через полминуты.
            if (on && MobileAutoSync.Instance.DesktopOwner is { } owner)
            {
                Hud.ClearEditMode();
                ShowDesktopLock(owner);
                return;
            }

            _document.ViewMode = on ? EditorViewMode.Draft : EditorViewMode.Reading;
            MobileAutoSync.Instance.Editing = on;

            if (on)
                PageCanvas.Focus();
        }

        /// <summary>
        /// Книгу занял или отпустил компьютер.
        ///
        /// Правило несимметричное: книга принадлежит компьютеру. Занял — телефон
        /// выходит из правки немедленно, даже посреди набора; отпустил — правка
        /// снова доступна, но сама не включается.
        /// </summary>
        private void OnDesktopLockChanged(DevicePresence? desktop)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (desktop is not null)
                {
                    Hud.SetEditAvailable(false);

                    if (_document is not null)
                    {
                        _document.ViewMode = EditorViewMode.Reading;
                        _document.IsReadOnly = true;
                    }

                    MobileAutoSync.Instance.Editing = false;
                    ShowDesktopLock(desktop);
                    return;
                }

                // Книга закрыта — включать нечего: кнопку держит в покое общий
                // выключатель худа.
                if (_document is null)
                    return;

                Hud.SetEditAvailable(true);
                _document.IsReadOnly = false;
                ForeignBanner.IsVisible = false;
            });
        }

        private void ShowDesktopLock(DevicePresence desktop)
        {
            ForeignBlock.Text =
                $"Книга занята: она открыта на компьютере «{desktop.DeviceName}». "
                + "Правка на телефоне выключена, читать можно.";

            ForeignBanner.IsVisible = true;
        }

        private void OnOpenRequested(string name)
        {
            // Прежняя книга закрывается по-человечески: где остановились —
            // записано, и вернувшись к ней, читатель попадёт туда же.
            StopPositionWatch();

            // Предупреждение относилось к прежней книге.
            ForeignBanner.IsVisible = false;

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
            CloseStorage();

            var storage = new SqliteFileStorageService(projectPath, Serilog.Log.Logger);
            _storage = storage;

            // Картинки видов чтения читаются отсюда же. Обычный их путь идёт через
            // вкладку с открытым проектом, а вкладок на телефоне нет.
            ReadingAssets.ProjectSource = storage.ReadFile;

            LoadProjectFonts(storage);

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

        /// <summary>
        /// Отдать складу шрифты, уложенные в книгу.
        ///
        /// Обычно склад берёт их сам, через активную вкладку — она знает, какой
        /// проект открыт. Вкладок на телефоне нет, и без этого вызова книга,
        /// набранная гарнитурой с машины автора, открывалась здесь чем попало:
        /// Skia молча подставляла похожий системный шрифт, и отличить подмену от
        /// авторского замысла было нельзя.
        ///
        /// Байты вычитываются сразу и целиком: хранилище закроется, как только
        /// рукопись прочитана, а гарнитуры нужны всё время, пока книга открыта.
        /// </summary>
        private static void LoadProjectFonts(SqliteFileStorageService storage)
        {
            var fonts = new List<(string Name, byte[] Data)>();

            try
            {
                foreach (var path in storage.GetFiles(ProjectFonts.ZipFolder))
                {
                    var data = storage.ReadFile(path);
                    if (data is not { Length: > 0 }) continue;

                    fonts.Add((Path.GetFileName(path), data));
                }
            }
            catch (Exception ex)
            {
                // Без уложенных шрифтов книга откроется системными — хуже, но
                // читаемо. Ронять из-за этого открытие нельзя.
                Serilog.Log.Warning(ex, "Failed to read project fonts");
            }

            ProjectFonts.LoadFrom(fonts);
        }

        /// <summary>
        /// Закрыть книгу. Источник картинок снимается первым: оставить его на
        /// закрытом хранилище значит уронить отрисовку на первом же обращении.
        /// </summary>
        private void CloseStorage()
        {
            ReadingAssets.ProjectSource = null;

            MobileAutoSync.Instance.StopWatching();
            MobileAutoSync.Instance.Editing = false;

            _storage?.Dispose();
            _storage = null;
        }

        private void Attach(DocumentModel document, string title)
        {
            var vm = new DocumentViewModel(document, _chunkManager, _autoReplace, _spellCheck);

            vm.ViewMode = EditorViewMode.Reading;

            // Запомненное накладывается до первой раскладки: смена подачи или
            // кегля после неё стоит полного прохода пагинации, а книга при этом
            // на мгновение показывается не такой, какой её оставили.
            //
            // По умолчанию — лента, а не лист. Лист вписывается в экран целиком,
            // и бумажная страница, ужатая до ширины телефона, даёт кегль,
            // которого не видно. Лента страниц не знает: текст течёт по ширине
            // экрана своим размером.
            ReaderState.Apply(vm.Reading);

            _document = vm;
            _title = title;

            PageCanvas.DataContext = vm;

            var host = new MobileReadingHost(PageCanvas, () => _document);
            _ribbon = new ReadingRibbonViewModel(host);

            Hud.Attach(_ribbon);

            // Книга открыта — следим, не занял ли её компьютер. Если он уже её
            // держит, правка выключена с самого начала, а не через полминуты.
            MobileAutoSync.Instance.Editing = false;
            MobileAutoSync.Instance.WatchBook(title);

            if (MobileAutoSync.Instance.DesktopOwner is { } owner)
            {
                Hud.SetEditAvailable(false);
                vm.IsReadOnly = true;
                ShowDesktopLock(owner);
            }

            EmptyBlock.IsVisible = false;
            Scroll.IsVisible = true;

            // Номер страницы лента узнаёт от канваса, а не сама: раскладка
            // складывается уже после того, как книга отдана на показ. Позиция
            // восстанавливается там же и по той же причине — до раскладки
            // возвращать не к чему.
            Dispatcher.UIThread.Post(() =>
            {
                SyncPageState();
                RestorePosition();
                StartPositionWatch();
            }, DispatcherPriority.Loaded);
        }

        // ── Свежесть книги ────────────────────────────────────────────────

        /// <summary>
        /// Книга обновилась с сервера.
        ///
        /// Открытая перечитывается на месте: читатель узнаёт об этом лишь тем,
        /// что текст стал новее, а место в книге сохраняется — оно записано долей
        /// прочитанного и от перечитывания не зависит.
        ///
        /// Событие приходит из фонового цикла, то есть не с потока интерфейса, —
        /// отсюда переход на него.
        /// </summary>
        private void OnBookUpdated(string book)
        {
            Dispatcher.UIThread.Post(() =>
            {
                RefreshBooks();

                if (!string.Equals(book, _title, StringComparison.Ordinal) || _document is null)
                    return;

                Serilog.Log.Information("Reopening {Book} after it was updated from storage", book);
                OnOpenRequested(book);
            });
        }

        /// <summary>
        /// Книгу правят на другом устройстве. Говорится, а не запрещается: телефон
        /// правок не вносит, столкнуться ему не с чем, — но текст под рукой вот-вот
        /// устареет, и знать об этом читателю стоит.
        /// </summary>
        private void OnForeignEditing(DevicePresence other)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ForeignBlock.Text =
                    $"Книгу правят на устройстве «{other.DeviceName}». Здесь показана та версия, "
                    + "что была на сервере в последнюю сверку.";

                ForeignBanner.IsVisible = true;
            });
        }

        // ── Жесты ─────────────────────────────────────────────────────────

        /// <summary>Путь пальца, за которым смахивание считается смахиванием.</summary>
        private const double SwipeThresholdPx = 60.0;

        /// <summary>
        /// Во сколько раз горизонталь должна перевесить вертикаль.
        ///
        /// Без этого условия любая попытка прокрутить ленту, дёрнувшая палец вбок,
        /// переворачивала бы страницу. Полтора — не строгость ради строгости:
        /// палец редко идёт по прямой, и требовать чистой горизонтали значит не
        /// узнавать половину настоящих смахиваний.
        /// </summary>
        private const double SwipeDominance = 1.5;

        private Point? _swipeStart;

        private void OnReaderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // В правке касание принадлежит тексту: оно ставит каретку и поднимает
            // клавиатуру. Смахивание страниц здесь отняло бы у канваса нажатие.
            _swipeStart = _document is null || _document.ViewMode != EditorViewMode.Reading
                ? null
                : e.GetPosition(this);
        }

        private void OnReaderPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            var start = _swipeStart;
            _swipeStart = null;

            if (start is not { } from || _document is null)
                return;

            // В ленте смахивать нечего: страниц нет, а вертикальную прокрутку
            // ведёт контейнер сам.
            if (_document.Reading.Flow == ReadingFlow.Column)
                return;

            var to = e.GetPosition(this);
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;

            if (Math.Abs(dx) < SwipeThresholdPx) return;
            if (Math.Abs(dx) < Math.Abs(dy) * SwipeDominance) return;

            // Палец влево — вперёд, как страница уходит из-под руки в бумажной
            // книге.
            PageCanvas.SpreadTurn(dx < 0 ? 1 : -1);
            SyncPageState();

            e.Handled = true;
        }

        private double? _zoomAtPinchStart;

        /// <summary>
        /// Щипок приближает книгу.
        ///
        /// Приближение, а не кегль: разбиение на страницы от него не зависит, и
        /// место в тексте не теряется. Кегль щипком менять нельзя — каждое
        /// движение пальцев перекраивало бы книгу заново.
        ///
        /// Мера жеста накопительная — она считается от начала щипка, — поэтому
        /// умножать её надо на приближение, каким оно было в тот момент, а не на
        /// нынешнее: иначе шаги множатся друг на друга и книга улетает.
        /// </summary>
        private void OnPinch(object? sender, PinchEventArgs e)
        {
            if (_document is null) return;
            if (_document.ViewMode != EditorViewMode.Reading) return;

            // В ленте приближать нечего: текст и так набран по ширине экрана.
            if (_document.Reading.Flow == ReadingFlow.Column) return;

            _zoomAtPinchStart ??= _document.Reading.Zoom;

            PageCanvas.SetBookZoom(_zoomAtPinchStart.Value * e.Scale);
            Hud.Refresh();

            e.Handled = true;
        }

        private void OnPinchEnded(object? sender, PinchEndedEventArgs e)
        {
            _zoomAtPinchStart = null;
        }

        // ── Позиция чтения ────────────────────────────────────────────────

        /// <summary>
        /// Доля прочитанного. У ленты берётся из прокрутки, у листа — из номера
        /// страницы: у них нет общей величины, кроме этой доли, а она переживает
        /// и смену подачи, и смену кегля.
        /// </summary>
        private double CurrentPosition()
        {
            if (_document is null)
                return 0.0;

            if (_document.Reading.Flow != ReadingFlow.Column)
            {
                int count = Math.Max(PageCanvas.SpreadPageCount - 1, 1);
                return Math.Clamp((PageCanvas.SpreadPageNumber - 1) / (double)count, 0.0, 1.0);
            }

            double range = Scroll.Extent.Height - Scroll.Viewport.Height;
            return range > 1.0 ? Math.Clamp(Scroll.Offset.Y / range, 0.0, 1.0) : 0.0;
        }

        private void RestorePosition()
        {
            if (_document is null)
                return;

            double position = ReaderState.PositionOf(_title);
            if (position <= 0.0)
                return;

            if (_document.Reading.Flow != ReadingFlow.Column)
            {
                int last = Math.Max(PageCanvas.SpreadPageCount - 1, 0);
                PageCanvas.SpreadGoToPage((int)Math.Round(position * last), animate: false);
                SyncPageState();
                return;
            }

            double range = Scroll.Extent.Height - Scroll.Viewport.Height;
            if (range > 1.0)
                Scroll.Offset = Scroll.Offset.WithY(position * range);
        }

        /// <summary>
        /// Позиция снимается по часам, а не по событию прокрутки.
        ///
        /// Событий у прокрутки десятки в секунду, и писать файл на каждое нельзя.
        /// Телефон же приложение не закрывает, а убивает: рассчитывать на то, что
        /// перед смертью дадут сохраниться, не приходится, и опрос раз в несколько
        /// секунд надёжнее любого обработчика ухода.
        /// </summary>
        private void StartPositionWatch()
        {
            _positionTimer ??= CreatePositionTimer();
            _positionTimer.Start();
        }

        private DispatcherTimer CreatePositionTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(3)
            };

            // Запись сама сравнивает с запомненным и молчит, если ничего не
            // изменилось: неподвижная книга файл не трогает.
            timer.Tick += (_, _) => ReaderState.SavePosition(_title, CurrentPosition());
            return timer;
        }

        private void StopPositionWatch()
        {
            _positionTimer?.Stop();

            if (_document is not null && _title.Length > 0)
                ReaderState.SavePosition(_title, CurrentPosition());
        }

        private DispatcherTimer? _positionTimer;

        private void ShowEmpty(string message)
        {
            StopPositionWatch();
            CloseStorage();

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
