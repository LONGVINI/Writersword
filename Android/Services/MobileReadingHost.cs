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
        /// Виды — только встроенные. Приложенные к документу и общие для всех
        /// проектов живут в настройках программы, которых на телефоне нет.
        /// </summary>
        public IReadOnlyList<ReadingTheme> ReadingThemes() => ReadingTheme.BuiltIn.ToList();

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
        /// Настройки чтения живут в сессии документа, а сессию телефон пока не
        /// пишет: книга здесь только читается. Выбранный вид переживёт смену
        /// подачи и кегля, но не перезапуск.
        /// </summary>
        public void PersistReadingPreferences() { }
    }
}
