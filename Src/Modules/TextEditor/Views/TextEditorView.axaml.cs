using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Blocks;
using Writersword.Modules.TextEditor.Views.Document;

namespace Writersword.Modules.TextEditor.Views
{
    public partial class TextEditorView : UserControl
    {
        private bool _isCrossDrag;
        private int _dragStartParagraph = -1;
        private int _dragStartChar = -1;
        private Point _dragStartPoint;

        public TextEditorView()
        {
            InitializeComponent();

            AddHandler(PointerPressedEvent, OnDocumentPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnDocumentPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnDocumentPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnDocumentKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        private TextEditorViewModel? ViewModel => DataContext as TextEditorViewModel;

        // ── Pointer ──────────────────────────────────────────────────────

        private void OnDocumentPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            _dragStartPoint = e.GetPosition(this);
            var (p, c) = HitTest(_dragStartPoint);
            _dragStartParagraph = p;
            _dragStartChar = c;
            _isCrossDrag = false;
        }

        private void OnDocumentPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragStartParagraph < 0) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var (curPar, curChar) = HitTest(e.GetPosition(this));
            if (curPar < 0) return;

            if (curPar == _dragStartParagraph && !_isCrossDrag) return; // TextBox сам

            _isCrossDrag = true;
            e.Handled = true; // только здесь перехватываем

            ApplySelection(_dragStartParagraph, _dragStartChar, curPar, curChar);
        }

        private void OnDocumentPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _dragStartParagraph = -1;
            _dragStartChar = -1;
            // _isCrossDrag не сбрасываем — нужен для Ctrl+C
        }

        // ── Selection ─────────────────────────────────────────────────────

        private void ApplySelection(int fromPar, int fromChar, int toPar, int toChar)
        {
            var views = GetParagraphViews();
            bool goDown = toPar > fromPar || (toPar == fromPar && toChar >= fromChar);
            int minP = Math.Min(fromPar, toPar);
            int maxP = Math.Max(fromPar, toPar);

            for (int i = 0; i < views.Count; i++)
            {
                var box = views[i].FindControl<TextBox>("ParagraphBox");
                if (box is null) continue;
                int len = box.Text?.Length ?? 0;

                if (i < minP || i > maxP)
                {
                    box.SelectionStart = 0;
                    box.SelectionEnd = 0;
                }
                else if (fromPar == toPar)
                {
                    box.SelectionStart = Math.Min(fromChar, toChar);
                    box.SelectionEnd = Math.Max(fromChar, toChar);
                }
                else if (i == fromPar)
                {
                    box.SelectionStart = goDown ? fromChar : 0;
                    box.SelectionEnd = goDown ? len : fromChar;
                }
                else if (i == toPar)
                {
                    box.SelectionStart = goDown ? 0 : toChar;
                    box.SelectionEnd = goDown ? toChar : len;
                }
                else
                {
                    box.SelectionStart = 0;
                    box.SelectionEnd = len;
                }
            }
        }

        private async void OnDocumentKeyDown(object? sender, KeyEventArgs e)
        {
            // Работаем ТОЛЬКО если есть кросс-выделение (несколько абзацев)
            if (!_isCrossDrag) return;

            var views = GetParagraphViews();
            var selectedBoxes = views
                .Select(v => v.FindControl<TextBox>("ParagraphBox"))
                .Where(b => b is not null && b.SelectionEnd > b.SelectionStart)
                .ToList();

            if (selectedBoxes.Count == 0) return;

            if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                string text = string.Join(
                    Environment.NewLine,
                    selectedBoxes.Select(b => b!.Text?.Substring(
                        b.SelectionStart,
                        b.SelectionEnd - b.SelectionStart) ?? ""));

                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(text);
                return;
            }

            if (e.Key == Key.Back || e.Key == Key.Delete)
            {
                e.Handled = true;
                var docVm = ViewModel?.DocumentViewModel;
                if (docVm is null) return;

                // Удаляем выделенный текст в каждом затронутом абзаце
                foreach (var box in selectedBoxes)
                {
                    if (box is null) continue;
                    int start = box.SelectionStart;
                    int end = box.SelectionEnd;
                    if (box.Text is null) continue;
                    box.Text = box.Text.Remove(start, end - start);
                    box.SelectionStart = start;
                    box.SelectionEnd = start;
                }

                // Схлопываем средние пустые абзацы
                for (int i = views.Count - 1; i >= 0; i--)
                {
                    var box = views[i].FindControl<TextBox>("ParagraphBox");
                    if (box is null) continue;
                    if (string.IsNullOrEmpty(box.Text) && docVm.Paragraphs.Count > 1)
                    {
                        var pvm = views[i].DataContext as ParagraphViewModel;
                        if (pvm is not null)
                            docVm.Paragraphs.Remove(pvm);
                    }
                }

                ClearAllSelections();
                return;
            }

            // Любая другая клавиша — снимаем кросс-выделение
            ClearAllSelections();
        }

        private void ClearAllSelections()
        {
            _isCrossDrag = false;
            foreach (var view in GetParagraphViews())
            {
                var box = view.FindControl<TextBox>("ParagraphBox");
                if (box is not null)
                {
                    box.SelectionStart = 0;
                    box.SelectionEnd = 0;
                }
            }
            ViewModel?.DocumentViewModel?.ClearSelection();
        }

        // ── HitTest ───────────────────────────────────────────────────────

        private (int parIdx, int charIdx) HitTest(Point pointInThis)
        {
            var views = GetParagraphViews();

            for (int i = 0; i < views.Count; i++)
            {
                var view = views[i];
                Point local = this.TranslatePoint(pointInThis, view) ?? new Point(-1, -1);
                if (!new Rect(view.Bounds.Size).Contains(local)) continue;

                var box = view.FindControl<TextBox>("ParagraphBox");
                if (box is null) continue;

                var presenter = box.GetVisualDescendants()
                                   .OfType<TextPresenter>()
                                   .FirstOrDefault();
                if (presenter is null) return (i, 0);

                Point pl = this.TranslatePoint(pointInThis, presenter) ?? new Point(0, 0);
                pl = new Point(
                    Math.Clamp(pl.X, 0, Math.Max(presenter.Bounds.Width, 1)),
                    Math.Clamp(pl.Y, 0, Math.Max(presenter.Bounds.Height, 1)));

                int charIdx = presenter.TextLayout is not null
                     ? presenter.TextLayout.HitTestPoint(pl).TextPosition
                     : 0;
                return (i, charIdx);
            }

            if (views.Count > 0)
            {
                var box = views[^1].FindControl<TextBox>("ParagraphBox");
                return (views.Count - 1, box?.Text?.Length ?? 0);
            }

            return (-1, -1);
        }

        private List<EditorParagraphView> GetParagraphViews() =>
            this.GetVisualDescendants()
                .OfType<EditorParagraphView>()
                .ToList();
    }
}