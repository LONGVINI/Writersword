using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    public class PaletteListItem
    {
        public ColorPalette Palette { get; init; } = new();
        public bool IsGlobal { get; init; }
        public bool IsLocal => !IsGlobal;

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
        public ObservableCollection<string> CurrentColors { get; } = new();

        private GlobalPaletteData _global = new();
        private PaletteListItem? _selected;

        // Состояние перетаскивания строки палитры.
        private bool _rowPressed, _rowDragging;
        private Point _rowPressPos;
        private PaletteListItem? _rowItem;
        private int _rowIndex = -1;

        public PaletteManagerView()
        {
            InitializeComponent();
        }

        /// <summary>Перезагрузить данные из хранилищ (глобального и проектного).</summary>
        public void Refresh()
        {
            LoadGlobal();
            ApplyCollapsed();
            RebuildStandard();
            RebuildPalettes();
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
            }
            else ColorPicked?.Invoke(hex);
            e.Handled = true;
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

            Palettes.Clear();
            var proj = CurrentProject;
            if (proj is not null)
                foreach (var p in proj.ProjectPalettes)
                    Palettes.Add(new PaletteListItem { Palette = p, IsGlobal = false });
            foreach (var p in _global.Palettes)
                Palettes.Add(new PaletteListItem { Palette = p, IsGlobal = true });

            _selected = Palettes.FirstOrDefault(x => x.Palette.Id == prevId) ?? Palettes.FirstOrDefault();
            LoadSelected();
        }

        private void LoadSelected()
        {
            CurrentColors.Clear();
            if (_selected is null) { ShowDetail(false); return; }

            foreach (var c in _selected.Palette.Colors) CurrentColors.Add(Norm(c));

            var nameBox = this.FindControl<TextBox>("PaletteName");
            if (nameBox is not null) nameBox.Text = _selected.Palette.Name;

            ToggleClass(this.FindControl<Button>("ScopeLocalBtn"), "active", !_selected.IsGlobal);
            ToggleClass(this.FindControl<Button>("ScopeGlobalBtn"), "active", _selected.IsGlobal);
            ShowDetail(true);
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
            var p = new ColorPalette { Name = SharedStrings.Palette_New };
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
                if (item.IsGlobal) SaveGlobal(); else SaveProjectDoc();
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
            _rowPressPos = e.GetPosition(this);
            e.Pointer.Capture(c);
        }

        private void OnPaletteRowMoved(object? sender, PointerEventArgs e)
        {
            if (!_rowPressed || _rowItem is null) return;

            var cur = e.GetPosition(this);
            if (!_rowDragging)
            {
                double dx = cur.X - _rowPressPos.X;
                double dy = cur.Y - _rowPressPos.Y;
                if (dx * dx + dy * dy < 25) return;
                _rowDragging = true;
            }

            var items = this.FindControl<ItemsControl>("PaletteList");
            if (items is null) return;

            int target = TargetRowAt(items, e.GetPosition(items));
            if (target < 0 || _rowIndex < 0 || target == _rowIndex) return;

            // Переставляем только внутри своей области (локальные с локальными,
            // глобальные с глобальными) — смена области делается кнопками Local/Global.
            if (Palettes[target].IsGlobal != _rowItem.IsGlobal) return;

            Palettes.Move(_rowIndex, target);
            _rowIndex = target;
        }

        private void OnPaletteRowReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_rowPressed) return;
            _rowPressed = false;
            e.Pointer.Capture(null);

            if (!_rowDragging)
            {
                if (_rowItem is not null)
                {
                    _selected = _rowItem;
                    LoadSelected();
                }
            }
            else
            {
                PersistOrder();
            }

            _rowDragging = false;
            _rowItem = null;
            _rowIndex = -1;
        }

        private static int TargetRowAt(ItemsControl items, Point p)
        {
            foreach (var cont in items.GetRealizedContainers())
                if (cont.Bounds.Contains(p)) return items.IndexFromContainer(cont);
            return -1;
        }

        // Переписывает порядок исходных списков из текущего порядка отображения.
        private void PersistOrder()
        {
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

            SaveGlobal();
            SaveProjectDoc();
        }

        private void OnAddPaletteColor(object? sender, RoutedEventArgs e)
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
            }
            else ColorPicked?.Invoke(hex);
            e.Handled = true;
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

        private static void SaveProjectDoc()
        {
            try
            {
                var tab = CoreServices.GetService<ITabCollection>()?.ActiveTab;
                var wf = CoreServices.GetService<IProjectWorkflow>();
                if (tab is not null && wf is not null) _ = wf.SaveDocumentAsync(tab);
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
