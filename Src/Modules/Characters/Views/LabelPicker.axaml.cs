using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Views
{
    /// <summary>
    /// Быстрый выбор метки из уже заведённых в проекте.
    ///
    /// Появляется у кнопки «добавить метку» и живёт ровно до выбора. Полный
    /// редактор открывается отсюда последней строкой — и только когда метки
    /// нужного вида ещё нет. Раньше кнопка вела прямиком в редактор, и чтобы
    /// поставить персонажу метку, которая в проекте есть с первой главы,
    /// приходилось заново выбирать ей значок и цвет.
    ///
    /// Список даётся снаружи готовым: окно ничего не знает ни о службе меток,
    /// ни о вьюмоделях вкладок — как и редактор метки рядом.
    /// </summary>
    public partial class LabelPicker : UserControl
    {
        private readonly ObservableCollection<CharacterLabel> _shown = new();
        private IReadOnlyList<CharacterLabel> _all = Array.Empty<CharacterLabel>();

        private Action<CharacterLabel>? _pick;
        private Action? _create;
        private Flyout? _host;

        public LabelPicker()
        {
            InitializeComponent();

            var rows = this.FindControl<ItemsControl>("Rows");
            if (rows != null) rows.ItemsSource = _shown;
        }

        /// <summary>
        /// Показать список у кнопки.
        ///
        /// labels — метки проекта; те, что уже стоят у персонажа, отсеиваются
        /// вызывающим: показывать их значило бы предлагать поставить второй
        /// раз то, что и так стоит.
        /// </summary>
        public static void ShowAt(
            Control anchor,
            IEnumerable<CharacterLabel> labels,
            Action<CharacterLabel> pick,
            Action create)
        {
            if (anchor == null) return;

            var picker = new LabelPicker();
            picker._all = labels?.Where(l => l != null && !string.IsNullOrWhiteSpace(l.Name))
                                 .ToList() ?? new List<CharacterLabel>();
            picker._pick = pick;
            picker._create = create;

            // Всплывающее окно у кнопки, а не оверлей на весь модуль: выбор
            // метки — одно движение, и затемнять ради него всю программу
            // незачем. Flyout, а не голый Popup: он сам знает, к чему
            // прицепиться, сам закрывается по щелчку мимо и по Esc, и берёт
            // оформление из темы приложения.
            var flyout = new Flyout
            {
                Placement = PlacementMode.BottomEdgeAlignedLeft,
                ShowMode = FlyoutShowMode.Standard,
                Content = picker
            };

            picker._host = flyout;
            flyout.ShowAt(anchor);

            picker.Rebuild();

            // Курсор в поиск: с десятком меток он не нужен, с сотней — это
            // единственный способ дойти до нужной, не читая список глазами.
            Dispatcher.UIThread.Post(() =>
            {
                var box = picker.FindControl<TextBox>("SearchBox");
                box?.Focus();
            }, DispatcherPriority.Background);
        }

        /// <summary>Пересобрать показанный список по строке поиска.</summary>
        private void Rebuild()
        {
            var query = this.FindControl<TextBox>("SearchBox")?.Text?.Trim() ?? string.Empty;

            var matched = string.IsNullOrEmpty(query)
                ? _all
                : _all.Where(l => l.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                      .ToList();

            _shown.Clear();
            foreach (var label in matched) _shown.Add(label);

            var empty = this.FindControl<TextBlock>("EmptyHint");
            if (empty != null)
            {
                empty.IsVisible = _shown.Count == 0;
                empty.Text = _all.Count == 0
                    ? "Меток в проекте пока нет"
                    : "Ничего не нашлось";
            }

            // Когда искали и не нашли — строка внизу заводит метку прямо с этим
            // именем: набранное слово и есть то, чего в проекте не хватает.
            var caption = this.FindControl<TextBlock>("CreateCaption");
            if (caption != null)
                caption.Text = _shown.Count == 0 && query.Length > 0
                    ? $"Завести метку «{query}»…"
                    : "Новая метка…";
        }

        private void OnSearchChanged(object? sender, TextChangedEventArgs e) => Rebuild();

        private void OnSearchKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            if (e.Key != Key.Enter) return;
            e.Handled = true;

            // Enter ставит первую в списке, а если список пуст — открывает
            // редактор: и то и другое продолжает набранное, а не отменяет его.
            if (_shown.Count > 0) Pick(_shown[0]);
            else RequestCreate();
        }

        private void OnPickClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Control c && c.DataContext is CharacterLabel label) Pick(label);
        }

        private void OnCreateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            RequestCreate();
        }

        private void Pick(CharacterLabel label)
        {
            var pick = _pick;
            Close();
            pick?.Invoke(label);
        }

        /// <summary>
        /// Открыть полный редактор. Имя из поиска уезжает туда же: если его
        /// набрали и не нашли, метку заводят именно с ним.
        /// </summary>
        private void RequestCreate()
        {
            PendingName = this.FindControl<TextBox>("SearchBox")?.Text?.Trim() ?? string.Empty;

            var create = _create;
            Close();
            create?.Invoke();
        }

        /// <summary>
        /// Имя, набранное в поиске к моменту открытия редактора. Читается
        /// вызывающим сразу в колбэке создания.
        /// </summary>
        public static string PendingName { get; private set; } = string.Empty;

        private void Close()
        {
            var host = _host;
            _host = null;
            host?.Hide();
        }
    }
}
