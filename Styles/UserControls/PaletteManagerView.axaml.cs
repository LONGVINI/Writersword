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
        public bool IsGlobal { get; init; }
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

        // Идёт ли правка имени этой палитры (показывается поле ввода).
        public bool IsRenaming { get; init; }

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Palette.Name) ? "—" : Palette.Name;

        // Видимость в быстром попапе (для биндинга двух иконок глазика).
        public bool Shown => Palette.Visible;
        public bool Hidden => !Palette.Visible;

        // Лента-метка: первые 5 цветов палитры по очереди.
        public IReadOnlyList<string> Preview => Palette.Colors.Take(5).ToList();
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

        // Модальное окно управления палитрами (порядок/видимость/имена).
        private Border? _modal;
        private bool _modalOpen;
        private IDisposable? _layerBoundsSub;
        private List<(ColorPalette p, bool global, bool visible, string name)>? _modalSnapshot;

        // Id палитры, у которой сейчас правится имя (поле ввода в строке).
        private string? _renamingId;

        // Состояние перетаскивания свотча «Стандартных цветов».
        private bool _stdPressed, _stdDragging;
        private Point _stdPressPos;
        private string? _stdDragHex;
        private int _stdDragIndex = -1;

        // Состояние перетаскивания свотча выбранной палитры.
        private bool _palPressed, _palDragging;
        private Point _palPressPos;
        private string? _palDragHex;
        private int _palDragIndex = -1;

        public PaletteManagerView()
        {
            InitializeComponent();

            // Модалку отвязываем от обычной раскладки: при открытии она переносится
            // в OverlayLayer окна, чтобы лежать поверх всего как отдельное окно.
            _modal = this.FindControl<Border>("OrderModalRoot");
            var host = this.FindControl<Panel>("RootHost");
            if (_modal is not null && host is not null) host.Children.Remove(_modal);
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

            Palettes.Move(idx, target);
            if (_modalOpen) ApplyOrderToSources(); else PersistOrder();
        }

        // Кнопка-карандаш: включает правку имени этой палитры (и делает её активной).
        private void OnRenamePalette(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is PaletteListItem item)
            {
                _selected = item;
                _renamingId = item.Palette.Id;
                RebuildPalettes();
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
            if (sender is TextBox tb && tb.DataContext is PaletteListItem item)
            {
                item.Palette.Name = (tb.Text ?? string.Empty).Trim();
                if (item.IsGlobal) SaveGlobal(); else SaveProjectDoc();
            }
            _renamingId = null;
            RebuildPalettes();
        }

        /// <summary>Перезагрузить данные из хранилищ (глобального и проектного).</summary>
        public void Refresh()
        {
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

        private void OnStandardPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border b || b.DataContext is not string hex) return;

            if (e.GetCurrentPoint(b).Properties.IsRightButtonPressed)
            {
                _global.StandardColors.RemoveAll(c => Norm(c) == Norm(hex));
                SaveGlobal();
                RebuildStandard();
                e.Handled = true;
                return;
            }

            _stdPressed = true;
            _stdDragging = false;
            _stdDragHex = hex;
            _stdDragIndex = Standard.IndexOf(hex);
            _stdPressPos = e.GetPosition(this);
            e.Pointer.Capture(b);
            e.Handled = true;
        }

        private void OnStandardMoved(object? sender, PointerEventArgs e)
        {
            if (!_stdPressed) return;

            var cur = e.GetPosition(this);
            if (!_stdDragging)
            {
                double dx = cur.X - _stdPressPos.X;
                double dy = cur.Y - _stdPressPos.Y;
                if (dx * dx + dy * dy < 25) return;
                _stdDragging = true;
            }

            var items = this.FindControl<ItemsControl>("StandardItems");
            if (items is null) return;

            int target = TargetRowAt(items, e.GetPosition(items));
            if (target >= 0 && _stdDragIndex >= 0 && target != _stdDragIndex)
            {
                Standard.Move(_stdDragIndex, target);
                _stdDragIndex = target;
            }
        }

        private void OnStandardReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_stdPressed) return;
            _stdPressed = false;
            e.Pointer.Capture(null);

            if (!_stdDragging)
            {
                if (_stdDragHex is string h) ColorPicked?.Invoke(h);
            }
            else
            {
                _global.StandardColors.Clear();
                foreach (var c in Standard) _global.StandardColors.Add(Norm(c));
                SaveGlobal();
            }

            _stdDragging = false;
            _stdDragIndex = -1;
            _stdDragHex = null;
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

        private void RebuildPalettes()
        {
            var prevId = _selected?.Palette.Id;

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
                Palettes.Add(new PaletteListItem { Palette = p, IsGlobal = g, IsActive = p.Id == activeId, IsRenaming = p.Id == _renamingId });

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

            if (_selected?.Palette.Id == item.Palette.Id) _selected = null;
            RebuildPalettes();
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
            _selected = new PaletteListItem { Palette = p, IsGlobal = toGlobal };
            RebuildPalettes();
        }

        // Переключение видимости палитры в быстром попапе (глазик).
        private void OnToggleVisible(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is PaletteListItem item)
            {
                item.Palette.Visible = !item.Palette.Visible;
                if (!_modalOpen) { if (item.IsGlobal) SaveGlobal(); else SaveProjectDoc(); }
                RebuildPalettes();
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
        }

        private void OnPaletteRowMoved(object? sender, PointerEventArgs e)
        {
            if (!_rowPressed || !_rowDragging || _rowCard is null) return;

            var items = (sender as Visual)?.FindAncestorOfType<ItemsControl>();
            if (items is null) return;

            // Призрак едет за курсором по вертикали.
            double dy = e.GetPosition(items).Y - _rowStartItemsY;
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
            _selected.Palette.Colors.Add(hex);
            CurrentColors.Add(hex);
            PersistSelected();
            RebuildPalettes();
        }

        // Имя активной палитры (для подписи закреплённой плашки).
        public string ActivePaletteName => _selected?.Palette.Name ?? string.Empty;

        // Есть ли активная палитра (плашка показывается только когда есть).
        public bool HasActivePalette => _selected is not null;

        // Срабатывает при смене активной палитры — редактор обновляет плашку.
        public event Action? ActiveChanged;

        private void OnPaletteColorPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_selected is null || sender is not Border b || b.DataContext is not string hex) return;

            if (e.GetCurrentPoint(b).Properties.IsRightButtonPressed)
            {
                _selected.Palette.Colors.RemoveAll(c => Norm(c) == Norm(hex));
                for (int i = CurrentColors.Count - 1; i >= 0; i--)
                    if (Norm(CurrentColors[i]) == Norm(hex)) CurrentColors.RemoveAt(i);
                PersistSelected();
                RebuildPalettes();
                e.Handled = true;
                return;
            }

            _palPressed = true;
            _palDragging = false;
            _palDragHex = hex;
            _palDragIndex = CurrentColors.IndexOf(hex);
            _palPressPos = e.GetPosition(this);
            e.Pointer.Capture(b);
            e.Handled = true;
        }

        private void OnPaletteColorMoved(object? sender, PointerEventArgs e)
        {
            if (!_palPressed) return;

            var cur = e.GetPosition(this);
            if (!_palDragging)
            {
                double dx = cur.X - _palPressPos.X;
                double dy = cur.Y - _palPressPos.Y;
                if (dx * dx + dy * dy < 25) return;
                _palDragging = true;
            }

            var items = this.FindControl<ItemsControl>("PaletteColorsItems");
            if (items is null) return;

            int target = TargetRowAt(items, e.GetPosition(items));
            if (target >= 0 && _palDragIndex >= 0 && target != _palDragIndex)
            {
                CurrentColors.Move(_palDragIndex, target);
                _palDragIndex = target;
            }
        }

        private void OnPaletteColorReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_palPressed) return;
            _palPressed = false;
            e.Pointer.Capture(null);

            if (!_palDragging)
            {
                if (_palDragHex is string h) ColorPicked?.Invoke(h);
            }
            else if (_selected is not null)
            {
                _selected.Palette.Colors.Clear();
                foreach (var c in CurrentColors) _selected.Palette.Colors.Add(Norm(c));
                PersistSelected();
                RebuildPalettes();
            }

            _palDragging = false;
            _palDragIndex = -1;
            _palDragHex = null;
        }

        private void OnRemovePaletteColor(object? sender, RoutedEventArgs e)
        {
            if (_selected is null || sender is not Control c || c.DataContext is not string hex) return;
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
