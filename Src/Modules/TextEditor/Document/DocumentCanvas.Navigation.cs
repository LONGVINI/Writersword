using Avalonia.Threading;
using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Document
{
    /// <summary>
    /// Возврат на прежнее место после прыжка по ссылке.
    ///
    /// Прыжок по оглавлению, по навигатору или по поиску уносит человека туда, где он
    /// не был, и обратной дороги у него нет: в книге на триста страниц прокруткой
    /// прежнее место не найти — он не знает, где оно было. Поэтому места прыжков
    /// запоминаются, и вернуться можно в одно движение.
    ///
    /// Место запоминается опознавателем абзаца, а не номером слайса: слайсы переживают
    /// не всякую правку, опознаватель переживает любую.
    /// </summary>
    public sealed partial class DocumentCanvas
    {
        /// <summary>Место в рукописи: абзац, знак в нём и положение листа на экране.</summary>
        private readonly struct NavPlace
        {
            public NavPlace(Guid paraId, int charPos, double scrollY)
            {
                ParaId = paraId;
                CharPos = charPos;
                ScrollY = scrollY;
            }

            public Guid ParaId { get; }
            public int CharPos { get; }

            /// <summary>
            /// Прокрутка на миг прыжка. Возврат ставит её обратно как есть: каретка
            /// говорит, в какой строке человек был, а прокрутка — какой кусок книги он
            /// при этом видел, и это не одно и то же.
            /// </summary>
            public double ScrollY { get; }

            public bool IsEmpty => ParaId == Guid.Empty;
        }

        /// <summary>Места, откуда уходили прыжками. Последнее — сверху.</summary>
        private readonly List<NavPlace> _navBack = new();

        /// <summary>Места, куда можно вернуться вперёд после Alt+Влево.</summary>
        private readonly List<NavPlace> _navForward = new();

        /// <summary>Сколько прыжков помним. Дальше самые старые вытесняются.</summary>
        private const int NavDepth = 50;

        /// <summary>
        /// Фантомный возврат: место последнего прыжка, которое снимет первое нажатие
        /// Ctrl+Z.
        ///
        /// Живёт от прыжка до первой правки. Пока он есть, Ctrl+Z возвращает на прежнее
        /// место и ничего в рукописи не меняет; человек видит, что вернулся, и понимает,
        /// что произошло. Стоит ему напечатать хоть букву — фантом снимается, и Ctrl+Z
        /// снова обычная отмена.
        ///
        /// Снимается ровно один и ровно последний: цепочку прыжков отматывает Alt+Влево,
        /// а Ctrl+Z, отматывающий чужую историю, перестал бы быть отменой.
        /// </summary>
        private NavPlace _phantomReturn;

        /// <summary>
        /// Куда прыгнули, сняв фантом. Нужен Ctrl+Y: он обязан вернуть туда, откуда
        /// фантом увёл, а не повторить чью-то правку — иначе пара клавиш несимметрична.
        /// </summary>
        private NavPlace _phantomForward;

        /// <summary>Где каретка и лист прямо сейчас.</summary>
        private NavPlace CurrentNavPlace()
        {
            var pvm = GetVmAt(_caretPara);
            if (pvm?.Model is null) return default;

            return new NavPlace(
                pvm.Model.Id,
                _caretChar,
                _parentScrollViewer?.Offset.Y ?? 0);
        }

        /// <summary>
        /// Запомнить нынешнее место перед прыжком. Зовётся всеми переходами по ссылкам:
        /// оглавлением, навигатором, поиском.
        ///
        /// Ход вперёд обнуляется: человек ушёл новой дорогой, и та, по которой он мог
        /// вернуться вперёд, больше никуда не ведёт.
        /// </summary>
        private void RememberPlaceBeforeJump()
        {
            var place = CurrentNavPlace();
            if (place.IsEmpty) return;

            // Прыжок на то же место, где стоим, запоминать незачем: возврат туда же
            // выглядел бы как не сработавшая клавиша.
            if (_navBack.Count > 0 && _navBack[_navBack.Count - 1].ParaId == place.ParaId) return;

            _navBack.Add(place);
            if (_navBack.Count > NavDepth) _navBack.RemoveAt(0);

            _navForward.Clear();

            _phantomReturn = place;
            _phantomForward = default;
        }

        /// <summary>
        /// Правка отменяет фантом. Зовётся из всех мест, которые меняют рукопись.
        ///
        /// Без этого Ctrl+Z после прыжка и правки увёл бы человека по навигации, оставив
        /// правку на месте, — то есть сделал бы вид, что отменил, ничего не отменив.
        /// </summary>
        private void ForgetPhantomReturn()
        {
            _phantomReturn = default;
            _phantomForward = default;
        }

        /// <summary>
        /// Снимает фантом, если он есть: возвращает на место, откуда прыгнули.
        /// </summary>
        /// <returns>true — возврат состоялся, и отменять правку этим нажатием не надо.</returns>
        private bool TryPhantomReturn()
        {
            if (_phantomReturn.IsEmpty) return false;

            var back = _phantomReturn;
            var here = CurrentNavPlace();

            _phantomReturn = default;
            _phantomForward = here;

            // Место снято и со стека возврата: человек уже вернулся, и Alt+Влево,
            // отправляющий туда же во второй раз, выглядит как заевшая клавиша.
            if (_navBack.Count > 0 && _navBack[_navBack.Count - 1].ParaId == back.ParaId)
                _navBack.RemoveAt(_navBack.Count - 1);

            if (!here.IsEmpty) _navForward.Add(here);

            GoToNavPlace(back);
            return true;
        }

        /// <summary>
        /// Прыгает вперёд после снятого фантома — это Ctrl+Y сразу за Ctrl+Z.
        /// </summary>
        /// <returns>true — прыжок состоялся, и повторять правку этим нажатием не надо.</returns>
        private bool TryPhantomForward()
        {
            if (_phantomForward.IsEmpty) return false;

            var forward = _phantomForward;
            var here = CurrentNavPlace();

            _phantomForward = default;
            _phantomReturn = here;

            if (_navForward.Count > 0 && _navForward[_navForward.Count - 1].ParaId == forward.ParaId)
                _navForward.RemoveAt(_navForward.Count - 1);

            if (!here.IsEmpty) _navBack.Add(here);

            GoToNavPlace(forward);
            return true;
        }

        /// <summary>Назад по истории прыжков — Alt+Влево и Shift+F5.</summary>
        public void ExecuteNavigateBack()
        {
            if (_navBack.Count == 0) return;

            var target = _navBack[_navBack.Count - 1];
            _navBack.RemoveAt(_navBack.Count - 1);

            var here = CurrentNavPlace();
            if (!here.IsEmpty) _navForward.Add(here);

            // Фантом снят: возврат уже сделан этой клавишей, и Ctrl+Z обязан снова быть
            // отменой правки.
            ForgetPhantomReturn();

            GoToNavPlace(target);
        }

        /// <summary>Вперёд по истории прыжков — Alt+Вправо.</summary>
        public void ExecuteNavigateForward()
        {
            if (_navForward.Count == 0) return;

            var target = _navForward[_navForward.Count - 1];
            _navForward.RemoveAt(_navForward.Count - 1);

            var here = CurrentNavPlace();
            if (!here.IsEmpty) _navBack.Add(here);

            ForgetPhantomReturn();

            GoToNavPlace(target);
        }

        /// <summary>Есть ли куда возвращаться. По этому показывается кнопка возврата.</summary>
        public bool CanNavigateBack => _navBack.Count > 0;

        /// <summary>
        /// Ставит каретку и лист на запомненное место.
        ///
        /// Прокрутка восстанавливается своей записанной величиной, а не доводкой до
        /// каретки: доводка ставит строку к краю окна, а человек смотрел на неё там, где
        /// смотрел. Если же абзац с тех пор из рукописи ушёл, остаётся одна прокрутка —
        /// она приведёт примерно туда же, и это лучше, чем не сделать ничего.
        /// </summary>
        private void GoToNavPlace(NavPlace place)
        {
            if (place.IsEmpty || DocVm is null) return;

            int paragraphIndex = FindParagraphIndexById(place.ParaId);

            if (paragraphIndex >= 0)
            {
                if (!IsParagraphInLayouts(paragraphIndex))
                {
                    RebuildLayouts();
                    if (!IsParagraphInLayouts(paragraphIndex)) paragraphIndex = -1;
                }
            }

            if (paragraphIndex >= 0)
            {
                _caretPara = FindFirstSliceForDocVmParagraph(paragraphIndex);
                _caretChar = Clamp(place.CharPos, 0, GetVmAt(_caretPara)?.PlainText?.Length ?? 0);
                _caretLineHint = -1;

                SnapCaretToCorrectSlice();
                UpdatePreferredX();
                SyncSel();
                UpdateSelectionContext();

                var pvm = GetVmAt(_caretPara);
                if (pvm is not null && DocVm.Paragraphs.Contains(pvm)) DocVm.SetActiveParagraph(pvm);

                ResetCaret();
            }

            // Прокрутка ставится после каретки и отдельным проходом: доводка до каретки,
            // назначенная пересборкой раскладки, стоит в очереди и без этого перебила бы
            // записанное положение листа.
            double target = place.ScrollY;
            Dispatcher.UIThread.Post(() =>
            {
                if (_parentScrollViewer is null) return;
                SmoothScrollTo(Math.Max(0, target));
            }, DispatcherPriority.Background);

            InvalidateFull();
        }

        /// <summary>Место абзаца в списке абзацев по его опознавателю. -1 — такого нет.</summary>
        private int FindParagraphIndexById(Guid paraId)
        {
            if (DocVm is null) return -1;

            for (int i = 0; i < DocVm.Paragraphs.Count; i++)
                if (DocVm.Paragraphs[i].Model?.Id == paraId) return i;

            return -1;
        }
    }
}
