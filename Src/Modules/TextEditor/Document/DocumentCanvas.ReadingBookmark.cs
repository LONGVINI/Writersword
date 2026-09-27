using System;
using Avalonia.Threading;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Закладка чтения: книга открывается там, где её оставили, и после перезапуска.
    ///
    /// Канвас сам ничего не хранит. Снаружи ему дают место, запомненное с прошлого
    /// раза, — оно применяется при первом входе в книгу вместо каретки, — и слушают,
    /// куда книгу перелистнули, чтобы запомнить новое место.
    ///
    /// Место — абзац документа и строка внутри него, с которой начинается открытый
    /// разворот. Номер страницы для этого не годится: он меняется от кегля, формата
    /// листа и подачи.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>
        /// Книгу перелистнули: абзац документа и строка внутри него, с которых
        /// начинается открытый разворот.
        /// </summary>
        public Action<int, int>? ReadingPositionChanged { get; set; }

        // Место, где книгу оставили в прошлый раз. -1 — применять нечего.
        private int _bookmarkPara = -1;
        private int _bookmarkLine;

        // Сколько раз закладку не удалось найти в раскладке. Пока раскладка
        // достраивается, абзаца в ней может ещё не быть; бесконечно ждать нельзя —
        // книга так и стояла бы на первой странице, не слушая и каретку.
        private int _bookmarkMisses;
        private const int BookmarkMaxMisses = 5;

        private bool _readingPositionReportQueued;

        /// <summary>
        /// Место, с которого открыть книгу при первом входе в неё. Применяется один
        /// раз; дальше книга, как и прежде, открывается у каретки, а каретка сама
        /// ходит за листами.
        /// </summary>
        public void SetReadingBookmark(int paragraphIndex, int line)
        {
            _bookmarkPara = paragraphIndex < 0 ? -1 : paragraphIndex;
            _bookmarkLine = Math.Max(0, line);
            _bookmarkMisses = 0;
        }

        /// <summary>
        /// Ищет страницу закладки в текущей раскладке. true — найдена, закладка
        /// израсходована. false и pending — абзаца в раскладке пока нет, стоит
        /// подождать следующей пересборки. false без pending — закладки нет или она
        /// указывает за конец документа.
        /// </summary>
        private bool TryResolveReadingBookmark(out int pageIdx, out bool pending)
        {
            pageIdx = -1;
            pending = false;

            if (_bookmarkPara < 0 || DocVm is null) return false;

            if (_bookmarkPara >= DocVm.Paragraphs.Count)
            {
                _bookmarkPara = -1;
                return false;
            }

            var target = DocVm.Paragraphs[_bookmarkPara];

            // Абзац может разойтись на несколько страниц: берётся тот его кусок, в
            // котором лежит запомненная строка, — последний, что начинается не позже неё.
            int best = -1;
            for (int i = 0; i < _layouts.Count; i++)
            {
                var pl = _layouts[i];
                if (pl.Vm != target || pl.Cell is not null) continue;
                if (best < 0 || pl.LineFrom <= _bookmarkLine) best = i;
            }

            if (best < 0)
            {
                if (++_bookmarkMisses > BookmarkMaxMisses)
                {
                    _bookmarkPara = -1;
                    return false;
                }

                pending = true;
                return false;
            }

            pageIdx = _layouts[best].PageIndex;
            _bookmarkPara = -1;
            return true;
        }

        /// <summary>
        /// Разворот сменился — место в книге сообщается наружу. Не сразу, а в простое:
        /// разворот меняется и посреди пересборки раскладки, а сообщать нужно уже
        /// о странице готовой вёрстки. Несколько смен подряд дают одно сообщение.
        /// </summary>
        private void ScheduleReadingPositionReport()
        {
            if (_readingPositionReportQueued) return;

            _readingPositionReportQueued = true;
            Dispatcher.UIThread.Post(ReportReadingPosition, DispatcherPriority.Background);
        }

        private void ReportReadingPosition()
        {
            _readingPositionReportQueued = false;

            if (!SpreadMode || DocVm is null || _layouts.Count == 0) return;

            // Закладка с прошлого раза ещё не применена — перезаписывать её местом,
            // на котором книга стоит до применения, нельзя.
            if (_bookmarkPara >= 0) return;

            // Первый обычный абзац разворота. Страница может начинаться таблицей —
            // тогда берётся следующая страница разворота.
            int left = _spreadLeftPage;
            int last = left + SpreadStep - 1;

            for (int page = left; page <= last; page++)
            {
                for (int i = 0; i < _layouts.Count; i++)
                {
                    var pl = _layouts[i];
                    if (pl.PageIndex != page || pl.Cell is not null) continue;

                    int docIdx = DocVm.Paragraphs.IndexOf(pl.Vm);
                    if (docIdx < 0) continue;

                    ReadingPositionChanged?.Invoke(docIdx, pl.LineFrom);
                    return;
                }
            }
        }
    }
}
