using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.TextEditor.Models.Settings;

namespace Writersword.Modules.TextEditor.Views.Backdrops
{
    /// <summary>Что выбрали в окне фонов.</summary>
    public sealed class BackdropPickResult
    {
        public BackdropPickResult(string? reference) => Reference = reference;

        /// <summary>Адрес картинки. Пусто — фон убрать.</summary>
        public string? Reference { get; }
    }

    /// <summary>Строка папки в списке слева.</summary>
    public sealed class BackdropFolderRow : ReactiveObject
    {
        private bool _isSelected;

        public BackdropFolderRow(BackdropPack pack)
        {
            Pack = pack;
        }

        public BackdropPack Pack { get; }

        public string Name => Pack.Name;

        /// <summary>Где лежит папка и сколько в ней картинок.</summary>
        public string Subtitle
        {
            get
            {
                string where = Pack.Scope == BackdropScope.Local ? "в проекте" : "общая";
                int count = Pack.Items.Count;
                return count == 0 ? where : $"{where} · {count}";
            }
        }

        /// <summary>
        /// Значок области: замок у папки проекта — она уедет с рукописью, шар у
        /// общей — она одна на все проекты.
        ///
        /// Геометрией, а не строкой: привязка строки к Data держится на неявном
        /// преобразовании типов, и молчаливая осечка оставила бы список без
        /// значков вовсе.
        /// </summary>
        public Geometry ScopeIcon => Pack.Scope == BackdropScope.Local ? LocalIcon : GlobalIcon;

        private static readonly Geometry LocalIcon = Geometry.Parse(
            "M18 8h-1V6A5 5 0 007 6v2H6a2 2 0 00-2 2v10a2 2 0 002 2h12a2 2 0 002-2V10a2 2 0 00-2-2zM9 6a3 3 0 016 0v2H9V6zm3 12a2 2 0 110-4 2 2 0 010 4z");

        private static readonly Geometry GlobalIcon = Geometry.Parse(
            "M12 2a10 10 0 100 20 10 10 0 000-20zm7.9 9h-3.1a15.6 15.6 0 00-1.2-5.4A8 8 0 0119.9 11zM12 4.2c.8 1.2 1.5 3.3 1.7 6.8h-3.4c.2-3.5.9-5.6 1.7-6.8zM4.1 13h3.1c.1 2 .5 3.9 1.2 5.4A8 8 0 014.1 13zm3.1-2H4.1a8 8 0 014.3-5.4A15.6 15.6 0 007.2 11zm4.8 8.8c-.8-1.2-1.5-3.3-1.7-6.8h3.4c-.2 3.5-.9 5.6-1.7 6.8zm3.6-.4c.7-1.5 1.1-3.4 1.2-5.4h3.1a8 8 0 01-4.3 5.4z");

