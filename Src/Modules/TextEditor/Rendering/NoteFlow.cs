using System;
using System.Collections.Generic;
using Writersword.Core.Models.Rendering;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.Rendering
{
    /// <summary>Кусок сноски в области сносок листа.</summary>
    /// <param name="Note">Сноска.</param>
    /// <param name="Layout">Раскладка абзаца сноски.</param>
    /// <param name="LineFrom">Первая строка куска.</param>
    /// <param name="LineTo">Строка за последней строкой куска.</param>
    /// <param name="OffsetPt">Верх куска от верха области сносок.</param>
    /// <param name="HeightPt">Высота куска вместе с интервалами абзаца.</param>
    internal readonly record struct NoteAreaPiece(
        NoteBlock Note, SKTextLayout Layout, int LineFrom, int LineTo, float OffsetPt, float HeightPt)
    {
        /// <summary>Верх первой строки куска от верха области сносок.</summary>
        public float TextOffsetPt => OffsetPt + (LineFrom == 0 ? Layout.SpaceBeforePt : 0f);
    }

    /// <summary>
    /// Область сносок одного листа: строка с чертой и под ней сноски в порядке знаков.
    /// Прижимается к нижнему полю листа.
    /// </summary>
    /// <param name="PageIndex">Лист.</param>
    /// <param name="HeightPt">Высота области вместе со строкой черты.</param>
    /// <param name="RuleBandPt">Высота строки с чертой — она стоит в области первой.</param>
    /// <param name="Continued">
    /// Область открывается продолжением сноски с прошлого листа: черта над ней идёт
    /// во всю ширину текста.
    /// </param>
    /// <param name="Pieces">Куски сносок сверху вниз.</param>
    internal sealed record NoteArea(
        int PageIndex, float HeightPt, float RuleBandPt, bool Continued, IReadOnlyList<NoteAreaPiece> Pieces);

    /// <summary>
    /// Счёт сносок одного прохода раскладки страниц: какие сноски на каком листе стоят,
    /// сколько места они у листа отняли и что продолжается на следующем.
    ///
    /// Правила — как у Word. Строка со знаком сноски остаётся на листе, только если
    /// вместе с ней помещается начало её сноски. Сноска, не поместившаяся целиком,
    /// продолжается внизу следующего листа. Запрет висячих строк действует и в
    /// сносках: абзац сноски короче четырёх строк между листами не делится, у более
    /// длинного на каждом листе остаётся не меньше двух строк.
    ///
    /// Одним счётом пользуются и раскладка листов редактора, и раскладка печати:
    /// обе ведут поток строк по листам и спрашивают здесь, сколько места строке
    /// нужно под её сноски.
    /// </summary>
    internal sealed class NoteFlow
    {
        /// <summary>Длина черты над сносками, пт: два дюйма, как у Word.</summary>
        public const float RuleLengthPt = 144f;

        /// <summary>Толщина черты над сносками, пт.</summary>
        public const float RuleThicknessPt = 0.6f;

        /// <summary>
        /// Где в своей строке стоит черта над сносками: доля высоты строки от её верха.
        /// Сверено с Word: черта — знак в строке обычного текста, чуть выше её середины
        /// между базовой линией и верхом строчных.
        /// </summary>
        public const float RulePositionShare = 0.6f;

        /// <summary>
        /// Сколько листа может занять продолжение сноски с прошлого листа: остальное
        /// остаётся тексту, иначе длинная сноска вытеснила бы его с листа целиком.
        /// </summary>
        public const float TailPageShare = 0.8f;

        /// <summary>
        /// Сколько строк абзаца сноски остаётся на каждом листе при её разрыве.
        /// </summary>
        public const int SplitMinLines = 2;

        /// <summary>Кусок сноски на листе до того, как область листа собрана.</summary>
        private readonly record struct Piece(
            NoteBlock Note, SKTextLayout Layout, int LineFrom, int LineTo, float HeightPt, bool Continued);

        /// <summary>Сноска, расставленная не до конца: с какого абзаца и строки продолжать.</summary>
        private sealed class Tail
        {
            public Tail(NoteBlock note, List<SKTextLayout> layouts)
            {
                Note = note;
                Layouts = layouts;
            }

            public NoteBlock Note { get; }
            public List<SKTextLayout> Layouts { get; }
            public int Paragraph;
            public int Line;

            public bool Started => Paragraph > 0 || Line > 0;
            public bool Done => Paragraph >= Layouts.Count;
        }

        // Запас на ошибку округления при сравнении высот.
        private const float FitTolerancePt = 0.01f;

        // Потребность, с которой строка заведомо не помещается на лист.
        private const float NeverFitsPt = 1e9f;

        private readonly NoteNumbering _numbering;
        private readonly Func<NoteReference, List<SKTextLayout>> _layoutsOf;
        private readonly float _ruleBandPt;
        private readonly float _emptyLinePt;

        private readonly Dictionary<Guid, List<SKTextLayout>> _layouts = new();
        private readonly SortedDictionary<int, List<Piece>> _areas = new();
        private readonly List<Tail> _tails = new();
        private readonly HashSet<Guid> _placed = new();
        private int _pageIdx = -1;

        /// <param name="numbering">Номера сносок документа.</param>
        /// <param name="layoutsOf">Раскладки абзацев сноски, по одной на абзац.</param>
        /// <param name="ruleBandPt">Высота строки с чертой над сносками.</param>
        /// <param name="emptyLinePt">Высота абзаца сноски без единой строки.</param>
        public NoteFlow(
            NoteNumbering numbering,
            Func<NoteReference, List<SKTextLayout>> layoutsOf,
            float ruleBandPt,
            float emptyLinePt)
        {
            _numbering = numbering;
            _layoutsOf = layoutsOf;
            _ruleBandPt = ruleBandPt;
            _emptyLinePt = emptyLinePt;
        }

        /// <summary>Сноски документа в порядке знаков.</summary>
        public IReadOnlyList<NoteReference> Footnotes => _numbering.Footnotes;

        /// <summary>Концевые сноски документа в порядке знаков.</summary>
        public IReadOnlyList<NoteReference> Endnotes => _numbering.Endnotes;

        /// <summary>Высота строки с чертой над сносками.</summary>
        public float RuleBandPt => _ruleBandPt;

        /// <summary>Высота абзаца сноски без единой строки.</summary>
        public float EmptyLinePt => _emptyLinePt;

        /// <summary>Есть сноски, продолжение которых ждёт следующего листа.</summary>
        public bool HasTails => _tails.Count > 0;

        /// <summary>В абзаце стоит знак сноски.</summary>
        public bool HasMarksIn(ParagraphBlock paragraph) => _numbering.HasFootnoteMarksIn(paragraph);

        /// <summary>Сноска уже стоит на листе.</summary>
        public bool IsPlaced(Guid noteId) => _placed.Contains(noteId);

        /// <summary>Сноска уже стоит на листе, который этот проход не раскладывает.</summary>
        public void MarkPlaced(Guid noteId) => _placed.Add(noteId);

        /// <summary>Раскладки абзацев сноски (концевой — тоже).</summary>
        public List<SKTextLayout> LayoutsOf(NoteReference reference)
        {
            if (_layouts.TryGetValue(reference.Note.Id, out var layouts)) return layouts;

            layouts = _layoutsOf(reference);
            _layouts[reference.Note.Id] = layouts;
            return layouts;
        }

        /// <summary>Сноски, чьи знаки стоят в строке, в порядке знаков.</summary>
        public IEnumerable<NoteReference> ReferencesIn(SKLineLayout line)
        {
            foreach (var seg in line.Segments)
            {
                if (seg.InlineImageId is not Guid id || seg.IsHidden) continue;
                if (!_numbering.TryGet(id, out var reference)) continue;
                if (reference.Note.Kind != NoteKind.Footnote) continue;

                yield return reference;
            }
        }

        /// <summary>Сколько места на листе уже отдано сноскам вместе с чертой.</summary>
        public float ReservedOn(int pageIdx)
        {
            if (!_areas.TryGetValue(pageIdx, out var pieces) || pieces.Count == 0) return 0f;

            float reservedPt = _ruleBandPt;
            foreach (var piece in pieces) reservedPt += piece.HeightPt;
            return reservedPt;
        }

        /// <summary>
        /// Поток перешёл на новый лист: продолжения сносок прошлого листа встают на
        /// него первыми и отнимают у него место.
        /// </summary>
        /// <param name="pageIdx">Лист, на котором стоит поток.</param>
        /// <param name="pageTopPt">Верх текстовой области листа.</param>
        /// <param name="contentYPt">Где на листе стоит поток.</param>
        /// <param name="pageBottomPt">Низ места под текст: уменьшается на занятое сносками.</param>
        public void EnterPage(int pageIdx, float pageTopPt, float contentYPt, ref float pageBottomPt)
        {
            if (pageIdx == _pageIdx) return;
            _pageIdx = pageIdx;

            if (_tails.Count == 0) return;

            float pageTextPt = Math.Max(0f, pageBottomPt - pageTopPt);
            float availablePt = Math.Min(
                Math.Max(0f, pageBottomPt - contentYPt),
                pageTextPt * TailPageShare);
            float beforePt = availablePt;
            bool atPageTop = contentYPt <= pageTopPt + 0.5f;
            bool placedAny = false;

            while (_tails.Count > 0)
            {
                var tail = _tails[0];
                float tailBeforePt = availablePt;

                // На пустом листе первое продолжение встаёт в любом случае: иначе
                // сноска, не помещающаяся в отведённую долю листа, не кончилась бы.
                Place(tail, pageIdx, ref availablePt, force: atPageTop && !placedAny);
                if (availablePt < tailBeforePt) placedAny = true;

                if (!tail.Done) break;
                _tails.RemoveAt(0);
            }

            pageBottomPt -= beforePt - availablePt;
        }

        /// <summary>
        /// Сколько места внизу листа нужно строке, чтобы остаться на нём: начало первой
        /// её сноски, которой на листах ещё нет, вместе с чертой, если сноска на листе
        /// первая.
        /// </summary>
        public float MinNeed(SKLineLayout line)
        {
            foreach (var reference in ReferencesIn(line))
            {
                if (_placed.Contains(reference.Note.Id)) continue;

                // Прошлая сноска листа не поместилась целиком: область сносок занята,
                // и новой сноске на этом листе начаться негде.
                if (_tails.Count > 0) return NeverFitsPt;

                var layouts = LayoutsOf(reference);
                if (layouts.Count == 0) return 0f;

                float bandPt = ReservedOn(_pageIdx) > 0f ? 0f : _ruleBandPt;
                var first = layouts[0];
                int lines = first.Lines.Count;
                int headLines = Unsplittable(reference.Note, 0, lines) ? lines : SplitMinLines;

                return bandPt + PieceHeight(first, 0, headLines);
            }

            return 0f;
        }

        /// <summary>
        /// Строка встала на лист: её сноски встают внизу этого листа и отнимают у него
        /// место. Сноска, не поместившаяся целиком, продолжается на следующем.
        /// </summary>
        /// <param name="line">Строка.</param>
        /// <param name="pageIdx">Лист строки.</param>
        /// <param name="availablePt">Место между низом строки и низом места под текст.</param>
        /// <param name="pageBottomPt">Низ места под текст: уменьшается на занятое сносками.</param>
        public void Commit(SKLineLayout line, int pageIdx, float availablePt, ref float pageBottomPt)
        {
            bool first = true;

            foreach (var reference in ReferencesIn(line))
            {
                if (!_placed.Add(reference.Note.Id)) continue;

                var tail = new Tail(reference.Note, LayoutsOf(reference));
                if (tail.Done) continue;

                if (_tails.Count > 0)
                {
                    _tails.Add(tail);
                    continue;
                }

                float beforePt = availablePt;

                // Начало первой сноски строки встаёт на лист вместе со строкой: место
                // под него проверено до того, как строка осталась на листе.
                Place(tail, pageIdx, ref availablePt, force: first);
                first = false;

                pageBottomPt -= beforePt - availablePt;
                if (!tail.Done) _tails.Add(tail);
            }
        }

        /// <summary>
        /// Ставит сноску на заданный лист в свободное место, уменьшая его на занятое.
        /// Что не поместилось — на следующий лист.
        /// </summary>
        public void CommitAt(NoteReference reference, int pageIdx, ref float freePt)
        {
            if (!_placed.Add(reference.Note.Id)) return;

            var tail = new Tail(reference.Note, LayoutsOf(reference));
            if (tail.Done) return;

            if (_tails.Count == 0)
                Place(tail, pageIdx, ref freePt, force: false);

            if (!tail.Done) _tails.Add(tail);
        }

        /// <summary>
        /// Строки абзаца, начиная с заданной, переезжают на следующий лист: их сноски
        /// снимаются с листа и встанут заново там, куда встанут строки.
        /// </summary>
        public void Rollback(SKTextLayout layout, int fromLine)
        {
            for (int li = Math.Max(0, fromLine); li < layout.Lines.Count; li++)
            {
                foreach (var reference in ReferencesIn(layout.Lines[li]))
                {
                    Guid id = reference.Note.Id;
                    if (!_placed.Remove(id)) continue;

                    foreach (var pieces in _areas.Values)
                        pieces.RemoveAll(piece => piece.Note.Id == id);

                    _tails.RemoveAll(tail => tail.Note.Id == id);
                }
            }
        }

        /// <summary>
        /// Области сносок листов по итогам прохода: сверху строка с чертой, под ней
        /// сноски в порядке знаков. Куда область встаёт на листе, решает раскладка.
        /// </summary>
        public List<NoteArea> BuildAreas()
        {
            var areas = new List<NoteArea>(_areas.Count);

            foreach (var area in _areas)
            {
                var pieces = area.Value;
                if (pieces.Count == 0) continue;

                var areaPieces = new List<NoteAreaPiece>(pieces.Count);
                float offsetPt = _ruleBandPt;

                foreach (var piece in pieces)
                {
                    areaPieces.Add(new NoteAreaPiece(
                        piece.Note, piece.Layout, piece.LineFrom, piece.LineTo, offsetPt, piece.HeightPt));
                    offsetPt += piece.HeightPt;
                }

                areas.Add(new NoteArea(area.Key, offsetPt, _ruleBandPt, pieces[0].Continued, areaPieces));
            }

            return areas;
        }

        /// <summary>
        /// Абзац сноски, который между листами не делится: короче четырёх строк (запрет
        /// висячих строк не оставляет, где резать) или помечен «не разрывать».
        /// </summary>
        private static bool Unsplittable(NoteBlock note, int paragraph, int lines)
            => lines < SplitMinLines * 2
               || (paragraph < note.Paragraphs.Count
                   && note.Paragraphs[paragraph].Properties.KeepTogether);

        /// <summary>
        /// Место под сноску целиком: все её абзацы. Черта над сносками сюда не входит —
        /// её считает вызывающий, если сносок на листе ещё нет.
        /// </summary>
        public float FullNeed(NoteReference reference)
        {
            float heightPt = 0f;
            foreach (var layout in LayoutsOf(reference))
                heightPt += PieceHeight(layout, 0, layout.Lines.Count);
            return heightPt;
        }

        /// <summary>
        /// Место под сноски строки таблицы, чьи знаки стоят в её ячейках, — чтобы строка
        /// встала на лист вместе с ними, а не оставила их без места. Уже поставленные и
        /// уже учтённые (<paramref name="counted"/>) не считаются.
        /// </summary>
        public float RowNeed(IEnumerable<SKTextLayout> cellParagraphs, ISet<Guid> counted)
        {
            float needPt = 0f;
            foreach (var layout in cellParagraphs)
            {
                foreach (var line in layout.Lines)
                {
                    foreach (var reference in ReferencesIn(line))
                    {
                        if (_placed.Contains(reference.Note.Id) || !counted.Add(reference.Note.Id)) continue;
                        needPt += FullNeed(reference);
                    }
                }
            }
            return needPt;
        }

        /// <summary>Высота строк [from, to) абзаца сноски с его интервалами по краям.</summary>
        private float PieceHeight(SKTextLayout layout, int from, int to)
        {
            int lines = layout.Lines.Count;
            if (lines == 0) return _emptyLinePt;

            to = Math.Min(to, lines);

            float heightPt = from == 0 ? layout.SpaceBeforePt : 0f;
            for (int li = Math.Max(0, from); li < to; li++)
                heightPt += layout.Lines[li].Height;
            if (to >= lines) heightPt += layout.SpaceAfterPt;

            return heightPt;
        }

        /// <summary>
        /// Ставит на лист столько сноски, сколько помещается в свободное место, и
        /// уменьшает его на занятое. force — начало сноски встаёт, даже если места под
        /// него не хватило.
        /// </summary>
        private void Place(Tail tail, int pageIdx, ref float availablePt, bool force)
        {
            _areas.TryGetValue(pageIdx, out var pieces);

            float bandPt = pieces is { Count: > 0 } ? 0f : _ruleBandPt;
            float budgetPt = availablePt - bandPt;
            float usedPt = 0f;
            bool placedAny = false;
            bool continued = tail.Started;

            while (!tail.Done)
            {
                var layout = tail.Layouts[tail.Paragraph];
                int lines = layout.Lines.Count;
                int from = tail.Line;
                int to = from;
                bool unsplittable = Unsplittable(tail.Note, tail.Paragraph, lines);
                bool fits;

                if (lines == 0)
                {
                    fits = usedPt + _emptyLinePt <= budgetPt + FitTolerancePt;
                }
                else
                {
                    float heightPt = from == 0 ? layout.SpaceBeforePt : 0f;
                    while (to < lines)
                    {
                        float lineHeightPt = layout.Lines[to].Height
                            + (to == lines - 1 ? layout.SpaceAfterPt : 0f);
                        if (usedPt + heightPt + lineHeightPt > budgetPt + FitTolerancePt) break;

                        heightPt += lineHeightPt;
                        to++;
                    }

                    if (to < lines)
                    {
                        if (unsplittable)
                        {
                            to = from;
                        }
                        else
                        {
                            // Хвост абзаца не короче двух строк, начало — тоже.
                            if (lines - to < SplitMinLines) to = lines - SplitMinLines;
                            if (from == 0 && to < SplitMinLines) to = from;
                            if (to < from) to = from;
                        }
                    }

                    fits = to > from;
                }

                if (!fits)
                {
                    if (!force || placedAny) break;

                    // Начало сноски встаёт принудительно: наименьший кусок, который
                    // разрешают правила разрыва.
                    if (lines > 0)
                    {
                        to = unsplittable ? lines : Math.Max(from + 1, SplitMinLines);
                        if (lines - to < SplitMinLines) to = lines;
                    }
                }

                float pieceHeightPt = PieceHeight(layout, from, to);

                if (pieces is null)
                {
                    pieces = new List<Piece>();
                    _areas[pageIdx] = pieces;
                }

                pieces.Add(new Piece(tail.Note, layout, from, lines == 0 ? 0 : to, pieceHeightPt, continued));
                usedPt += pieceHeightPt;
                placedAny = true;

                if (lines == 0 || to >= lines)
                {
                    tail.Paragraph++;
                    tail.Line = 0;
                }
                else
                {
                    tail.Line = to;
                    break;
                }
            }

            if (placedAny) availablePt -= bandPt + usedPt;
        }
    }
}
