using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.TextEditor.Document;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.ViewModels;
using Writersword.Modules.TextEditor.ViewModels.Reading;

namespace Writersword.Mobile.Services
{
    /// <summary>
    /// Договор ленты чтения, исполненный на телефоне.
    ///
    /// Худ читалки не пишет своей логики: он вешается на ту же
    /// ReadingRibbonViewModel, что и лента настольной программы, а та работает
    /// через этот договор. Отсюда главное свойство — правки означают ровно то же
    /// самое: те же пределы, те же ступени, тот же разбор на «пересобрать
    /// раскладку» и «просто перерисовать». Написать телефону свою половину этих
    /// правил значило бы завести вторую читалку, которая через месяц разъедется
    /// с первой.
    ///
    /// Не исполнено здесь три вещи, и каждая по своей причине. Виды чтения
    /// правятся отдельным окном, которого на телефоне нет. Полный экран телефону
    /// не нужен: у него и так весь экран. Выход из чтения некуда делать —
    /// читалка и есть всё приложение.
    /// </summary>
    public sealed class MobileReadingHost : IReadingHost
    {
        private readonly DocumentCanvas _canvas;
        private readonly Func<DocumentViewModel?> _document;

        public MobileReadingHost(DocumentCanvas canvas, Func<DocumentViewModel?> document)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _document = document ?? throw new ArgumentNullException(nameof(document));
        }

        public ReadingSettings? Reading => _document()?.Reading;

        /// <summary>
        /// Встроенные виды и приложенные к книге.
        ///
        /// Приложенные лежат в самой рукописи (DocumentModel.ReadingThemes) и
        /// потому доезжают на телефон вместе с ней — доставать их неоткуда не
        /// нужно. Вид, чей опознаватель совпал со встроенным, не задваивается:
        /// встроенному лишь ставится пометка, что он приложен к документу, —
        /// так же поступает и настольная лента.
        ///
        /// Общих для всех проектов видов здесь нет и не будет: они живут в
        /// настройках программы и привязаны к машине, а не к книге.
        /// </summary>
        public IReadOnlyList<ReadingTheme> ReadingThemes()
        {
            var result = new List<ReadingTheme>();
            var byId = new Dictionary<string, ReadingTheme>(StringComparer.Ordinal);

            foreach (var builtIn in ReadingTheme.BuiltIn)
            {
                var copy = builtIn.Clone();
                result.Add(copy);
                byId[copy.Id] = copy;
            }

            var document = _document()?.Document.ReadingThemes;
            if (document is null)
                return result;

            foreach (var theme in document)
            {
                if (byId.TryGetValue(theme.Id, out var existing))
                {
                    existing.InDocument = true;
                    continue;
                }

                var copy = theme.Clone();
                copy.InDocument = true;
                result.Add(copy);
                byId[copy.Id] = copy;
            }

            return result;
        }

        public void ApplyReadingLayout() => _canvas.ApplyReadingSettings();

        public void ApplyReadingVisual() => _document()?.RaiseReadingVisualChanged();

        public void TurnReadingPage(int direction) => _canvas.SpreadTurn(direction);

        public void GoReadingFirst() => _canvas.SpreadGoToPage(0);

        public void GoReadingLast() => _canvas.SpreadGoToPage(Math.Max(0, _canvas.SpreadPageCount - 1));

        public void GoReadingPage(int pageIndex, bool animate) => _canvas.SpreadGoToPage(pageIndex, animate);

        /// <summary>Выходить некуда: читалка и есть всё приложение.</summary>
        public void ExitReading() { }

        /// <summary>Телефон и так во весь экран.</summary>
        public void ApplyReadingFullscreen(bool on) { }

        /// <summary>Окна правки видов на телефоне нет.</summary>
        public void OpenReadingThemes() { }

        /// <summary>
        /// Настройки чтения — личное дело читателя, поэтому лежат они в данных
        /// приложения, а не в проекте: уехав с рукописью, они открыли бы её у
        /// получателя чужими глазами.
        /// </summary>
        public void PersistReadingPreferences() => ReaderState.Save(Reading);
    }
}
