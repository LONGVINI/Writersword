using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Writersword.Modules.TextEditor.Document;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Services;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.Views.Shared;

namespace Writersword.Modules.TextEditor.Views
{
    /// <summary>
    /// Правка колонтитулов на листе, как в Word: поле ввода без фона лежит точно на
    /// полосе колонтитула, тем же шрифтом и цветом, а полотно в это время приглушает
    /// текст документа и рисует границы колонтитулов с ярлыками.
    ///
    /// Просьбу открыть поле присылает вью-модель документа — после двойного щелчка по
    /// полю листа, щелчка по колонтитулу в режиме колонтитулов или кнопки ленты. Вид
    /// ставит поле на полосу, держит его там при прокрутке и смене масштаба и передаёт
    /// принятый текст обратно вью-модели. Поле живёт, пока открыт режим колонтитулов.
    /// </summary>
    public partial class TextEditorView
    {
        private DocumentCanvas? _hfCanvas;
        private ScrollViewer? _hfScroll;
        private Canvas? _hfLayer;
        private HeaderFooterBandEditor? _hfEditor;
        private DocumentViewModel? _hfDoc;

        private int _hfPage;
        private bool _hfHeader;
        private int? _hfMismatchNumber;

        // Своя правка идёт в документ: извещение о смене колонтитулов от неё поле не перечитывает.
        private bool _hfApplying;

        // Поле открыто, а лист ещё не разложен (например, только что сменился режим):
        // место полосы спрашивается повторно, пока раскладка не появится.
        private int _hfPositionAttempts;
        private bool _hfRetryScheduled;
        private const int HeaderFooterPositionAttemptsMax = 30;

        // Лист нужно показать: полосу, стоящую вне окна, прокручиваем в окно один раз
        // при открытии, дальше поле просто следует за листом.
        private bool _hfEnsureVisible;

        private void WireHeaderFooterEditor()
        {
            _hfCanvas = this.FindControl<DocumentCanvas>("PageCanvas");
            _hfScroll = this.FindControl<ScrollViewer>("DocumentScrollViewer");
            _hfLayer = this.FindControl<Canvas>("HeaderFooterEditLayer");
            _hfEditor = this.FindControl<HeaderFooterBandEditor>("HeaderFooterEditor");

            if (_hfCanvas is null || _hfScroll is null || _hfLayer is null || _hfEditor is null)
            {
                _logger.Warning("Header/footer editor parts not found");
                return;
            }

            _hfEditor.Committed += OnHeaderFooterEditorCommitted;
            _hfEditor.ExitRequested += OnHeaderFooterExitRequested;
            _hfEditor.ContinueCountingChosen += OnHeaderFooterContinueCounting;
            _hfEditor.KeepSingleChosen += OnHeaderFooterKeepSingle;
            _hfEditor.UndoRequested += () => _hfCanvas?.ExecuteUndo();
            _hfEditor.RedoRequested += () => _hfCanvas?.ExecuteRedo();
            _hfEditor.SizeChanged += (_, _) => PositionHeaderFooterEditor();

            _hfScroll.ScrollChanged += (_, _) => PositionHeaderFooterEditor();
            _hfCanvas.LayoutUpdated += (_, _) => PositionHeaderFooterEditor();
            _hfLayer.SizeChanged += (_, _) => PositionHeaderFooterEditor();

            _hfCanvas.DataContextChanged += (_, _) => SubscribeHeaderFooterDocument(_hfCanvas.DataContext as DocumentViewModel);
            SubscribeHeaderFooterDocument(_hfCanvas.DataContext as DocumentViewModel);

            // Esc выходит из колонтитулов, где бы ни стояла каретка. В поле колонтитула
            // его разбирает само поле; здесь — когда фокус на листе или на ленте.
            AddHandler(KeyDownEvent, OnHeaderFooterViewKeyDown, RoutingStrategies.Tunnel);

            // Вопрос о счёте ничего не держит: любой щелчок мимо него — «только эта
            // страница», и работа идёт дальше.
            AddHandler(PointerPressedEvent, OnHeaderFooterViewPointerPressed, RoutingStrategies.Tunnel, true);
        }

        private void OnHeaderFooterViewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
            if (_hfDoc is not { IsHeaderFooterMode: true }) return;
            if (_hfEditor is { HasSlotFocus: true }) return;

