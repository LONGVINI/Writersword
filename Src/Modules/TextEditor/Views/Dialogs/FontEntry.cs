using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Строка списка гарнитур.
    ///
    /// Заголовок раздела хранится у первой строки раздела, а не отдельным пунктом.
    /// Отдельным пунктом он был бы выбираемым: стрелки останавливались бы на нём,
    /// а Enter применял бы к рукописи слово «Недавние». Строк одного и того же
    /// шрифта в списке может быть две — в закреплённых и среди всех, — и это
    /// намеренно: наверху лежит то, чем пользуются, внизу полный перечень.
    ///
    /// Звёздочка и заголовок раздела умеют меняться на месте и сообщают об этом
    /// списку. Раньше они были доступны только для чтения, а закрепление
    /// пересобирало список целиком — из-за чего человека выбрасывало к началу, к
    /// тому самому пункту, который только что уехал в верхний раздел. Теперь
    /// закрепление зажигает звезду там, где она есть, и список не двигается;
    /// разделы перестраиваются на следующем открытии.
    /// </summary>
    public sealed class FontEntry : INotifyPropertyChanged
    {
        public FontEntry(string name, bool isPinned, string? sectionTitle, bool inPinnedSection = false)
        {
            Name = name;
            _isPinned = isPinned;
            _sectionTitle = sectionTitle;
            InPinnedSection = inPinnedSection;
        }

        public string Name { get; }

        /// <summary>
        /// Строка стоит в разделе закреплённых, а не среди всех шрифтов. По этому
        /// признаку решается, можно ли её перетаскивать: порядок есть только у
        /// закреплённых, полный перечень идёт по алфавиту и переставлять в нём нечего.
        /// </summary>
        public bool InPinnedSection { get; }

        private bool _isPinned;

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned == value) return;
                _isPinned = value;
                Raise();
            }
        }

        private bool _isDragging;

        /// <summary>
        /// Строку держат указателем. Пока её тащат, она приподнята над списком:
        /// без этого перестановка читается как рябь — строки меняются местами, и
        /// непонятно, какую из них держат.
        /// </summary>
        public bool IsDragging
        {
            get => _isDragging;
            set
            {
                if (_isDragging == value) return;
                _isDragging = value;
                Raise();
            }
        }

        private string? _sectionTitle;

        /// <summary>
        /// Заголовок раздела над строкой. Пусто — строка не первая в разделе.
        /// Меняется при перетаскивании: заголовок обязан остаться у той строки,
        /// которая после перестановки оказалась первой.
        /// </summary>
        public string? SectionTitle
        {
            get => _sectionTitle;
            set
            {
                if (_sectionTitle == value) return;
                _sectionTitle = value;
                Raise();
                Raise(nameof(HasSection));
            }
        }

        public bool HasSection => SectionTitle is not null;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
