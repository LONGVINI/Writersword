using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Toc;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.ViewModels.Toc
{
    /// <summary>
    /// Узел дерева заголовков в навигаторе.
    /// </summary>
    public sealed class NavigatorNode : ReactiveObject
    {
        private bool _isExpanded = true;
        private bool _isCurrent;
        private int _pageNumber;

        // Владелец узнаёт о свёрнутой ветке, чтобы вернуть её такой же после пересборки
        // дерева. Ветку сворачивают руками, а пересобирается дерево само — и без этой
        // памяти набранная в заголовке буква разворачивала бы всё обратно.
        private readonly Action<NavigatorNode>? _onExpandedChanged;

        public NavigatorNode(TocHeading heading, Action<NavigatorNode>? onExpandedChanged = null)
        {
            BlockId = heading.BlockId;
            Text = heading.Text;
            Level = heading.Level;
            ParagraphIndex = heading.ParagraphIndex;
            _pageNumber = heading.PageNumber;
            _onExpandedChanged = onExpandedChanged;
        }

        public Guid BlockId { get; }

        public string Text { get; }

        /// <summary>Уровень заголовка: 1 — глава, дальше глубже.</summary>
        public int Level { get; }

        /// <summary>Место абзаца в потоке документа — по нему делается переход.</summary>
        public int ParagraphIndex { get; set; }

        /// <summary>Номер страницы. Ноль — раскладка ещё не считала, показывать нечего.</summary>
        public int PageNumber
        {
            get => _pageNumber;
            set
            {
                this.RaiseAndSetIfChanged(ref _pageNumber, value);
                this.RaisePropertyChanged(nameof(PageText));
                this.RaisePropertyChanged(nameof(HasPage));
            }
        }

        public string PageText => _pageNumber > 0 ? _pageNumber.ToString() : string.Empty;

        public bool HasPage => _pageNumber > 0;

        /// <summary>Ветка раскрыта.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                this.RaiseAndSetIfChanged(ref _isExpanded, value);
                _onExpandedChanged?.Invoke(this);
            }
        }

        /// <summary>
        /// В этом заголовке сейчас стоит каретка. Подсветка ведётся по абзацу, а не по
        /// прокрутке: человек листает документ глазами, а работает там, где курсор.
        /// </summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
        }

        /// <summary>Отступ строки в дереве — по уровню заголовка.</summary>
        public double Indent => (Level - 1) * 12.0;

        public ObservableCollection<NavigatorNode> Children { get; } = new();
    }

    /// <summary>
    /// Навигатор по заголовкам: дерево глав сбоку от рукописи.
    ///
    /// От оглавления в тексте он отличается назначением. Оглавление — часть книги, его
    /// печатают. Навигатор — рабочий инструмент: он показывает структуру, пока её пишут,
    /// и потому живёт вне документа, не занимает страницу и не требует обновления по
    /// кнопке. Заголовки для обоих собирает один и тот же <see cref="TocService"/>.
    /// </summary>
    public sealed class NavigatorViewModel : ReactiveObject
    {
        private string _searchText = string.Empty;
        private int _maxVisibleLevel = 9;
        private bool _isVisible;
        private Guid _currentBlockId;

        // Раскрытость веток переживает пересборку дерева: набранная буква в заголовке
        // пересобирает список целиком, и без этой памяти дерево схлопывалось бы на каждый
        // нажатый символ.
        private readonly HashSet<Guid> _collapsed = new();

        /// <summary>Корни дерева.</summary>
        public ObservableCollection<NavigatorNode> Roots { get; } = new();

        /// <summary>Панель показана.</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        /// <summary>
        /// Строка поиска по заголовкам. Отбор идёт по вхождению без учёта регистра;
        /// родитель остаётся в дереве, если подошёл кто-то из его детей — иначе ветка
        /// с найденной подглавой исчезла бы вместе с главой.
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                this.RaiseAndSetIfChanged(ref _searchText, value ?? string.Empty);
                RebuildFromCache();
            }
        }

        /// <summary>Глубина показа: 1 — только главы, 9 — всё.</summary>
        public int MaxVisibleLevel
        {
            get => _maxVisibleLevel;
            set
            {
                int clamped = value < 1 ? 1 : (value > 9 ? 9 : value);
                this.RaiseAndSetIfChanged(ref _maxVisibleLevel, clamped);
                RebuildFromCache();
            }
        }

        /// <summary>В рукописи нет ни одного заголовка.</summary>
        public bool IsEmpty => Roots.Count == 0;

        /// <summary>
        /// Переход к абзацу по индексу в потоке документа. Ставит владелец панели.
        /// </summary>
        public Action<int>? GoToParagraphRequested { get; set; }

        // Последний собранный плоский список: по нему идут отбор и перестроение дерева
        // без повторного обхода документа.
        private List<TocHeading> _flat = new();

        /// <summary>
        /// Пересобирает дерево по документу. Вызывается на изменение структуры и на
        /// пересчёт раскладки — второй раз только ради номеров страниц.
        /// </summary>
        public void Rebuild(DocumentModel? doc, IReadOnlyDictionary<Guid, int>? pageMap)
        {
            _flat = doc is null
                ? new List<TocHeading>()
                : TocService.Collect(doc, includeManual: true, pageMap: pageMap);

            RebuildFromCache();
        }

        /// <summary>
        /// Обновляет только номера страниц у уже построенного дерева. Дешевле полной
        /// пересборки и не роняет раскрытость веток и выделение.
        /// </summary>
        public void UpdatePageNumbers(IReadOnlyDictionary<Guid, int>? pageMap)
        {
            if (pageMap is null) return;

            foreach (var heading in _flat)
                if (pageMap.TryGetValue(heading.BlockId, out int page))
                    heading.PageNumber = page;

            foreach (var root in Roots)
                ApplyPages(root, pageMap);
        }

        private static void ApplyPages(NavigatorNode node, IReadOnlyDictionary<Guid, int> pageMap)
        {
            if (pageMap.TryGetValue(node.BlockId, out int page))
                node.PageNumber = page;

            foreach (var child in node.Children)
                ApplyPages(child, pageMap);
        }

        /// <summary>
        /// Отмечает заголовок, в котором стоит каретка. Ищется ближайший заголовок выше
        /// абзаца: человек может стоять в середине главы, и подсветиться должна она.
        /// </summary>
        public void SetCurrentParagraph(int paragraphIndex)
        {
            Guid found = Guid.Empty;

            for (int i = 0; i < _flat.Count; i++)
            {
                if (_flat[i].ParagraphIndex > paragraphIndex) break;
                found = _flat[i].BlockId;
            }

            if (found == _currentBlockId) return;
            _currentBlockId = found;

            foreach (var root in Roots)
                ApplyCurrent(root, found);
        }

        private static void ApplyCurrent(NavigatorNode node, Guid currentId)
        {
            node.IsCurrent = node.BlockId == currentId;
            foreach (var child in node.Children)
                ApplyCurrent(child, currentId);
        }

        /// <summary>Переход к заголовку.</summary>
        public void GoTo(NavigatorNode? node)
        {
            if (node is null) return;
            GoToParagraphRequested?.Invoke(node.ParagraphIndex);
        }

        /// <summary>Свернуть все ветки.</summary>
        public void CollapseAll() => SetExpandedAll(false);

        /// <summary>Развернуть все ветки.</summary>
        public void ExpandAll() => SetExpandedAll(true);

        private void SetExpandedAll(bool expanded)
        {
            _collapsed.Clear();
            foreach (var root in Roots)
                SetExpandedRecursive(root, expanded);
        }

        private void SetExpandedRecursive(NavigatorNode node, bool expanded)
        {
            node.IsExpanded = expanded;
            if (!expanded) _collapsed.Add(node.BlockId);

            foreach (var child in node.Children)
                SetExpandedRecursive(child, expanded);
        }

        /// <summary>Запоминает раскрытость ветки, чтобы она пережила пересборку.</summary>
        public void RememberExpanded(NavigatorNode node)
        {
            if (node is null) return;
            if (node.IsExpanded) _collapsed.Remove(node.BlockId);
            else _collapsed.Add(node.BlockId);
        }

        // ── Внутреннее ────────────────────────────────────────────────────

        private void RebuildFromCache()
        {
            var filtered = Filter(_flat);
            var tree = TocService.BuildTree(filtered);

            Roots.Clear();
            foreach (var heading in tree)
                Roots.Add(ToNode(heading));

            this.RaisePropertyChanged(nameof(IsEmpty));

            if (_currentBlockId != Guid.Empty)
                foreach (var root in Roots)
                    ApplyCurrent(root, _currentBlockId);
        }

        private List<TocHeading> Filter(List<TocHeading> flat)
        {
            var byLevel = new List<TocHeading>(flat.Count);
            foreach (var heading in flat)
                if (heading.Level <= _maxVisibleLevel)
                    byLevel.Add(heading);

            if (_searchText.Length == 0) return byLevel;

            // Найденная подглава тянет за собой всю цепочку родителей: без них она
            // висела бы в дереве без места, и человек не понял бы, где это в книге.
            var keep = new HashSet<Guid>();
            var chain = new List<TocHeading>();

            foreach (var heading in byLevel)
            {
                while (chain.Count > 0 && chain[chain.Count - 1].Level >= heading.Level)
                    chain.RemoveAt(chain.Count - 1);
                chain.Add(heading);

                if (heading.Text.IndexOf(_searchText, StringComparison.CurrentCultureIgnoreCase) < 0)
                    continue;

                foreach (var parent in chain)
                    keep.Add(parent.BlockId);
            }

            var result = new List<TocHeading>(keep.Count);
            foreach (var heading in byLevel)
                if (keep.Contains(heading.BlockId))
                    result.Add(heading);

            return result;
        }

        private NavigatorNode ToNode(TocHeading heading)
        {
            var node = new NavigatorNode(heading, RememberExpanded)
            {
                // При поиске всё раскрыто: человек ищет, а не разбирает структуру.
                IsExpanded = _searchText.Length > 0 || !_collapsed.Contains(heading.BlockId)
            };

            foreach (var child in heading.Children)
                node.Children.Add(ToNode(child));

            return node;
        }
    }
}
