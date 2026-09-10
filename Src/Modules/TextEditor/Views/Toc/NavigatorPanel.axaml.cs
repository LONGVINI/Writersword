using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Writersword.Modules.TextEditor.ViewModels.Toc;

namespace Writersword.Modules.TextEditor.Views.Toc
{
    /// <summary>
    /// Панель навигатора: дерево заголовков рукописи слева от листов.
    ///
    /// Переход делается одним щелчком. Ctrl-щелчок, которым в Word ходят по оглавлению,
    /// существует там потому, что оглавление лежит внутри текста и его же правят: без
    /// оговорки щелчок не смог бы поставить каретку в строку. Панель текстом не является,
    /// править в ней нечего, и требовать оговорку значило бы усложнить единственное
    /// действие, ради которого её открывают.
    ///
    /// Ходит переход по щелчку и по Enter, а не по смене выделения. Разница видна с
    /// клавиатуры: стрелками по дереву можно осмотреть структуру книги, не сдвинув при
    /// этом рукопись. На выделении переход уводил бы документ на каждое нажатие стрелки.
    /// </summary>
    public partial class NavigatorPanel : UserControl
    {
        // Порядок глубины показа по кнопке: главы, три уровня, всё. Девять уровней в
        // рукописи не встречаются, а перебирать их по одному дольше, чем нужно.
        private static readonly int[] LevelSteps = { 1, 3, 9 };

        public NavigatorPanel()
        {
            InitializeComponent();

            var tree = this.FindControl<TreeView>("HeadingsTree");
            if (tree is not null)
            {
                tree.AddHandler(InputElement.PointerReleasedEvent, OnTreePointerReleased,
                    RoutingStrategies.Tunnel);
                tree.KeyDown += OnTreeKeyDown;
            }

            var closeBtn = this.FindControl<Button>("CloseBtn");
            if (closeBtn is not null)
                closeBtn.Click += OnCloseClick;

            var collapseBtn = this.FindControl<Button>("CollapseBtn");
            if (collapseBtn is not null)
                collapseBtn.Click += OnCollapseClick;

            var levelsBtn = this.FindControl<Button>("LevelsBtn");
            if (levelsBtn is not null)
                levelsBtn.Click += OnLevelsClick;

            DataContextChanged += OnDataContextChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>Человек закрыл панель крестиком.</summary>
        public event EventHandler? CloseRequested;

        private NavigatorViewModel? Vm => DataContext as NavigatorViewModel;

        private void OnDataContextChanged(object? sender, EventArgs e) => UpdateLevelsText();

        // ── Переход ───────────────────────────────────────────────────────

        private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;

            // Стрелка раскрытия ветки к переходу отношения не имеет: свернуть главу и
            // уйти в неё — разные намерения, и делать их одним щелчком нельзя.
            if (e.Source is Visual visual && IsInsideExpander(visual)) return;

            var node = NodeOf(e.Source as Visual);
            if (node is null) return;

            Vm?.GoTo(node);
        }

        private void OnTreeKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender is not TreeView tree) return;
            if (tree.SelectedItem is not NavigatorNode node) return;

            Vm?.GoTo(node);
            e.Handled = true;
        }

        /// <summary>Узел дерева, которому принадлежит элемент под указателем.</summary>
        private static NavigatorNode? NodeOf(Visual? source)
        {
            for (var v = source; v is not null; v = v.GetVisualParent())
            {
                if (v is TreeViewItem item) return item.DataContext as NavigatorNode;
                if (v is Control c && c.DataContext is NavigatorNode node) return node;
            }

            return null;
        }

        /// <summary>Щелчок пришёлся на стрелку раскрытия ветки.</summary>
        private static bool IsInsideExpander(Visual source)
        {
            for (var v = source; v is not null; v = v.GetVisualParent())
            {
                if (v is TreeViewItem) return false;
                if (v is ToggleButton) return true;
            }

            return false;
        }

        // ── Кнопки шапки ──────────────────────────────────────────────────

        private void OnCloseClick(object? sender, RoutedEventArgs e)
            => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void OnCollapseClick(object? sender, RoutedEventArgs e)
        {
            var vm = Vm;
            if (vm is null) return;

            // Одна кнопка на оба действия: если хоть что-то раскрыто — сворачиваем всё,
            // иначе разворачиваем. Две кнопки заняли бы место, а нужна всегда та, что
            // меняет текущее состояние.
            bool anyExpanded = false;
            foreach (var root in vm.Roots)
                if (root.IsExpanded && root.Children.Count > 0) { anyExpanded = true; break; }

            if (anyExpanded) vm.CollapseAll();
            else vm.ExpandAll();
        }

        private void OnLevelsClick(object? sender, RoutedEventArgs e)
        {
            var vm = Vm;
            if (vm is null) return;

            int current = vm.MaxVisibleLevel;
            int next = LevelSteps[0];

            for (int i = 0; i < LevelSteps.Length; i++)
            {
                if (LevelSteps[i] != current) continue;
                next = LevelSteps[(i + 1) % LevelSteps.Length];
                break;
            }

            vm.MaxVisibleLevel = next;
            UpdateLevelsText();
        }

        private void UpdateLevelsText()
        {
            var text = this.FindControl<TextBlock>("LevelsText");
            if (text is null) return;

            int level = Vm?.MaxVisibleLevel ?? 3;
            text.Text = level <= 1 ? "1" : "1-" + level.ToString();
        }
    }
}