        public bool CanDelete => Pack.CanDelete;

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }
    }

    /// <summary>Плитка картинки.</summary>
    public sealed class BackdropTile : ReactiveObject, IDisposable
    {
        private bool _isSelected;

        public BackdropTile(BackdropItem item, Bitmap? preview)
        {
            Item = item;
            Preview = preview;
        }

        public BackdropItem Item { get; }

        public string FileName => Item.FileName;

        /// <summary>Уменьшенная картинка. Полный файл в сетку не грузится.</summary>
        public Bitmap? Preview { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public void Dispose() => Preview?.Dispose();
    }

    /// <summary>
    /// Окно выбора фона: папки слева, плитки справа.
    ///
    /// Оверлей модуля, а не отдельное окно — как и прочие окна редактора: своё
    /// окно уводит фокус, перекрывает рукопись целиком и на слабых машинах
    /// открывается заметной паузой.
    /// </summary>
    public partial class BackdropPickerOverlay : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<BackdropPickerOverlay>();

        // Ширина превью в плитке. Больше грузить незачем: в сетке картинка
        // показывается в полтораста точек, а обои бывают по восемь мегапикселей.
        private const int PreviewWidthPx = 320;

        private readonly ObservableCollection<BackdropFolderRow> _folders = new();
        private readonly ObservableCollection<BackdropTile> _tiles = new();

        private TaskCompletionSource<BackdropPickResult?>? _tcs;
        private BackdropFolderRow? _currentFolder;
        private BackdropTile? _currentTile;

        public BackdropPickerOverlay()
        {
            InitializeComponent();

            var folderList = this.FindControl<ItemsControl>("FolderList");
            if (folderList is not null) folderList.ItemsSource = _folders;

            var tileList = this.FindControl<ItemsControl>("TileList");
            if (tileList is not null) tileList.ItemsSource = _tiles;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>
        /// Показывает окно и ждёт выбора. Возвращает адрес выбранной картинки,
        /// пустой адрес — просьбу убрать фон, null — отказ.
        /// </summary>
        public Task<BackdropPickResult?> ShowAsync(string? currentReference)
        {
            _tcs?.TrySetResult(null);
            _tcs = new TaskCompletionSource<BackdropPickResult?>();

            ReloadFolders(currentReference);

            IsVisible = true;
            Focus();
            return _tcs.Task;
        }

        // ── Папки ─────────────────────────────────────────────────────────

        private void ReloadFolders(string? selectReference = null)
        {
            var packs = BackdropLibrary.Packs();

            _folders.Clear();
            foreach (var pack in packs) _folders.Add(new BackdropFolderRow(pack));

            // Открывается та папка, где лежит нынешний фон: человек пришёл сюда,
            // скорее всего, чтобы заменить его на соседнюю картинку.
            var target = BackdropLibrary.PackOf(selectReference, packs);
            var row = target is null
                ? _folders.FirstOrDefault()
                : _folders.FirstOrDefault(f => ReferenceEquals(f.Pack, target));

            SelectFolder(row, selectReference);
        }

        private void SelectFolder(BackdropFolderRow? row, string? selectReference = null)
        {
            foreach (var f in _folders) f.IsSelected = ReferenceEquals(f, row);
            _currentFolder = row;

            ReloadTiles(selectReference);
        }

        private void OnFolderPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border { Tag: BackdropFolderRow row }) SelectFolder(row);
        }

        private void OnCreateFolder(object? sender, RoutedEventArgs e)
        {
            var nameBox = this.FindControl<TextBox>("NewFolderName");
            var scopeBox = this.FindControl<ComboBox>("NewFolderScope");

            var scope = scopeBox?.SelectedIndex == 1 ? BackdropScope.Global : BackdropScope.Local;
            var pack = BackdropLibrary.CreatePack(nameBox?.Text ?? string.Empty, scope);

            if (pack is null)
            {
                SetStatus(scope == BackdropScope.Local
                    ? "Папку в проекте завести не удалось: проект не открыт."
                    : "Папку завести не удалось.");
                return;
            }

            if (nameBox is not null) nameBox.Text = string.Empty;

            ReloadFolders();
            SelectFolder(_folders.FirstOrDefault(f => f.Pack.Id == pack.Id));
            SetStatus($"Папка «{pack.Name}» заведена.");
        }

        private void OnDeleteFolder(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: BackdropFolderRow row }) return;

            // Щелчок по крестику не должен заодно выбирать папку под ним.
            e.Handled = true;

            BackdropLibrary.DeletePack(row.Pack);
            ReloadFolders();
            SetStatus($"Папка «{row.Name}» удалена.");
        }

        // ── Плитки ────────────────────────────────────────────────────────

        private void ReloadTiles(string? selectReference = null)
        {
            foreach (var tile in _tiles) tile.Dispose();
            _tiles.Clear();
            _currentTile = null;

            var pack = _currentFolder?.Pack;
            if (pack is not null)
            {
                foreach (var item in pack.Items)
                    _tiles.Add(new BackdropTile(item, LoadPreview(item.Reference)));
            }

            var selected = selectReference is null
                ? null
                : _tiles.FirstOrDefault(t => string.Equals(t.Item.Reference, selectReference, StringComparison.Ordinal));

            SelectTile(selected);

            var empty = this.FindControl<TextBlock>("EmptyHint");
            if (empty is not null) empty.IsVisible = _tiles.Count == 0;
        }

        private void SelectTile(BackdropTile? tile)
        {
            foreach (var t in _tiles) t.IsSelected = ReferenceEquals(t, tile);
            _currentTile = tile;

            var apply = this.FindControl<Button>("ApplyButton");
            if (apply is not null) apply.IsEnabled = tile is not null;
        }

        private void OnTilePressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border { Tag: BackdropTile tile }) SelectTile(tile);
        }

        private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not Border { Tag: BackdropTile tile }) return;

            SelectTile(tile);
            Complete(new BackdropPickResult(tile.Item.Reference));
        }

        private void OnDeleteTile(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: BackdropTile tile }) return;

            e.Handled = true;

            BackdropLibrary.Delete(tile.Item);
            ReloadFolders();
            SetStatus($"Картинка «{tile.FileName}» убрана из папки.");
        }

        /// <summary>Уменьшенная картинка для плитки. Не прочиталась — плитка без превью.</summary>
        private static Bitmap? LoadPreview(string reference)
        {
            try
            {
                var data = BackdropLibrary.Read(reference);
                if (data is null || data.Length == 0) return null;

                using var stream = new MemoryStream(data);
                return Bitmap.DecodeToWidth(stream, PreviewWidthPx);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to build a backdrop preview: {Ref}", reference);
                return null;
            }
        }

        // ── Добавление картинок ───────────────────────────────────────────

        private async void OnAddImages(object? sender, RoutedEventArgs e)
        {
            if (_currentFolder?.Pack is not { } pack)
            {
                SetStatus("Сначала выберите папку.");
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage) return;

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Картинки фона",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" }
                    }
                }
            });

            if (files.Count == 0) return;

            int added = 0;
            string? last = null;

            foreach (var file in files)
            {
                string? path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) continue;

                var item = BackdropLibrary.Import(path!, pack);
                if (item is null) continue;

                added++;
                last = item.Reference;
            }

            ReloadFolders(last);
            SetStatus(added == 0
                ? "Ни одну картинку положить не удалось."
                : $"Добавлено картинок: {added}.");
        }

        // ── Завершение ────────────────────────────────────────────────────

        private void OnApply(object? sender, RoutedEventArgs e)
        {
            if (_currentTile is not { } tile) return;
            Complete(new BackdropPickResult(tile.Item.Reference));
        }

        private void OnClearBackdrop(object? sender, RoutedEventArgs e)
            => Complete(new BackdropPickResult(null));

        private void OnCancel(object? sender, RoutedEventArgs e) => Complete(null);

        /// <summary>Щелчок по затемнению закрывает окно, по самой карточке — нет.</summary>
        private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) => Complete(null);

        private void OnCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        private void Complete(BackdropPickResult? result)
        {
            IsVisible = false;

            foreach (var tile in _tiles) tile.Dispose();
            _tiles.Clear();
            _currentTile = null;

            _tcs?.TrySetResult(result);
            _tcs = null;
        }

        private void SetStatus(string text)
        {
            var status = this.FindControl<TextBlock>("StatusText");
            if (status is not null) status.Text = text;
        }
    }
}
