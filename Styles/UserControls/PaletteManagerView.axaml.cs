using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Project;
using Writersword.Core.Services;

namespace Writersword.Styles.UserControls
{
    /// <summary>
    /// Элемент списка палитр: сама палитра, признак глобальной и представления
    /// для биндинга строки (имя, видимость, лента-метка из первых цветов).
    /// </summary>
    public class PaletteListItem : INotifyPropertyChanged
    {
        public ColorPalette Palette { get; init; } = new();

        // Область палитры (глобальная/локальная). Уведомляющее свойство — чтобы смена
        // Local/Global обновляла иконку и подсветку на месте, без пересборки списка.
        private bool _isGlobal;
        public bool IsGlobal
        {
            get => _isGlobal;
            set
            {
                if (_isGlobal == value) return;
                _isGlobal = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGlobal)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLocal)));
            }
        }
        public bool IsLocal => !IsGlobal;

        // Активная (выбранная) палитра. Уведомляющее свойство — чтобы менять активную
        // без пересборки всего списка (быстрое переключение).
        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Развёрнута ли палитра в быстром попапе (свой сворачиваемый заголовок).
        private bool _expanded = true;
        public bool Expanded
        {
            get => _expanded;
            set { if (_expanded == value) return; _expanded = value; Raise(nameof(Expanded)); }
        }

        // Идёт ли правка имени этой палитры (показывается поле ввода). Уведомляющее —
        // чтобы включать правку без пересборки списка.
        private bool _isRenaming;
        public bool IsRenaming
        {
            get => _isRenaming;
            set { if (_isRenaming == value) return; _isRenaming = value; Raise(nameof(IsRenaming)); }
        }

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Palette.Name) ? "—" : Palette.Name;

        // Видимость в быстром попапе (для биндинга двух иконок глазика).
        public bool Shown => Palette.Visible;
        public bool Hidden => !Palette.Visible;

        // Лента-метка: первые 5 цветов палитры по очереди.
        public IReadOnlyList<string> Preview => Palette.Colors.Take(5).ToList();

        // Обновить вычисляемые поля (имя/видимость/лента) после правки данных палитры.
        public void RefreshComputed()
        {
            Raise(nameof(DisplayName));
            Raise(nameof(Shown));
            Raise(nameof(Hidden));
            Raise(nameof(Preview));
        }
    }

    /// <summary>
    /// Менеджер палитр: редактируемые глобальные «стандартные цвета» (со сбросом)
    /// и именованные палитры — локальные (в проекте) и глобальные (в settings.json).
    /// Секции сворачиваются (состояние глобальное), палитры переставляются
    /// перетаскиванием, видимость в попапе переключается глазиком.
    /// </summary>
    public partial class PaletteManagerView : UserControl
    {
        // Колбэки связи с редактором цвета.
        public Action<string>? ColorPicked { get; set; }
        public Func<string>? CurrentColorProvider { get; set; }

        public ObservableCollection<string> Standard { get; } = new();
        public ObservableCollection<PaletteListItem> Palettes { get; } = new();
        // Только видимые палитры — для основного списка редактора (скрытые прячутся
        // и тут, и в быстром попапе; полный список доступен в окне управления).
        public ObservableCollection<PaletteListItem> VisiblePalettes { get; } = new();
        public ObservableCollection<string> CurrentColors { get; } = new();

        private GlobalPaletteData _global = new();
        private PaletteListItem? _selected;

        // Состояние перетаскивания строки палитры (драг-призрак по удержанию).
        private bool _rowPressed, _rowDragging;
        private PaletteListItem? _rowItem;
        private int _rowIndex = -1;
        private Avalonia.Threading.DispatcherTimer? _holdTimer;
        private IPointer? _rowPointer;
        private Control? _rowHeader;
        private Border? _rowCard;
        private ItemsControl? _rowList;
        private double _rowStartItemsY;
        private Avalonia.Animation.Transitions? _savedTransitions;
        private double _dragShift;
        private int _dragTarget = -1;

        // Автопрокрутка списка при перетаскивании у верх/нижнего края.
        private ScrollViewer? _rowScroll;
        private Point _dragPointerInScroll;
        private Avalonia.Threading.DispatcherTimer? _autoScrollTimer;

        // Модальное окно управления палитрами (порядок/видимость/имена).
        private Border? _modal;
        private bool _modalOpen;
        private IDisposable? _layerBoundsSub;
        private List<(ColorPalette p, bool global, bool visible, string name)>? _modalSnapshot;

        // Окно загрузки палитры (вкладки импорт/экспорт), центрируется над модулем.
        private Border? _ioModal;
        private bool _exportJson;   // выбранный формат на вкладке экспорта

        // Общее перетаскивание свотча (и «Стандартные», и цвета палитры — одинаково).
        // Активная коллекция при драге и действие сохранения её порядка в источник.
        private System.Collections.ObjectModel.ObservableCollection<string>? _swColors;
        private Action? _swCommit;

        // Состояние перетаскивания свотча выбранной палитры.
        private bool _palPressed, _palDragging;
        private string? _palDragHex;
        private int _palDragIndex = -1;
        private int _palDragTarget = -1;
        // Перетаскивание свотча по принципу карточек: сам свотч-Border = призрак,
        // его ячейку поднимаем по ZIndex, соседи разъезжаются по X.
        private Border? _swElem;
        private Border? _swHeader;
        private Panel? _swPanel;
        private ItemsControl? _swList;
        private IPointer? _swPointer;
        // Метрики сетки (для расчёта смещений при переносе на строки).
        private int _swColumns = 1;
        private double _swCellW;
        private double _swCellH;
        private Avalonia.Animation.Transitions? _swSavedTransitions;
        private Avalonia.Threading.DispatcherTimer? _swHoldTimer;

        public PaletteManagerView()
        {
            InitializeComponent();

            // Мини-окна импорта/экспорта отвязываем от обычной раскладки: при открытии
            // они переносятся в OverlayLayer окна, чтобы лежать поверх всего по центру.
            var host = this.FindControl<Panel>("RootHost");
            _modal = null;   // окно управления по шестерёнке удалено; поле оставлено для мёртвого кода
            _ioModal = this.FindControl<Border>("PaletteIORoot");
            if (host is not null && _ioModal is not null) host.Children.Remove(_ioModal);
        }

        // ── Импорт / экспорт палитр ───────────────────────────────────────

        // Показ/скрытие центрируемого мини-окна над модулем редактора.
        private void ShowOverlay(Border? modal)
        {
            if (modal is null) return;
            var layer = OverlayLayer.GetOverlayLayer(this);
            var host = this.FindAncestorOfType<ColorEditorOverlay>() as Visual ?? layer;
            if (layer is not null && !layer.Children.Contains(modal))
                layer.Children.Add(modal);
            PositionOverlay(layer, host, modal);
            _layerBoundsSub?.Dispose();
            _layerBoundsSub = (host ?? layer)?.GetObservable(BoundsProperty)
                .Subscribe(_ => PositionOverlay(layer, host, modal));
            modal.IsVisible = true;
        }

        private static void PositionOverlay(OverlayLayer? layer, Visual? host, Border modal)
        {
            if (layer is null) return;
            host ??= layer;
            var p = host.TranslatePoint(new Point(0, 0), layer) ?? new Point();
            modal.HorizontalAlignment = HorizontalAlignment.Left;
            modal.VerticalAlignment = VerticalAlignment.Top;
            modal.Margin = new Thickness(p.X, p.Y, 0, 0);
            modal.Width = host.Bounds.Width;
            modal.Height = host.Bounds.Height;
        }

        private void HideOverlay(Border? modal)
        {
            _layerBoundsSub?.Dispose();
            _layerBoundsSub = null;
            if (modal is null) return;
            modal.IsVisible = false;
            (modal.Parent as OverlayLayer)?.Children.Remove(modal);
        }

        // ── Окно загрузки палитры (вкладки импорт/экспорт) ──

        private void OnOpenPaletteIO(object? sender, RoutedEventArgs e)
        {
            // Сброс полей импорта.
            var name = this.FindControl<TextBox>("ImportName");
            var code = this.FindControl<TextBox>("ImportCode");
            var err = this.FindControl<TextBlock>("ImportError");
            if (name is not null) name.Text = string.Empty;
            if (code is not null) code.Text = string.Empty;
            if (err is not null) err.IsVisible = false;
            ShowIoTab(import: true);
            ShowOverlay(_ioModal);
        }

        private void OnIoTabImport(object? sender, RoutedEventArgs e) => ShowIoTab(import: true);
        private void OnIoTabExport(object? sender, RoutedEventArgs e) => ShowIoTab(import: false);
        private void OnIoClose(object? sender, RoutedEventArgs e) => HideOverlay(_ioModal);

        private void ShowIoTab(bool import)
        {
            var imp = this.FindControl<StackPanel>("IoImportPanel");
            var exp = this.FindControl<StackPanel>("IoExportPanel");
            if (imp is not null) imp.IsVisible = import;
            if (exp is not null) exp.IsVisible = !import;
            ToggleClass(this.FindControl<Button>("IoImportTab"), "palAccent", import);
            ToggleClass(this.FindControl<Button>("IoExportTab"), "palAccent", !import);

            if (!import)
            {
                // На вкладке экспорта готовим код активной палитры.
                _exportJson = false;
                UpdateExportTabs();
                FillExportCode();
            }
        }

        private void OnImportConfirm(object? sender, RoutedEventArgs e)
        {
            var codeBox = this.FindControl<TextBox>("ImportCode");
            var nameBox = this.FindControl<TextBox>("ImportName");
            var err = this.FindControl<TextBlock>("ImportError");

            var (parsedName, colors) = ParsePaletteCode(codeBox?.Text ?? string.Empty);
            if (colors.Count == 0)
            {
                if (err is not null)
                {
                    err.Text = SharedStrings.Palette_ImportEmpty;
                    err.IsVisible = true;
                }
                return;
            }

            var proj = CurrentProject;
            if (proj is null) return;

            var name = (nameBox?.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
                name = string.IsNullOrWhiteSpace(parsedName) ? SharedStrings.Palette_New : parsedName!.Trim();

            PushUndo();
            double maxOrder = 0;
            foreach (var x in proj.ProjectPalettes) if (x.Order > maxOrder) maxOrder = x.Order;
            foreach (var x in _global.Palettes) if (x.Order > maxOrder) maxOrder = x.Order;

            var p = new ColorPalette { Name = name, Order = maxOrder + 1, Colors = colors };
            proj.ProjectPalettes.Add(p);
            SaveProjectDoc();
            _selected = new PaletteListItem { Palette = p, IsGlobal = false };
            RebuildPalettes();
            HideOverlay(_ioModal);
        }

        private async void OnImportLoadFile(object? sender, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = SharedStrings.Palette_OpenFileTitle,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType(SharedStrings.Palette_FileTypeLabel)
                        {
                            Patterns = new[] { "*.json", "*.txt", "*.hex", "*.gpl" }
                        },
                        Avalonia.Platform.Storage.FilePickerFileTypes.All
                    }
                });
            if (files is null || files.Count == 0) return;
            try
            {
                await using var s = await files[0].OpenReadAsync();
                using var r = new System.IO.StreamReader(s);
                var text = await r.ReadToEndAsync();
                var code = this.FindControl<TextBox>("ImportCode");
                if (code is not null) code.Text = text;
                var nameBox = this.FindControl<TextBox>("ImportName");
                if (nameBox is not null && string.IsNullOrWhiteSpace(nameBox.Text))
                    nameBox.Text = System.IO.Path.GetFileNameWithoutExtension(files[0].Name);
            }
            catch { }
        }

        // Разбор кода палитры: сначала пробуем наш JSON (объект/массив строк),
        // иначе выдёргиваем все hex-коды (#RRGGBB / RRGGBB / #RGB) из текста.
        private static (string? name, List<string> colors) ParsePaletteCode(string text)
        {
            text = (text ?? string.Empty).Trim();
            var result = new List<string>();
            if (text.Length == 0) return (null, result);

            if (text.StartsWith("{"))
            {
                try
                {
                    var p = Newtonsoft.Json.JsonConvert.DeserializeObject<ColorPalette>(text);
                    if (p?.Colors is { Count: > 0 })
                    {
                        foreach (var c in p.Colors) { var n = NormHex(c); if (n != null) result.Add(n); }
                        if (result.Count > 0) return (p.Name, result);
                    }
                }
                catch { }
            }
            else if (text.StartsWith("["))
            {
                try
                {
                    var arr = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(text);
                    if (arr is not null)
                    {
                        foreach (var c in arr) { var n = NormHex(c); if (n != null) result.Add(n); }
                        if (result.Count > 0) return (null, result);
                    }
                }
                catch { }
            }

            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                text, "(?<![0-9A-Fa-f])#?([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})(?![0-9A-Fa-f])"))
            {
                var n = NormHex(m.Groups[1].Value);
                if (n != null && !result.Contains(n)) result.Add(n);
            }
            return (null, result);
        }

        // Нормализует hex в "#RRGGBB" (раскрывает 3-значную форму), иначе null.
        private static string? NormHex(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('#');
            if (s.Length == 3 && IsHex(s))
                s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
            if (s.Length == 6 && IsHex(s)) return "#" + s.ToUpperInvariant();
            return null;
        }

        private static bool IsHex(string s)
        {
            foreach (var ch in s)
                if (!Uri.IsHexDigit(ch)) return false;
            return true;
        }

        // ── Экспорт ──

        private void OnExportHex(object? sender, RoutedEventArgs e)
        {
            _exportJson = false; UpdateExportTabs(); FillExportCode();
        }

        private void OnExportJson(object? sender, RoutedEventArgs e)
        {
            _exportJson = true; UpdateExportTabs(); FillExportCode();
        }

        private void UpdateExportTabs()
        {
            ToggleClass(this.FindControl<Button>("ExpHexBtn"), "palAccent", !_exportJson);
            ToggleClass(this.FindControl<Button>("ExpJsonBtn"), "palAccent", _exportJson);
        }

        private void FillExportCode()
        {
            var box = this.FindControl<TextBox>("ExportCode");
            if (box is null || _selected is null) return;
            box.Text = _exportJson
                // Только Name и Colors — внутренние поля (Id/Order/Visible) для шаринга не нужны.
                ? Newtonsoft.Json.JsonConvert.SerializeObject(
                    new { _selected.Palette.Name, _selected.Palette.Colors },
                    Newtonsoft.Json.Formatting.Indented)
                : string.Join(Environment.NewLine, _selected.Palette.Colors);
        }

        private async void OnExportCopy(object? sender, RoutedEventArgs e)
        {
            var box = this.FindControl<TextBox>("ExportCode");
            var top = TopLevel.GetTopLevel(this);
            if (box?.Text is string t && top?.Clipboard is not null)
                await top.Clipboard.SetTextAsync(t);
        }

        private async void OnExportSaveFile(object? sender, RoutedEventArgs e)
        {
            var box = this.FindControl<TextBox>("ExportCode");
            var top = TopLevel.GetTopLevel(this);
            if (box is null || top is null || _selected is null) return;

            var baseName = string.IsNullOrWhiteSpace(_selected.Palette.Name) ? "palette" : _selected.Palette.Name;
            var ext = _exportJson ? "json" : "txt";
            var file = await top.StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = SharedStrings.Palette_SaveFileTitle,
                    SuggestedFileName = baseName + "." + ext,
                    DefaultExtension = ext,
                    FileTypeChoices = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType(_exportJson ? "JSON (*.json)" : "Текст (*.txt)")
                        {
                            Patterns = new[] { "*." + ext }
                        }
                    }
                });
            if (file is null) return;
            try
            {
                await using var s = await file.OpenWriteAsync();
                using var w = new System.IO.StreamWriter(s);
                await w.WriteAsync(box.Text);
            }
            catch { }
        }

        // ── Окно управления палитрами (порядок, видимость, имена) ──────────

        private void OnOpenOrderModal(object? sender, RoutedEventArgs e)
        {
            if (_modal is null) return;

            // Снимок для отмены: текущий порядок, видимость и имена.
            _modalSnapshot = Palettes
                .Select(x => (x.Palette, x.IsGlobal, x.Palette.Visible, x.Palette.Name))
                .ToList();
            _modalOpen = true;

            var layer = OverlayLayer.GetOverlayLayer(this);
            // Центрируем по модулю (оверлею редактора), а не по всему окну.
            var host = this.FindAncestorOfType<ColorEditorOverlay>() as Visual ?? layer;
            if (layer is not null && !layer.Children.Contains(_modal))
                layer.Children.Add(_modal);

            PositionModalOver(layer, host);
            _layerBoundsSub?.Dispose();
            _layerBoundsSub = (host ?? layer)?.GetObservable(BoundsProperty)
                .Subscribe(_ => PositionModalOver(layer, host));

            _modal.IsVisible = true;
        }

        // Скрим окна совмещаем с границами модуля-хоста; карточка внутри центрируется.
        private void PositionModalOver(OverlayLayer? layer, Visual? host)
        {
            if (_modal is null || layer is null) return;
            host ??= layer;
            var p = host.TranslatePoint(new Point(0, 0), layer) ?? new Point();
            _modal.HorizontalAlignment = HorizontalAlignment.Left;
            _modal.VerticalAlignment = VerticalAlignment.Top;
            _modal.Margin = new Thickness(p.X, p.Y, 0, 0);
            _modal.Width = host.Bounds.Width;
            _modal.Height = host.Bounds.Height;
        }

        private void CloseModal()
        {
            _modalOpen = false;
            _layerBoundsSub?.Dispose();
            _layerBoundsSub = null;
            if (_modal is null) return;
            _modal.IsVisible = false;
            (_modal.Parent as OverlayLayer)?.Children.Remove(_modal);
        }

        // OK: фиксируем порядок/видимость/имена и сохраняем.
        private void OnConfirmOrder(object? sender, RoutedEventArgs e)
        {
            _modalOpen = false;
            PersistOrder();
            _modalSnapshot = null;
            CloseModal();
            RebuildPalettes();
        }

        // Отмена/закрытие: возвращаем порядок, видимость и имена из снимка.
        private void OnCancelOrder(object? sender, RoutedEventArgs e)
        {
            _modalOpen = false;
            if (_modalSnapshot is not null)
            {
                foreach (var s in _modalSnapshot)
                {
                    s.p.Visible = s.visible;
                    s.p.Name = s.name;
                }

                var proj = CurrentProject;
                if (proj is not null)
                {
                    proj.ProjectPalettes.Clear();
                    foreach (var s in _modalSnapshot.Where(x => !x.global))
                        proj.ProjectPalettes.Add(s.p);
                }
                _global.Palettes.Clear();
                foreach (var s in _modalSnapshot.Where(x => x.global))
                    _global.Palettes.Add(s.p);

                _modalSnapshot = null;
            }
            CloseModal();
            RebuildPalettes();
        }

        // Перемещение палитры стрелками — в пределах своей области (локальные/глобальные).
        private void OnMovePaletteUp(object? sender, RoutedEventArgs e) => MovePaletteRow(sender, -1);
        private void OnMovePaletteDown(object? sender, RoutedEventArgs e) => MovePaletteRow(sender, 1);

        private void MovePaletteRow(object? sender, int dir)
        {
            if (sender is not Control c || c.DataContext is not PaletteListItem item) return;
            int idx = Palettes.IndexOf(item);
            if (idx < 0) return;

            int target = idx + dir;
            if (target < 0 || target >= Palettes.Count) return;

            PushUndo();
            Palettes.Move(idx, target);
            if (_modalOpen) ApplyOrderToSources(); else PersistOrder();
        }

        // Кнопка-карандаш: включает правку имени этой палитры (точечно, без пересборки).
        private void OnRenamePalette(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is PaletteListItem item)
            {
                foreach (var p in Palettes)
                    if (p.IsRenaming && !ReferenceEquals(p, item)) p.IsRenaming = false;
                SelectPalette(item);
                item.IsRenaming = true;
            }
            e.Handled = true;
        }

        // При входе в правку имени сразу выделяем весь текст.
        private void OnRenameGotFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb) tb.SelectAll();
        }

        private void OnRenameKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CommitRename(sender);
        }

        // Имя фиксируется по Enter или снятии фокуса; затем выходим из режима правки.
        private void OnRenameCommit(object? sender, RoutedEventArgs e) => CommitRename(sender);

        private void CommitRename(object? sender)
        {
            if (sender is not TextBox tb || tb.DataContext is not PaletteListItem item) return;
            var newName = (tb.Text ?? string.Empty).Trim();
            if (item.Palette.Name == newName) { item.IsRenaming = false; return; }
            PushUndo();
            item.Palette.Name = newName;
            item.IsRenaming = false;
            item.RefreshComputed();   // обновляет отображаемое имя без пересборки списка
            if (item.IsGlobal) SaveGlobal(); else SaveProjectDoc();
        }

        /// <summary>Перезагрузить данные из хранилищ (глобального и проектного).</summary>
        public void Refresh()
        {
            _undo.Clear();
            _redo.Clear();
            LoadGlobal();
            ApplyCollapsed();
            RebuildStandard();
            RebuildPalettes();
        }

        // Режим вкладок снаружи: показываем только одну секцию, заголовки секций
        // прячем (их роль выполняют вкладки редактора). which = "standard" | "palettes".
        public void ShowSection(string which)
        {
            var stdH = this.FindControl<Control>("StdHeader");
            var palH = this.FindControl<Control>("PalHeader");
            var stdB = this.FindControl<Control>("StandardBody");
            var palB = this.FindControl<Control>("PalettesBody");
            bool std = which == "standard";
            if (stdH is not null) stdH.IsVisible = false;
            if (palH is not null) palH.IsVisible = false;
            if (stdB is not null) stdB.IsVisible = std;
            if (palB is not null) palB.IsVisible = !std;
        }

        // ── Сворачивание секций ───────────────────────────────────────────

        private void ApplyCollapsed()
        {
            SetSection("std", "StandardBody", "StdChevron");
            SetSection("pal", "PalettesBody", "PalChevron");
        }

        // Применяет состояние секции: показывает/прячет тело и поворачивает шеврон.
        private void SetSection(string key, string bodyName, string chevronName)
        {
            bool collapsed = IsCollapsed(key);
            var body = this.FindControl<Control>(bodyName);
            if (body is not null) body.IsVisible = !collapsed;
            var chev = this.FindControl<Control>(chevronName);
            if (chev is not null)
                chev.RenderTransform = new RotateTransform(collapsed ? -90 : 0);
        }

        private bool IsCollapsed(string key) =>
            _global.CollapsedSections.TryGetValue(key, out var v) && v;

        private void OnSectionHeader(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.Tag is string key)
            {
                _global.CollapsedSections[key] = !IsCollapsed(key);
                SaveGlobal();
                ApplyCollapsed();
            }
        }

        // ── Стандартные цвета ─────────────────────────────────────────────

        private void RebuildStandard()
        {
            Standard.Clear();
            foreach (var c in _global.StandardColors) Standard.Add(Norm(c));
        }

        private void OnResetStandard(object? sender, RoutedEventArgs e)
        {
            _global.StandardColors = StandardColors.Default();
            SaveGlobal();
            RebuildStandard();
        }

        private void OnAddStandard(object? sender, RoutedEventArgs e)
        {
            var hex = Current();
            if (hex is null || _global.StandardColors.Count >= StandardColors.MaxCount) return;
            if (_global.StandardColors.Any(c => Norm(c) == hex)) return;
            _global.StandardColors.Add(hex);
            SaveGlobal();
            RebuildStandard();
        }

        private void OnRemoveStandard(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is string hex)
            {
                _global.StandardColors.RemoveAll(x => Norm(x) == Norm(hex));
                SaveGlobal();
                RebuildStandard();
            }
        }

        // ── Палитры ───────────────────────────────────────────────────────

        // ── История изменений палитр (Ctrl+Z / Ctrl+Y) ───────────────────
        // Снимок захватывает оба источника палитр (проектные и глобальные) и
        // выбранную палитру: одна операция (например, смена области) меняет сразу
        // оба списка.
        private sealed class PaletteSnapshot
        {
            public List<ColorPalette> Project = new();
            public List<ColorPalette> Global = new();
            public string? SelectedId;
        }

        private readonly List<PaletteSnapshot> _undo = new();
        private readonly List<PaletteSnapshot> _redo = new();
        private const int MaxHistory = 50;
        private bool _restoring;

        // Глубокая копия списка палитр (через JSON, чтобы копировались все поля).
        private static List<ColorPalette> CloneList(List<ColorPalette>? src)
        {
            if (src is null || src.Count == 0) return new List<ColorPalette>();
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(src);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<List<ColorPalette>>(json)
                   ?? new List<ColorPalette>();
        }

        // Снимок текущего состояния обоих источников и выбранной палитры.
        private PaletteSnapshot Capture() => new PaletteSnapshot
        {
            Project = CloneList(CurrentProject?.ProjectPalettes),
            Global = CloneList(_global.Palettes),
            SelectedId = _selected?.Palette.Id
        };

        // Запомнить состояние ДО изменения. Вызывается в начале каждой операции,
        // меняющей палитры. Новое действие очищает стек повтора.
        private void PushUndo()
        {
            if (_restoring) return;
            _undo.Add(Capture());
            if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
            _redo.Clear();
        }

        // Отмена последнего изменения палитр.
        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Add(Capture());
            var snap = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            RestoreSnapshot(snap);
        }

        // Повтор отменённого изменения палитр.
        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(Capture());
            var snap = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            RestoreSnapshot(snap);
        }

        // Восстанавливает состояние из снимка: подменяет оба списка-источника,
        // сохраняет и пересобирает строки, сохраняя выбранную палитру.
        private void RestoreSnapshot(PaletteSnapshot snap)
        {
            _restoring = true;
            try
            {
                var proj = CurrentProject;
                if (proj is not null)
                {
                    proj.ProjectPalettes.Clear();
                    foreach (var p in snap.Project) proj.ProjectPalettes.Add(p);
                }
                _global.Palettes.Clear();
                foreach (var p in snap.Global) _global.Palettes.Add(p);

                _selected = null;
                SaveGlobal();
                SaveProjectDoc();
                RebuildPalettes(snap.SelectedId);
            }
            finally { _restoring = false; }
        }

        private void RebuildPalettes(string? selectId = null)
        {
            var prevId = selectId ?? _selected?.Palette.Id;

            var raw = new List<(ColorPalette p, bool g)>();
            var proj = CurrentProject;
            if (proj is not null)
                foreach (var p in proj.ProjectPalettes) raw.Add((p, false));
            foreach (var p in _global.Palettes) raw.Add((p, true));

            // Единый порядок отображения — по полю Order (общий для локальных и
            // глобальных). OrderBy стабилен, так что старые данные (Order=0) сохранят
            // текущий порядок до первой перестановки.
            raw = raw.OrderBy(x => x.p.Order).ToList();

            // Активной остаётся та же палитра независимо от видимости — чтобы скрытие
            // глазиком ничего не переставляло и не перепрыгивало. Если активной нет —
            // берём первую в списке.
            var activeId = raw.Any(x => x.p.Id == prevId)
                ? prevId
                : (raw.Count > 0 ? raw[0].p.Id : null);

            Palettes.Clear();
            foreach (var (p, g) in raw)
                Palettes.Add(new PaletteListItem { Palette = p, IsGlobal = g, IsActive = p.Id == activeId });

            // Основной список — только видимые; окно управления показывает все.
            VisiblePalettes.Clear();
            foreach (var it in Palettes)
                if (it.Palette.Visible) VisiblePalettes.Add(it);

            _selected = Palettes.FirstOrDefault(x => x.Palette.Id == activeId);
            LoadSelected();
        }

        // Быстрый выбор активной палитры: переключаем флаг IsActive только у старой и
        // новой строк (уведомляющее свойство), без пересборки всего списка.
        private void SelectPalette(PaletteListItem item)
        {
            foreach (var p in Palettes)
                if (p.IsActive && !ReferenceEquals(p, item)) p.IsActive = false;
            item.IsActive = true;
            _selected = item;
            LoadSelected();
        }

        private void LoadSelected()
        {
            CurrentColors.Clear();
            if (_selected is null) { ShowDetail(false); ActiveChanged?.Invoke(); return; }

            foreach (var c in _selected.Palette.Colors) CurrentColors.Add(Norm(c));

            var nameBox = this.FindControl<TextBox>("PaletteName");
            if (nameBox is not null) nameBox.Text = _selected.Palette.Name;

            ToggleClass(this.FindControl<Button>("ScopeLocalBtn"), "active", !_selected.IsGlobal);
            ToggleClass(this.FindControl<Button>("ScopeGlobalBtn"), "active", _selected.IsGlobal);
            ShowDetail(true);
            ActiveChanged?.Invoke();
        }

        private void ShowDetail(bool visible)
        {
            var d = this.FindControl<Control>("PaletteDetail");
            if (d is not null) d.IsVisible = visible;
        }

        private void OnNewPalette(object? sender, RoutedEventArgs e)
        {
            var proj = CurrentProject;
            if (proj is null) return;

            PushUndo();

            double maxOrder = 0;
            foreach (var x in proj.ProjectPalettes) if (x.Order > maxOrder) maxOrder = x.Order;
            foreach (var x in _global.Palettes) if (x.Order > maxOrder) maxOrder = x.Order;

            var p = new ColorPalette { Name = SharedStrings.Palette_New, Order = maxOrder + 1 };
            proj.ProjectPalettes.Add(p);
            SaveProjectDoc();
            _selected = new PaletteListItem { Palette = p, IsGlobal = false };
            RebuildPalettes();
        }

        private void OnDeletePalette(object? sender, RoutedEventArgs e)
        {
            if (_selected is null) return;
            if (_selected.IsGlobal)
            {
                _global.Palettes.RemoveAll(x => x.Id == _selected.Palette.Id);
                SaveGlobal();
            }
            else
            {
                CurrentProject?.ProjectPalettes.RemoveAll(x => x.Id == _selected.Palette.Id);
                SaveProjectDoc();
            }
            _selected = null;
            RebuildPalettes();
        }

        // Клик по строке основного списка делает палитру активной (выбранной).
        // Нажатие по кнопке-мусорке внутри строки сюда не относим — удаление
        // обрабатывается отдельно и не должно попутно «выбирать» строку.
        private void OnPaletteRowSelect(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (sender is Control c && c.DataContext is PaletteListItem item)
            {
                _selected = item;
                RebuildPalettes();
            }
        }

        // Удаление конкретной палитры из её строки (красноватая мусорка).
        private void OnDeletePaletteRow(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not PaletteListItem item) return;

            PushUndo();

            if (item.IsGlobal)
            {
                _global.Palettes.RemoveAll(x => x.Id == item.Palette.Id);
                if (!_modalOpen) SaveGlobal();
            }
            else
            {
                CurrentProject?.ProjectPalettes.RemoveAll(x => x.Id == item.Palette.Id);
                if (!_modalOpen) SaveProjectDoc();
            }

            // Точечно убираем строку (без Clear всего списка — иначе падает ContentPresenter).
            bool wasActive = item.IsActive;
            Palettes.Remove(item);
            VisiblePalettes.Remove(item);
            if (wasActive)
            {
                var next = Palettes.FirstOrDefault();
                if (next is not null) SelectPalette(next);
                else { _selected = null; LoadSelected(); }
            }
            e.Handled = true;
        }

        // Список палитр в окошке по шестерёнке реализуется во флайауте, поэтому
        // его источник назначаем при загрузке содержимого флайаута.
        private void OnOrderListLoaded(object? sender, RoutedEventArgs e)
        {
            if (sender is ItemsControl ic) ic.ItemsSource = Palettes;
        }

        private void OnNameKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CommitName();
        }

        private void OnNameCommit(object? sender, RoutedEventArgs e) => CommitName();

        private void CommitName()
        {
            if (_selected is null) return;
            var box = this.FindControl<TextBox>("PaletteName");
            var name = (box?.Text ?? string.Empty).Trim();
            if (_selected.Palette.Name == name) return;
            _selected.Palette.Name = name;
            PersistSelected();
            RebuildPalettes();
        }

        private void OnScopeLocal(object? sender, RoutedEventArgs e) => MoveScope(toGlobal: false);
        private void OnScopeGlobal(object? sender, RoutedEventArgs e) => MoveScope(toGlobal: true);

        private void MoveScope(bool toGlobal)
        {
            if (_selected is null || _selected.IsGlobal == toGlobal) return;
            PushUndo();
            var p = _selected.Palette;
            var proj = CurrentProject;

            if (toGlobal)
            {
                proj?.ProjectPalettes.RemoveAll(x => x.Id == p.Id);
                _global.Palettes.Add(p);
            }
            else
            {
                if (proj is null) return;
                _global.Palettes.RemoveAll(x => x.Id == p.Id);
                proj.ProjectPalettes.Add(p);
            }
            SaveGlobal();
            SaveProjectDoc();
            // Обновляем область у текущего элемента на месте (без Clear/пересборки) —
            // иконка области и подсветка Local/Global меняются по биндингу, без мерцания.
            _selected.IsGlobal = toGlobal;
        }

        // Переключение видимости палитры в быстром попапе (глазик).
        private void OnToggleVisible(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is PaletteListItem item)
            {
                PushUndo();
                item.Palette.Visible = !item.Palette.Visible;
                item.RefreshComputed();   // обновляет иконку глаза и приглушение, без пересборки
                if (!_modalOpen) { if (item.IsGlobal) SaveGlobal(); else SaveProjectDoc(); }
            }
            e.Handled = true;
        }

        // ── Перетаскивание строк палитр (порядок = приоритет показа) ──────

        private void OnPaletteRowPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not PaletteListItem item) return;

            _rowPressed = true;
            _rowDragging = false;
            _rowItem = item;
            _rowIndex = Palettes.IndexOf(item);
            _rowHeader = c;
            _rowPointer = e.Pointer;
            _rowCard = (c as Visual)?.FindAncestorOfType<Border>();
            _rowList = (c as Visual)?.FindAncestorOfType<ItemsControl>();
            _rowStartItemsY = _rowList is not null ? e.GetPosition(_rowList).Y : e.GetPosition(this).Y;

            // Удержание 80 мс в любом месте строки -> старт драга-призрака. Захват не
            // делаем сразу, чтобы быстрый клик по кнопке/строке остался кликом.
            _holdTimer?.Stop();
            _holdTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            _holdTimer.Tick += OnHoldTick;
            _holdTimer.Start();
        }

        private void OnHoldTick(object? sender, EventArgs e)
        {
            _holdTimer?.Stop();
            if (!_rowPressed || _rowDragging || _rowCard is null) return;
            _rowDragging = true;
            _rowPointer?.Capture(_rowHeader);
            _rowCard.ZIndex = 100;
            _rowCard.Opacity = 0.9;
            // На время драга гасим наведение у списка: иначе под курсором мерцают
            // значки строк и всплывают тултипы. Драг идёт через захват указателя.
            if (_rowList is not null) _rowList.IsHitTestVisible = false;
            // Призрак следует за курсором мгновенно — отключаем его переход на время драга.
            if (_rowCard.RenderTransform is TranslateTransform tt)
            {
                _savedTransitions = tt.Transitions;
                tt.Transitions = null;
            }
            _dragShift = _rowCard.Bounds.Height + 4;
            _dragTarget = _rowIndex;

            // Прокручиваемый предок — чтобы тянуть список к краю при драге.
            _rowScroll = _rowCard.FindAncestorOfType<ScrollViewer>();
            // Стартовая точка курсора — центр карточки (чтобы до первого движения
            // таймер не считал, что курсор у края, и не прокручивал самовольно).
            if (_rowScroll is not null)
            {
                var c = _rowCard.TranslatePoint(
                    new Point(_rowCard.Bounds.Width / 2, _rowCard.Bounds.Height / 2), _rowScroll);
                if (c is Point cp) _dragPointerInScroll = cp;
            }
            if (_autoScrollTimer is null)
            {
                _autoScrollTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _autoScrollTimer.Tick += OnAutoScrollTick;
            }
            _autoScrollTimer.Start();
        }

        private void OnPaletteRowMoved(object? sender, PointerEventArgs e)
        {
            if (!_rowPressed || !_rowDragging || _rowCard is null) return;

            var items = (sender as Visual)?.FindAncestorOfType<ItemsControl>();
            if (items is null) return;

            if (_rowScroll is not null) _dragPointerInScroll = e.GetPosition(_rowScroll);
            ApplyDrag(items, e.GetPosition(items).Y);
        }

        // Двигает призрак за курсором и пересчитывает слот под ним.
        private void ApplyDrag(ItemsControl items, double pointerItemsY)
        {
            if (_rowCard is null) return;

            // Призрак едет за курсором по вертикали.
            double dy = pointerItemsY - _rowStartItemsY;
            if (_rowCard.RenderTransform is TranslateTransform tt) tt.Y = dy;

            // Слот под призраком: соседи плавно разъезжаются, освобождая место.
            var tl = _rowCard.TranslatePoint(new Point(0, 0), items) ?? new Point();
            double centerY = tl.Y + _rowCard.Bounds.Height / 2.0;
            int target = TargetRowAt(items, new Point(_rowCard.Bounds.Width / 2.0, centerY));
            if (target < 0) return;
            if (target != _dragTarget)
            {
                _dragTarget = target;
                ShiftRows(items);
            }
        }

        // Пока курсор удерживается у верх/нижнего края списка — плавно прокручиваем
        // его, ускоряясь ближе к краю, и пересчитываем положение призрака.
        private void OnAutoScrollTick(object? sender, EventArgs e)
        {
            if (!_rowDragging || _rowScroll is null || _rowList is null) return;

            double vh = _rowScroll.Viewport.Height;
            double y = _dragPointerInScroll.Y;
            const double zone = 45;

            double step = 0;
            if (y < zone) step = -(zone - y) / zone * 14;
            else if (y > vh - zone) step = (y - (vh - zone)) / zone * 14;
            if (step == 0) return;

            var off = _rowScroll.Offset;
            double maxY = Math.Max(0, _rowScroll.Extent.Height - vh);
            double newY = Math.Clamp(off.Y + step, 0, maxY);
            if (Math.Abs(newY - off.Y) < 0.01) return;
            _rowScroll.Offset = new Vector(off.X, newY);

            // Список сдвинулся — пересчитываем призрак и слот под текущим курсором.
            var p = _rowScroll.TranslatePoint(_dragPointerInScroll, _rowList);
            if (p is Point pi) ApplyDrag(_rowList, pi.Y);
        }

        // Сдвигает соседние строки, чтобы открыть слот на позиции _dragTarget
        // (анимация — через переход трансформа в шаблоне строки).
        private void ShiftRows(ItemsControl items)
        {
            foreach (var cont in items.GetRealizedContainers())
            {
                var card = CardOf(cont);
                if (card is null || ReferenceEquals(card, _rowCard)) continue;
                int i = items.IndexFromContainer(cont);
                double shift = 0;
                if (_dragTarget > _rowIndex && i > _rowIndex && i <= _dragTarget) shift = -_dragShift;
                else if (_dragTarget < _rowIndex && i >= _dragTarget && i < _rowIndex) shift = _dragShift;
                if (card.RenderTransform is TranslateTransform tt) tt.Y = shift;
            }
        }

        private static Border? CardOf(Control cont)
            => (cont as ContentPresenter)?.Child as Border ?? cont as Border;

        private void OnPaletteRowReleased(object? sender, PointerReleasedEventArgs e)
        {
            _holdTimer?.Stop();
            _autoScrollTimer?.Stop();
            if (!_rowPressed) return;
            _rowPressed = false;

            if (_rowDragging)
            {
                _rowPointer?.Capture(null);
                var items = (sender as Visual)?.FindAncestorOfType<ItemsControl>();
                int target = _dragTarget;

                // Сброс смещений соседей.
                if (items is not null)
                    foreach (var cont in items.GetRealizedContainers())
                    {
                        var card = CardOf(cont);
                        if (card is not null && !ReferenceEquals(card, _rowCard)
                            && card.RenderTransform is TranslateTransform t)
                            t.Y = 0;
                    }

                // Призрак мгновенно встаёт в слот (Y=0 при ещё отключённом переходе),
                // затем переход возвращаем.
                if (_rowCard is not null)
                {
                    if (_rowCard.RenderTransform is TranslateTransform dt)
                    {
                        dt.Y = 0;
                        dt.Transitions = _savedTransitions;
                    }
                    _rowCard.ZIndex = 0;
                    _rowCard.Opacity = 1;
                }

                if (target >= 0 && _rowIndex >= 0 && target != _rowIndex)
                {
                    PushUndo();
                    Palettes.Move(_rowIndex, target);
                    if (_modalOpen) ApplyOrderToSources(); else PersistOrder();
                }
                else if (_rowItem is not null)
                {
                    // Зажал, но не перетащил (палитра не сменила слот) — это клик:
                    // делаем палитру активной, иначе выбор «не срабатывал».
                    SelectPalette(_rowItem);
                }
                _dragTarget = -1;
            }
            else if (_rowItem is not null
                     && !(e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
                     && !(e.Source is Visual v2 && v2.FindAncestorOfType<TextBox>(includeSelf: true) is not null))
            {
                // Быстрый клик по строке (не по кнопке/имени) делает палитру активной.
                SelectPalette(_rowItem);
            }

            if (_rowList is not null) _rowList.IsHitTestVisible = true;

            _rowDragging = false;
            _rowItem = null;
            _rowIndex = -1;
            _rowCard = null;
            _rowHeader = null;
            _rowList = null;
            _rowPointer = null;
            _rowScroll = null;
        }

        private static int TargetRowAt(ItemsControl items, Point p)
        {
            foreach (var cont in items.GetRealizedContainers())
                if (cont.Bounds.Contains(p)) return items.IndexFromContainer(cont);
            return -1;
        }

        // Переписывает порядок исходных списков из текущего порядка отображения.
        // Переносит текущий порядок отображения в исходные списки (без сохранения).
        // Нужно во время работы окна, чтобы перестановка не терялась при пересборке.
        private void ApplyOrderToSources()
        {
            // Сквозной порядок: позиция в общем списке = Order палитры.
            for (int i = 0; i < Palettes.Count; i++) Palettes[i].Palette.Order = i;

            var proj = CurrentProject;
            if (proj is not null)
            {
                var ordered = Palettes.Where(x => !x.IsGlobal).Select(x => x.Palette).ToList();
                proj.ProjectPalettes.Clear();
                foreach (var p in ordered) proj.ProjectPalettes.Add(p);
            }

            var globalOrdered = Palettes.Where(x => x.IsGlobal).Select(x => x.Palette).ToList();
            _global.Palettes.Clear();
            foreach (var p in globalOrdered) _global.Palettes.Add(p);
        }

        private void PersistOrder()
        {
            ApplyOrderToSources();
            SaveGlobal();
            SaveProjectDoc();
        }

        private void OnAddPaletteColor(object? sender, RoutedEventArgs e) => AddCurrentColor();

        // Добавляет текущий цвет редактора в активную палитру. Вызывается и из
        // закреплённой плашки редактора — чтобы добавлять без прокрутки к палитре.
        public void AddCurrentColor()
        {
            if (_selected is null) return;
            var hex = Current();
            if (hex is null) return;
            if (_selected.Palette.Colors.Any(c => Norm(c) == hex)) return;
            PushUndo();
            _selected.Palette.Colors.Add(hex);
            CurrentColors.Add(hex);
            PersistSelected();
            RebuildPalettes();
        }

        // Имя активной палитры (для подписи закреплённой плашки).
        public string ActivePaletteName => _selected?.Palette.Name ?? string.Empty;

        // Цвета активной палитры (для генератора шума из палитры).
        public IReadOnlyList<string>? ActivePaletteColors => _selected?.Palette.Colors;

        // Есть ли активная палитра (плашка показывается только когда есть).
        public bool HasActivePalette => _selected is not null;

        // Срабатывает при смене активной палитры — редактор обновляет плашку.
        public event Action? ActiveChanged;

        private void OnPaletteColorPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border b || b.DataContext is not string hex) return;
            var items = (b as Visual)?.FindAncestorOfType<ItemsControl>();
            if (items is null) return;

            bool isStandard = items.Name == "StandardItems";
            if (!isStandard && _selected is null) return;

            // ПКМ — удалить цвет.
            if (e.GetCurrentPoint(b).Properties.IsRightButtonPressed)
            {
                if (isStandard)
                {
                    _global.StandardColors.RemoveAll(c => Norm(c) == Norm(hex));
                    SaveGlobal();
                    RebuildStandard();
                }
                else
                {
                    PushUndo();
                    _selected!.Palette.Colors.RemoveAll(c => Norm(c) == Norm(hex));
                    for (int i = CurrentColors.Count - 1; i >= 0; i--)
                        if (Norm(CurrentColors[i]) == Norm(hex)) CurrentColors.RemoveAt(i);
                    PersistSelected();
                    RebuildPalettes();
                }
                e.Handled = true;
                return;
            }

            // Коллекция и сохранение её порядка — в зависимости от списка.
            if (isStandard)
            {
                _swColors = Standard;
                _swCommit = () =>
                {
                    _global.StandardColors.Clear();
                    foreach (var c in Standard) _global.StandardColors.Add(Norm(c));
                    SaveGlobal();
                };
            }
            else
            {
                _swColors = CurrentColors;
                _swCommit = () =>
                {
                    if (_selected is null) return;
                    _selected.Palette.Colors.Clear();
                    foreach (var c in CurrentColors) _selected.Palette.Colors.Add(Norm(c));
                    PersistSelected();
                    _selected.RefreshComputed();
                };
            }

            _palPressed = true;
            _palDragging = false;
            _palDragHex = hex;
            _palDragIndex = _swColors.IndexOf(hex);
            _palDragTarget = _palDragIndex;
            _swElem = b;
            _swHeader = b;
            _swPointer = e.Pointer;
            _swPanel = (b as Visual)?.FindAncestorOfType<Panel>();
            _swList = items;

            // Удержание 80 мс -> старт драга (как у карточек). Быстрый клик остаётся кликом.
            _swHoldTimer?.Stop();
            _swHoldTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _swHoldTimer.Tick += OnSwatchHoldTick;
            _swHoldTimer.Start();
            e.Handled = true;
        }

        private void OnSwatchHoldTick(object? sender, EventArgs e)
        {
            _swHoldTimer?.Stop();
            if (!_palPressed || _palDragging || _swElem is null) return;
            _palDragging = true;
            _swPointer?.Capture(_swHeader);
            if (_swPanel is not null) _swPanel.ZIndex = 100;
            _swElem.Opacity = 0.9;
            if (_swList is not null) _swList.IsHitTestVisible = false;
            // Призрак следует за курсором мгновенно — отключаем переход на время драга.
            if (_swElem.RenderTransform is TranslateTransform tt)
            {
                _swSavedTransitions = tt.Transitions;
                tt.Transitions = null;
            }
            _palDragTarget = _palDragIndex;

            // Метрики сетки: размер ячейки (контейнер с полями) и число колонок в строке.
            if (_swList is not null)
            {
                var cont = _swList.ContainerFromIndex(_palDragIndex);
                if (cont is not null)
                {
                    _swCellW = cont.Bounds.Width;
                    _swCellH = cont.Bounds.Height;
                }
                _swColumns = ComputeColumns(_swList);
            }
        }

        // Сколько ячеек помещается в одной строке (по контейнерам верхнего ряда).
        private static int ComputeColumns(ItemsControl items)
        {
            double minY = double.MaxValue;
            foreach (var c in items.GetRealizedContainers())
                if (c.Bounds.Y < minY) minY = c.Bounds.Y;
            int cols = 0;
            foreach (var c in items.GetRealizedContainers())
                if (Math.Abs(c.Bounds.Y - minY) < 1.0) cols++;
            return Math.Max(1, cols);
        }

        private void OnPaletteColorMoved(object? sender, PointerEventArgs e)
        {
            if (!_palPressed || !_palDragging || _swElem is null) return;
            var items = (sender as Visual)?.FindAncestorOfType<ItemsControl>();
            if (items is null) return;
            ApplyColorDrag(items, e.GetPosition(items));
        }

        // Призрак держится под курсором (X и Y); цель — ближайшая ячейка сетки;
        // соседи разъезжаются с учётом числа колонок (перенос на строки работает).
        private void ApplyColorDrag(ItemsControl items, Point pointer)
        {
            if (_swElem is null) return;

            // Смещение призрака = курсор минус центр исходного слота (порядок при драге
            // не меняем, поэтому слот фиксирован, и трансформ всегда считается верно).
            var dragCont = items.ContainerFromIndex(_palDragIndex) as Visual;
            if (dragCont is not null && _swElem.RenderTransform is TranslateTransform tt)
            {
                var tl = dragCont.TranslatePoint(new Point(0, 0), items) ?? new Point();
                tt.X = pointer.X - (tl.X + dragCont.Bounds.Width / 2.0);
                tt.Y = pointer.Y - (tl.Y + dragCont.Bounds.Height / 2.0);
            }

            int target = NearestCell(items, pointer);
            if (target < 0) return;
            if (target != _palDragTarget)
            {
                _palDragTarget = target;
                ShiftCells(items);
            }
        }

        // Ближайшая ячейка к курсору по 2D-расстоянию (на краях — крайняя, «упирание»).
        private static int NearestCell(ItemsControl items, Point p)
        {
            int best = -1;
            double bestD = double.MaxValue;
            foreach (var cont in items.GetRealizedContainers())
            {
                var tl = (cont as Visual)?.TranslatePoint(new Point(0, 0), items) ?? new Point();
                double cx = tl.X + cont.Bounds.Width / 2.0;
                double cy = tl.Y + cont.Bounds.Height / 2.0;
                double dx = cx - p.X, dy = cy - p.Y;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = items.IndexFromContainer(cont); }
            }
            return best;
        }

        // Сдвигает соседей в их новые слоты сетки (i±1), включая перенос между строками.
        private void ShiftCells(ItemsControl items)
        {
            int cols = Math.Max(1, _swColumns);
            foreach (var cont in items.GetRealizedContainers())
            {
                var sw = SwatchOf(cont);
                if (sw is null || ReferenceEquals(sw, _swElem)) continue;
                int i = items.IndexFromContainer(cont);
                int ni = i;
                if (_palDragTarget > _palDragIndex && i > _palDragIndex && i <= _palDragTarget) ni = i - 1;
                else if (_palDragTarget < _palDragIndex && i >= _palDragTarget && i < _palDragIndex) ni = i + 1;

                double tx = 0, ty = 0;
                if (ni != i)
                {
                    tx = (ni % cols - i % cols) * _swCellW;
                    ty = (ni / cols - i / cols) * _swCellH;
                }
                if (sw.RenderTransform is TranslateTransform tt) { tt.X = tx; tt.Y = ty; }
            }
        }

        // Свотч-Border внутри контейнера ячейки.
        private static Border? SwatchOf(Control cont)
        {
            var root = (cont as ContentPresenter)?.Child as Panel ?? cont as Panel;
            if (root is null) return null;
            foreach (var ch in root.Children)
                if (ch is Border bd && bd.Classes.Contains("swatch")) return bd;
            return null;
        }

        private void OnPaletteColorReleased(object? sender, PointerReleasedEventArgs e)
        {
            _swHoldTimer?.Stop();
            if (!_palPressed) return;
            _palPressed = false;

            if (_palDragging)
            {
                _swPointer?.Capture(null);
                var items = (sender as Visual)?.FindAncestorOfType<ItemsControl>();
                int target = _palDragTarget;

                // Снимаем смещения БЕЗ анимации: иначе на момент перестановки соседи
                // дёргаются (переход тянет трансформ к нулю, пока раскладка уже прыгнула
                // в новые слоты). Переходы вернём на следующий тик.
                var restore = new List<(TranslateTransform t, Avalonia.Animation.Transitions? saved)>();
                if (items is not null)
                    foreach (var cont in items.GetRealizedContainers())
                    {
                        var sw = SwatchOf(cont);
                        if (sw?.RenderTransform is TranslateTransform t)
                        {
                            var saved = ReferenceEquals(sw, _swElem) ? _swSavedTransitions : t.Transitions;
                            t.Transitions = null;
                            t.X = 0;
                            t.Y = 0;
                            restore.Add((t, saved));
                        }
                    }

                if (_swElem is not null) _swElem.Opacity = 1;
                if (_swPanel is not null) _swPanel.ZIndex = 0;

                if (target >= 0 && _palDragIndex >= 0 && target != _palDragIndex && _swColors is not null)
                {
                    // Стандартные цвета не входят в undo палитр; для палитр — снимок.
                    if (!ReferenceEquals(_swColors, Standard)) PushUndo();
                    _swColors.Move(_palDragIndex, target);
                    _swCommit?.Invoke();
                }

                // Возвращаем переходы после применения раскладки — чтобы сама фиксация
                // не анимировалась, а будущие перетаскивания снова были плавными.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var (t, saved) in restore) t.Transitions = saved;
                });
            }
            else if (_palDragHex is string h)
            {
                ColorPicked?.Invoke(h);   // быстрый клик — выбрать цвет
            }

            if (_swList is not null) _swList.IsHitTestVisible = true;

            _palDragging = false;
            _palDragHex = null;
            _palDragIndex = -1;
            _palDragTarget = -1;
            _swElem = null;
            _swHeader = null;
            _swPanel = null;
            _swList = null;
            _swPointer = null;
            _swColors = null;
            _swCommit = null;
        }

        private void OnRemovePaletteColor(object? sender, RoutedEventArgs e)
        {
            if (_selected is null || sender is not Control c || c.DataContext is not string hex) return;
            PushUndo();
            _selected.Palette.Colors.RemoveAll(x => Norm(x) == Norm(hex));
            for (int i = CurrentColors.Count - 1; i >= 0; i--)
                if (Norm(CurrentColors[i]) == Norm(hex)) CurrentColors.RemoveAt(i);
            PersistSelected();
            RebuildPalettes();
        }

        private void PersistSelected()
        {
            if (_selected is null) return;
            if (_selected.IsGlobal) SaveGlobal();
            else SaveProjectDoc();
        }

        // ── Хранилища ─────────────────────────────────────────────────────

        private void LoadGlobal()
        {
            var s = CoreServices.GetService<ISettingsService>();
            _global = s?.GetModuleSettings<GlobalPaletteData>("ColorPalettes") ?? new GlobalPaletteData();
            if (_global.StandardColors == null || _global.StandardColors.Count == 0)
                _global.StandardColors = StandardColors.Default();
            _global.Palettes ??= new List<ColorPalette>();
            _global.CollapsedSections ??= new Dictionary<string, bool>();
        }

        private void SaveGlobal()
        {
            var s = CoreServices.GetService<ISettingsService>();
            if (s is null) return;
            s.SaveModuleSettings("ColorPalettes", _global);
            s.Save();
        }

        private static ProjectFile? CurrentProject =>
            CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context?.Project;

        // Сохранение проекта дебаунсим: частые правки палитр схлопываются в одно
        // сохранение, иначе несколько полных записей идут подряд и нагружают/спамят.
        private static Avalonia.Threading.DispatcherTimer? _saveDebounce;

        private static void SaveProjectDoc()
        {
            if (_saveDebounce is null)
            {
                _saveDebounce = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(600)
                };
                _saveDebounce.Tick += (_, _) =>
                {
                    _saveDebounce!.Stop();
                    DoSaveProjectDoc();
                };
            }
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        private static void DoSaveProjectDoc()
        {
            try
            {
                var tab = CoreServices.GetService<ITabCollection>()?.ActiveTab;
                var wf = CoreServices.GetService<IProjectWorkflow>();
                if (tab is not null && wf is not null) _ = wf.SaveDocumentAsync(tab, showNotification: false);
            }
            catch { }
        }

        private string? Current()
        {
            var hex = CurrentColorProvider?.Invoke();
            if (string.IsNullOrWhiteSpace(hex)) return null;
            return Norm(hex);
        }

        private static string Norm(string? hex) => (hex ?? string.Empty).Trim().ToUpperInvariant();

        private static void ToggleClass(Button? b, string cls, bool on)
        {
            if (b is null) return;
            if (on) { if (!b.Classes.Contains(cls)) b.Classes.Add(cls); }
            else b.Classes.Remove(cls);
        }
    }
}