            _hfDoc.ExitHeaderFooterMode();
            e.Handled = true;
        }

        private void OnHeaderFooterViewPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_hfEditor is not { IsAsking: true }) return;
            if (_hfEditor.IsInsideAsk(e.Source as Visual)) return;

            OnHeaderFooterKeepSingle();
        }

        // ── Позиции колонтитула на линейке ────────────────────────────────

        private Writersword.Modules.TextEditor.ViewModels.Components.RulerViewModel? _hfRuler;

        /// <summary>Линейка модуля: позиции колонтитула, сдвинутые на ней, уходят в документ.</summary>
        private void SubscribeHeaderFooterRuler()
        {
            var ruler = (DataContext as TextEditorViewModel)?.Ruler;
            if (ReferenceEquals(_hfRuler, ruler)) return;

            if (_hfRuler is not null)
                _hfRuler.HeaderFooterTabsChanged -= OnRulerHeaderFooterTabsChanged;

            _hfRuler = ruler;

            if (_hfRuler is not null)
                _hfRuler.HeaderFooterTabsChanged += OnRulerHeaderFooterTabsChanged;
        }

        private void OnRulerHeaderFooterTabsChanged(double centerMm, double rightMm)
            => _hfDoc?.SetHeaderFooterTabs(centerMm, rightMm);

        /// <summary>
        /// Середина среднего места, правый край правого и ширина текста, мм от левого
        /// края текста — как они стоят сейчас.
        /// </summary>
        private (double CenterMm, double RightMm, double WidthMm) HeaderFooterTabsMm()
        {
            double width = Math.Max(_hfDoc?.PageSettings.GetTextWidthMm() ?? 160.0, 1.0);
            var settings = _hfDoc?.HeaderFooter;
            double center = settings?.CenterTabMm ?? width / 2.0;
            double right = settings?.RightTabMm ?? width;
            return (center, right, width);
        }

        /// <summary>Пока пишут в колонтитуле, линейка показывает его позиции — как в Word.</summary>
        private void ShowHeaderFooterRulerTabs()
        {
            SubscribeHeaderFooterRuler();
            var (center, right, width) = HeaderFooterTabsMm();
            _hfRuler?.ShowHeaderFooterTabs(width, center, right);
        }

        private void SubscribeHeaderFooterDocument(DocumentViewModel? doc)
        {
            if (ReferenceEquals(_hfDoc, doc)) return;

            if (_hfDoc is not null)
            {
                _hfDoc.HeaderFooterEditRequested -= OnHeaderFooterEditRequested;
                _hfDoc.HeaderFooterModeChanged -= OnHeaderFooterModeChangedForEditor;
                _hfDoc.HeaderFooterChanged -= OnHeaderFooterChangedForEditor;
                _hfDoc.HeaderFooterFieldInsertRequested -= OnHeaderFooterFieldInsertRequested;
                _hfDoc.HeaderFooterBandEditEndRequested -= OnHeaderFooterBandEditEndRequested;
            }

            // Документ сменился — правка прежнего листа теряет смысл.
            _hfEditor?.Hide();
            _hfCanvas?.ClearHeaderFooterEditBand();
            _hfMismatchNumber = null;

            _hfDoc = doc;

            if (_hfDoc is not null)
            {
                _hfDoc.HeaderFooterEditRequested += OnHeaderFooterEditRequested;
                _hfDoc.HeaderFooterModeChanged += OnHeaderFooterModeChangedForEditor;
                _hfDoc.HeaderFooterChanged += OnHeaderFooterChangedForEditor;
                _hfDoc.HeaderFooterFieldInsertRequested += OnHeaderFooterFieldInsertRequested;
                _hfDoc.HeaderFooterBandEditEndRequested += OnHeaderFooterBandEditEndRequested;
            }
        }

        private void OnHeaderFooterEditRequested(int page, bool header, int slot)
        {
            if (_hfEditor is null || _hfDoc is null) return;

            page = Math.Max(page, 0);

            // Та же полоса уже открыта — каретка просто переходит в нужное место.
            if (_hfEditor.IsEditing && page == _hfPage && header == _hfHeader)
            {
                _hfEditor.FocusSlot(slot, true);
                return;
            }

            // Открыта другая полоса — её правка принимается, прежде чем открыть новую.
            if (_hfEditor.IsEditing) _hfEditor.Commit();

            _hfPage = page;
            _hfHeader = header;
            _hfMismatchNumber = null;

            OpenHeaderFooterEditor(slot);
        }

        private void OpenHeaderFooterEditor(int slot)
        {
            if (_hfEditor is null || _hfDoc is null) return;

            var texts = _hfDoc.GetHeaderFooterEditTexts(_hfPage, _hfHeader);
            _hfEditor.Open(texts, _hfHeader, slot);
            _hfCanvas?.SetHeaderFooterEditBand(_hfPage, _hfHeader);
            ShowHeaderFooterRulerTabs();

            _hfPositionAttempts = 0;
            _hfEnsureVisible = true;
            PositionHeaderFooterEditor();
        }

        private void OnHeaderFooterModeChangedForEditor()
        {
            if (_hfEditor is null || _hfDoc is null) return;
            if (_hfDoc.IsHeaderFooterMode) return;

            // Работу с колонтитулами закрыли — начатая правка принимается.
            if (_hfEditor.IsEditing) _hfEditor.Commit();
            CloseHeaderFooterEditor();
        }

        private void OnHeaderFooterChangedForEditor()
        {
            if (_hfApplying || _hfEditor is null || _hfDoc is null) return;
            if (!_hfEditor.IsEditing) return;

            // Колонтитулы поменяли лентой или отменой, пока поле открыто: поле
            // показывает то, что теперь стоит на листе. Своя правка к этому моменту
            // уже принята — фокус ушёл на ленту раньше, чем сработала её кнопка.
            Dispatcher.UIThread.Post(() =>
            {
                if (_hfEditor is not { IsEditing: true } || _hfDoc is null) return;
                _hfEditor.Load(_hfDoc.GetHeaderFooterEditTexts(_hfPage, _hfHeader));
                ShowHeaderFooterRulerTabs();
                PositionHeaderFooterEditor();
            }, DispatcherPriority.Background);
        }

        private void OnHeaderFooterFieldInsertRequested(string token)
        {
            if (_hfEditor is null || _hfDoc is null) return;

            if (_hfEditor.IsEditing)
            {
                _hfEditor.InsertToken(token);
                return;
            }

            // Поле не открыто: поле встаёт в середину нижнего колонтитула листа каретки.
            _hfDoc.RequestHeaderFooterEdit(false);
            if (_hfEditor.IsEditing)
            {
                _hfEditor.FocusSlot(1, true);
                Dispatcher.UIThread.Post(() => _hfEditor?.InsertToken(token), DispatcherPriority.Background);
            }
        }

        private void OnHeaderFooterEditorCommitted(string[] texts)
        {
            if (_hfEditor is null) return;

            var doc = _hfDoc;
            if (doc is null)
            {
                CloseHeaderFooterEditor();
                return;
            }

            HeaderFooterEditResult? result;
            _hfApplying = true;
            try
            {
                result = doc.ApplyHeaderFooterBandEdit(_hfPage, _hfHeader, texts);
            }
            finally
            {
                _hfApplying = false;
            }

            // Поле показывает то, что теперь стоит на листе: числа полей могли смениться.
            if (!_hfEditor.HasSlotFocus)
                _hfEditor.Load(doc.GetHeaderFooterEditTexts(_hfPage, _hfHeader));

            if (result is { MismatchNumber: int number })
            {
                string typed = number.ToString(CultureInfo.CurrentCulture);
                foreach (var rule in result.AddedRules)
                {
                    if (rule.Action == PageRuleAction.ManualNumber && rule.ManualText is not null)
                        typed = rule.ManualText;
                }

                string expected = result.ExpectedNumberText ?? string.Empty;

                _hfMismatchNumber = number;
                _hfEditor.Ask(expected.Length > 0
                    ? "Здесь набрано «" + typed + "», а по счёту должно быть «" + expected + "»."
                    : "Здесь набрано «" + typed + "».");
            }
            else
            {
                _hfMismatchNumber = null;
                _hfEditor.HideAsk();
            }

            _hfPositionAttempts = 0;
            PositionHeaderFooterEditor();
        }

        private void OnHeaderFooterContinueCounting()
        {
            if (_hfDoc is not null && _hfMismatchNumber is int number)
                _hfDoc.ConvertManualNumberToRestart(_hfPage, number);

            _hfMismatchNumber = null;
            _hfEditor?.HideAsk();
            PositionHeaderFooterEditor();
        }

        private void OnHeaderFooterKeepSingle()
        {
            _hfMismatchNumber = null;
            _hfEditor?.HideAsk();
            PositionHeaderFooterEditor();
        }

        private void OnHeaderFooterExitRequested() => _hfDoc?.ExitHeaderFooterMode();

        /// <summary>
        /// Щелчок по тексту документа: набор в колонтитуле закончен, правка принимается,
        /// поле закрывается. Вкладка «Колонтитулы» остаётся открытой.
        /// </summary>
        private void OnHeaderFooterBandEditEndRequested()
        {
            if (_hfEditor is null) return;
            if (_hfEditor.IsEditing) _hfEditor.Commit();
            CloseHeaderFooterEditor();
        }

        private void CloseHeaderFooterEditor()
        {
            _hfMismatchNumber = null;
            _hfCanvas?.ClearHeaderFooterEditBand();
            (DataContext as TextEditorViewModel)?.Ruler.HideHeaderFooterTabs();

            if (_hfEditor is null || !_hfEditor.IsVisible) return;

            bool hadFocus = _hfEditor.HasSlotFocus;
            _hfEditor.Hide();

            // Клавиатура возвращается тексту: иначе горячие клавиши редактора молчат
            // до первого щелчка по листу.
            if (hadFocus) _hfCanvas?.Focus();
        }

        /// <summary>
        /// Ставит поле на полосу листа. Лист ещё не разложен — пробует позже.
        /// </summary>
        private void PositionHeaderFooterEditor()
        {
            if (_hfEditor is null || _hfCanvas is null || _hfLayer is null || _hfScroll is null) return;
            if (!_hfEditor.IsVisible) return;

            var rect = _hfCanvas.GetHeaderFooterBandRectPx(_hfPage, _hfHeader);
            if (rect is not { } r)
            {
                // Повтор один на раз: раскладка полотна зовёт сюда же на каждом проходе,
                // и счёт попыток должен идти по времени, а не по числу проходов.
                if (_hfRetryScheduled) return;

                if (_hfPositionAttempts++ < HeaderFooterPositionAttemptsMax)
                {
                    _hfRetryScheduled = true;
                    var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                    retry.Tick += (_, _) =>
                    {
                        retry.Stop();
                        _hfRetryScheduled = false;
                        PositionHeaderFooterEditor();
                    };
                    retry.Start();
                    return;
                }

                // Листа нет и не будет (черновик, чтение) — правка не открывается.
                CloseHeaderFooterEditor();
                return;
            }

            _hfPositionAttempts = 0;

            var origin = _hfCanvas.TranslatePoint(new Point(r.X, r.Y), _hfLayer);
            if (origin is not { } p) return;

            if (_hfEnsureVisible)
            {
                _hfEnsureVisible = false;

                double viewH = _hfLayer.Bounds.Height;
                if (viewH > 0 && (p.Y < 0 || p.Y + r.Height > viewH))
                {
                    // Полоса вне окна: прокрутка ставит её на треть высоты окна. Поле
                    // встанет на место по извещению о прокрутке.
                    double target = _hfScroll.Offset.Y + p.Y - viewH / 3;
                    double max = Math.Max(_hfScroll.Extent.Height - _hfScroll.Viewport.Height, 0);
                    _hfScroll.Offset = new Vector(_hfScroll.Offset.X, Math.Clamp(target, 0, max));
                    return;
                }
            }

            var settings = _hfDoc?.HeaderFooter;
            double scale = _hfCanvas.PointToPixelScale;
            _hfEditor.SetSlotFont(_hfCanvas.HeaderFooterFontFamilyName,
                (settings?.FontSizePt ?? 10) * scale,
                settings?.IsBold ?? false,
                settings?.IsItalic ?? false);
            _hfEditor.SetInk(_hfCanvas.HeaderFooterInkColor);
            _hfEditor.SetBandSize(r.Width, Math.Max(r.Height, _hfCanvas.HeaderFooterLineHeightPx));

            var (centerMm, rightMm, _) = HeaderFooterTabsMm();
            double pxPerMm = scale * 72.0 / 25.4;
            _hfEditor.SetSlotAnchors(centerMm * pxPerMm, rightMm * pxPerMm);

            Canvas.SetLeft(_hfEditor, p.X);
            Canvas.SetTop(_hfEditor, p.Y - _hfEditor.BandOffsetY);
        }
    }
}
