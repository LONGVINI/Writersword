using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Мост между канвасом и системным методом ввода. Нужен там, где текст приходит не от
    /// физической клавиатуры: на Android клавиатура вообще не поднимется, пока фокусный
    /// контрол не отдаст платформе объект-посредник, а обычный OnTextInput не сработает ни
    /// разу — платформа просто не знает, что этот контрол принимает текст.
    ///
    /// Посредник отдаёт платформе четыре вещи: визуал, в котором показан текст, текст вокруг
    /// каретки, прямоугольник каретки и границы выделения. Обратно приходят две операции:
    /// ввод текста (обычным TextInput, его ловит OnTextInput) и установка выделения, за
    /// которой платформа шлёт Delete — так на Android устроено удаление.
    ///
    /// Текстом вокруг каретки служит текущий абзац, а не весь документ: у книги это сотни
    /// тысяч знаков, и пересобирать их на каждое нажатие нельзя. Все смещения, которыми
    /// обменивается посредник, отсчитываются от начала этого абзаца.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        private CanvasInputMethodClient? _imClient;

        // Абзац, о котором методу ввода сообщили в прошлый раз. При переходе на другой абзац
        // текст вокруг каретки меняется целиком, и платформе выдаётся сброс состояния, а не
        // уведомление об изменении: иначе она продолжает считать смещения от старого абзаца.
        private int _imLastPara = -1;

        // Взведён, пока выделение ставит сама платформа. Обратные уведомления в этот момент
        // не отправляются: платформа ещё внутри собственного обновления и приняла бы их за
        // чужую правку, после которой пересчитала бы смещения от неверного состояния.
        private bool _imApplyingPlatformSelection;

        private void AttachInputMethod()
        {
            _imClient = new CanvasInputMethodClient(this);

            AddHandler(
                InputElement.TextInputMethodClientRequestedEvent,
                OnInputMethodClientRequested,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            // Многострочный ввод без подсказок и автозамены. Подсказки на Android означают
            // черновой текст: клавиатура присылает не готовую букву, а подчёркнутый кусок,
            // который потом заменяется целиком. Модель абзаца временного текста не знает,
            // поэтому черновики выключены — вводится сразу окончательный символ.
            TextInputOptions.SetMultiline(this, true);
            TextInputOptions.SetShowSuggestions(this, false);
            TextInputOptions.SetAutoCapitalization(this, false);
        }

        private void OnInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
        {
            if (_imClient is null) return;
            if (IsEditingBlocked) return;

            // Чтение и книжный разворот правку не предусматривают: клавиатура там не нужна
            // и только закрыла бы половину страницы.
            if (ReadingActive || SpreadMode) return;

            e.Client = _imClient;
        }

        /// <summary>
        /// Сообщает методу ввода, что каретка, выделение или текст абзаца изменились.
        /// Вызывается из ResetCaret/ResetCaretNoScroll — через них проходит и правка, и
        /// любое перемещение каретки.
        /// </summary>
        private void NotifyInputMethod()
        {
            var client = _imClient;
            if (client is null) return;
            if (_imApplyingPlatformSelection) return;

            if (_caretPara != _imLastPara)
            {
                _imLastPara = _caretPara;
                client.NotifyParagraphChanged();
            }
            else
            {
                client.NotifyTextChanged();
                client.NotifySelectionChanged();
            }

            client.NotifyCursorMoved();
        }

        /// <summary>
        /// Просит платформу поднять экранную клавиатуру. Вызывается по касанию текста: на
        /// компьютере панели ввода нет и вызов ничего не делает.
        /// </summary>
        private void RequestInputPane()
        {
            if (IsEditingBlocked) return;
            if (ReadingActive || SpreadMode) return;
            _imClient?.RequestInputPane();
        }

        // ── Данные для метода ввода ───────────────────────────────────────

        private string ImeSurroundingText()
            => GetVmAt(_caretPara)?.PlainText ?? string.Empty;

        private int ImeParagraphLength()
            => GetVmAt(_caretPara)?.PlainText?.Length ?? 0;

        private TextSelection ImeGetSelection()
        {
            int len = ImeParagraphLength();
            int caret = Clamp(_caretChar, 0, len);

            // Выделение отдаётся только когда оно целиком лежит в текущем абзаце: смещения
            // метода ввода отсчитываются от начала этого абзаца, и выделение через абзац в
            // них не выражается. Для платформы это выглядит как пустое выделение у каретки —
            // единственный честный ответ, который её не собьёт.
            if (HasSel() && _selStartPara == _caretPara && _selEndPara == _caretPara)
            {
                int a = Clamp(_selStartChar, 0, len);
                int b = Clamp(_selEndChar, 0, len);
                return a <= b ? new TextSelection(a, b) : new TextSelection(b, a);
            }

            return new TextSelection(caret, caret);
        }

        private void ImeSetSelection(TextSelection selection)
        {
            if (IsEditingBlocked) return;

            int len = ImeParagraphLength();
            int start = Clamp(selection.Start, 0, len);
            int end = Clamp(selection.End, 0, len);

            _imApplyingPlatformSelection = true;
            try
            {
                _selStartPara = _caretPara;
                _selStartChar = start;
                _selEndPara = _caretPara;
                _selEndChar = end;

                _caretChar = end;
                _caretLineHint = -1;

                // Выделение ставит платформа, а не пользователь: серия вертикальных перемещений
                // при этом обрывается, иначе следующий Up/Down увёл бы каретку по старому столбцу.
                _vNavActive = false;

                ResetCaretNoScroll();
                InvalidateFull();
            }
            finally
            {
                _imApplyingPlatformSelection = false;
            }
        }

        private Rect ImeCursorRectangle()
        {
            if (!TryGetCaretGeometry(out float xPt, out float lineTopPt, out float lineHeightPt))
                return default;

            // Страницы рядом: геометрия каретки логическая, а метод ввода ждёт координаты
            // канваса — добавляем сдвиг страницы, на которой стоит каретка.
            if (_pagesPerRow > 1 && _caretPara >= 0 && _caretPara < _layouts.Count)
            {
                List<PageRect> pages;
                lock (_renderLock) { pages = _pages; }

                int pageIdx = _layouts[_caretPara].PageIndex;
                if (pageIdx >= 0 && pageIdx < pages.Count)
                {
                    var (dxPt, dyPt) = PageVisualDelta(pageIdx, pages);
                    xPt += dxPt;
                    lineTopPt += dyPt;
                }
            }

            double zoom = Zoom;
            double scale = PtToPx * zoom;

            return new Rect(
                xPt * scale,
                lineTopPt * scale,
                1.0,
                (lineHeightPt > 0.01f ? lineHeightPt : FallbackLinePt) * scale);
        }

        /// <summary>
        /// Посредник, который платформа получает по запросу. Всё состояние живёт в канвасе,
        /// здесь только пересылка: класс существует потому, что платформа требует наследника
        /// TextInputMethodClient, а канвас уже наследник Control.
        /// </summary>
        private sealed class CanvasInputMethodClient : TextInputMethodClient
        {
            private readonly DocumentCanvas _owner;

            public CanvasInputMethodClient(DocumentCanvas owner) => _owner = owner;

            public override Visual TextViewVisual => _owner;

            // Черновой текст не поддерживается намеренно: см. комментарий в AttachInputMethod.
            public override bool SupportsPreedit => false;

            public override bool SupportsSurroundingText => true;

            public override string SurroundingText => _owner.ImeSurroundingText();

            public override Rect CursorRectangle => _owner.ImeCursorRectangle();

            public override TextSelection Selection
            {
                get => _owner.ImeGetSelection();
                set
                {
                    _owner.ImeSetSelection(value);
                    RaiseSelectionChanged();
                }
            }

            internal void NotifyTextChanged() => RaiseSurroundingTextChanged();

            internal void NotifySelectionChanged() => RaiseSelectionChanged();

            internal void NotifyCursorMoved() => RaiseCursorRectangleChanged();

            internal void RequestInputPane() => RaiseInputPaneActivationRequested();

            // Каретка ушла в другой абзац: текст вокруг неё сменился целиком, и платформе
            // нужен полный сброс, а не уведомление об изменении.
            internal void NotifyParagraphChanged()
            {
                RequestReset();
                RaiseSurroundingTextChanged();
                RaiseSelectionChanged();
            }
        }
    }
}
