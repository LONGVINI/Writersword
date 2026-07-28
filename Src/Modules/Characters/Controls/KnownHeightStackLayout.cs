using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Строка списка, которая сама знает свою высоту. Реализуется вьюмоделью,
    /// а не контролом: раскладка обязана знать высоту ещё не созданных строк,
    /// поэтому спрашивать её у элемента поздно.
    /// </summary>
    public interface IRowHeight
    {
        double RowHeight { get; }
    }

    /// <summary>
    /// Вертикальная виртуализованная раскладка для списка строк разной, но
    /// заранее известной высоты.
    ///
    /// Зачем: штатный StackLayout высоту нереализованных строк не знает —
    /// он оценивает её по уже показанным. Пока строки одинаковые, оценка
    /// точна; как только в одном списке идут заголовки папок (34) и карточки
    /// персонажей (48), оценка при каждой прокрутке пересчитывается: ползунок
    /// меняет размер, скролл дёргается и прыгает. Из-за этого боковой список
    /// редактора пришлось оставить невиртуализованным целиком.
    ///
    /// Здесь высота каждой строки известна заранее (IRowHeight), поэтому
    /// смещения считаются точной суммой, а не догадкой. Общая высота верна
    /// с первого измерения, ползунок не плавает, реализуются только видимые
    /// строки.
    ///
    /// Список плоский и одноуровневый — вложенных репитеров нет, а значит нет
    /// и зацикленного перемера, ради которого писался PerfItemsRepeater.
    /// </summary>
    public class KnownHeightStackLayout : VirtualizingLayout
    {
        /// <summary>Высота строки, не сообщившей свою собственную.</summary>
        public static readonly StyledProperty<double> DefaultRowHeightProperty =
            AvaloniaProperty.Register<KnownHeightStackLayout, double>(
                nameof(DefaultRowHeight), defaultValue: 48);

        /// <summary>Зазор между строками.</summary>
        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<KnownHeightStackLayout, double>(nameof(Spacing));

        public double DefaultRowHeight
        {
            get => GetValue(DefaultRowHeightProperty);
            set => SetValue(DefaultRowHeightProperty, value);
        }

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        // Реализованные строки: индекс элемента -> элемент. Контекст своего
        // списка не отдаёт, поэтому ведём его сами — иначе нечего перерабатывать
        // при уходе строки за пределы видимой области.
        // Тип Layoutable, а не Control: GetOrCreateElementAt отдаёт именно его.
        private readonly Dictionary<int, Layoutable> _realized = new();

        private int _firstRealized = -1;
        private int _lastRealized = -1;

        // Повторное измерение после первого прохода запрашивается один раз:
        // без флага запрос повторялся бы бесконечно, пока область видимости
        // остаётся неизвестной.
        private bool _viewportRequested;

        protected override void InitializeForContextCore(VirtualizingLayoutContext context)
        {
            base.InitializeForContextCore(context);
            _realized.Clear();
            _firstRealized = -1;
            _lastRealized = -1;
        }

        protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
        {
            base.UninitializeForContextCore(context);
            _realized.Clear();
        }

        protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object? source,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        {
            // Добавление за пределами показанного диапазона индексы уже
            // реализованных строк не сдвигает — пересоздавать их незачем.
            //
            // Это не мелочь: список наполняется батчами, изменений сотни,
            // и полный сброс на каждом означал бы, что строка под курсором
            // исчезает между нажатием и отпусканием — щелчки по списку
            // не срабатывают, пока идёт загрузка.
            bool appendedBelow =
                args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && args.NewStartingIndex > _lastRealized;

            if (!appendedBelow)
            {
                foreach (var element in _realized.Values)
                    context.RecycleElement(element);

                _realized.Clear();
                _firstRealized = -1;
                _lastRealized = -1;
            }

            _viewportRequested = false;

            InvalidateMeasure();
        }

        private double GetRowHeight(VirtualizingLayoutContext context, int index)
        {
            var item = context.GetItemAt(index);
            if (item is IRowHeight row && row.RowHeight > 0) return row.RowHeight;
            return DefaultRowHeight;
        }

        protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
        {
            int count = context.ItemCount;
            if (count == 0)
            {
                RecycleRange(context, int.MinValue, int.MinValue);
                return new Size(0, 0);
            }

            var viewport = context.RealizationRect;
            bool viewportKnown = viewport.Height > 0;

            // Пока область реализации не известна (первый проход, нулевая
            // высота), показываем начало списка: иначе не будет создано
            // ни одной строки и репитер останется пустым.
            double viewportTop = viewportKnown ? viewport.Top : 0;
            double viewportBottom = viewportKnown ? viewport.Bottom : availableSize.Height;
            if (double.IsInfinity(viewportBottom) || viewportBottom <= 0)
                viewportBottom = double.IsInfinity(availableSize.Height) ? 1200 : availableSize.Height;

            // Запас сверху и снизу. Без него строка, стоящая ровно на границе,
            // проявляется только после прокрутки: репитер обновляет область
            // видимости не мгновенно, и на первом показе часть строк успевает
            // остаться нереализованной — место под них занято, а контролов нет.
            const double Buffer = 240;
            viewportTop -= Buffer;
            viewportBottom += Buffer;

            // Пока окно прокрутки неизвестно, показываем начало списка целиком
            // с запасом: к моменту, когда репитер сообщит настоящую область,
            // видимые строки уже созданы, и пустого места на первом кадре нет.
            if (!viewportKnown)
            {
                viewportTop = 0;
                viewportBottom = Math.Max(viewportBottom, 1600);
            }

            double offset = 0;
            int first = -1;
            int last = -1;

            for (int i = 0; i < count; i++)
            {
                double height = GetRowHeight(context, i);
                double bottom = offset + height;

                bool visible = bottom > viewportTop && offset < viewportBottom;
                if (visible)
                {
                    if (first < 0) first = i;
                    last = i;

                    var element = context.GetOrCreateElementAt(i);
                    _realized[i] = element;
                    element.Measure(new Size(availableSize.Width, height));
                }

                offset = bottom + Spacing;
            }

            // Хвостовой зазор в общую высоту не входит.
            double total = count > 0 ? Math.Max(0, offset - Spacing) : 0;

            RecycleRange(context, first, last);
            _firstRealized = first;
            _lastRealized = last;

            // Первый проход идёт без настоящей области видимости — размер
            // посчитан по догадке. Просим ещё один, но не отсюда: инвалидация
            // во время измерения игнорируется, поэтому вызов уходит в очередь
            // диспетчера и отрабатывает уже после прохода, когда репитер знает
            // окно прокрутки.
            if (!viewportKnown && !_viewportRequested)
            {
                _viewportRequested = true;

                Avalonia.Threading.Dispatcher.UIThread.Post(
                    InvalidateMeasure,
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }
            else if (viewportKnown)
            {
                _viewportRequested = false;
            }

            double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            return new Size(width, total);
        }

        protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
        {
            int count = context.ItemCount;
            if (count == 0 || _realized.Count == 0) return finalSize;

            double offset = 0;

            for (int i = 0; i < count; i++)
            {
                double height = GetRowHeight(context, i);

                // Расставляем всё, что реализовано, а не диапазон из последнего
                // измерения: Arrange может прийти без предшествующего Measure,
                // и тогда границы диапазона уже не описывают набор элементов.
                if (_realized.TryGetValue(i, out var element))
                    element.Arrange(new Rect(0, offset, finalSize.Width, height));

                offset += height + Spacing;
            }

            return finalSize;
        }

        /// <summary>
        /// Вернуть репитеру строки, вышедшие за пределы видимой области.
        /// Без этого список растёт контролами, которые никто не показывает.
        /// </summary>
        private void RecycleRange(VirtualizingLayoutContext context, int keepFirst, int keepLast)
        {
            if (_realized.Count == 0) return;

            List<int>? dropped = null;

            foreach (var pair in _realized)
            {
                if (pair.Key >= keepFirst && pair.Key <= keepLast) continue;
                (dropped ??= new List<int>()).Add(pair.Key);
            }

            if (dropped == null) return;

            foreach (var index in dropped)
            {
                context.RecycleElement(_realized[index]);
                _realized.Remove(index);
            }
        }
    }
}
