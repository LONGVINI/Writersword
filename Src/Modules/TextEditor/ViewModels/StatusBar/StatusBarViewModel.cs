using ReactiveUI;

namespace Writersword.Modules.TextEditor.ViewModels.StatusBar
{
    /// <summary>
    /// ViewModel нижней панели статистики редактора.
    /// Обновляется при изменении выделения и после каждого автосохранения.
    /// </summary>
    public sealed class StatusBarViewModel : ReactiveObject
    {
        private int _wordCount;
        private int _charCount;
        private int _charCountNoSpaces;
        private int _pageCount;
        private int _paragraphCount;
        private int _currentPage;
        private double _zoom;
        private string _language = "ru";
        private bool _isSpellCheckActive;
        private bool _isReadOnly;

        /// <summary>Количество слов в документе.</summary>
        public int WordCount
        {
            get => _wordCount;
            set => this.RaiseAndSetIfChanged(ref _wordCount, value);
        }

        /// <summary>Количество символов с пробелами.</summary>
        public int CharCount
        {
            get => _charCount;
            set => this.RaiseAndSetIfChanged(ref _charCount, value);
        }

        /// <summary>Количество символов без пробелов.</summary>
        public int CharCountNoSpaces
        {
            get => _charCountNoSpaces;
            set => this.RaiseAndSetIfChanged(ref _charCountNoSpaces, value);
        }

        /// <summary>Общее количество страниц.</summary>
        public int PageCount
        {
            get => _pageCount;
            set => this.RaiseAndSetIfChanged(ref _pageCount, value);
        }

        /// <summary>Количество абзацев.</summary>
        public int ParagraphCount
        {
            get => _paragraphCount;
            set => this.RaiseAndSetIfChanged(ref _paragraphCount, value);
        }

        /// <summary>Текущая страница (1-based).</summary>
        public int CurrentPage
        {
            get => _currentPage;
            set => this.RaiseAndSetIfChanged(ref _currentPage, value);
        }

        /// <summary>Текущий масштаб (0.25 – 5.0).</summary>
        public double Zoom
        {
            get => _zoom;
            set => this.RaiseAndSetIfChanged(ref _zoom, value);
        }

        /// <summary>Код активного языка ввода.</summary>
        public string Language
        {
            get => _language;
            set => this.RaiseAndSetIfChanged(ref _language, value);
        }

        /// <summary>Проверка орфографии активна.</summary>
        public bool IsSpellCheckActive
        {
            get => _isSpellCheckActive;
            set => this.RaiseAndSetIfChanged(ref _isSpellCheckActive, value);
        }

        /// <summary>Документ в режиме только чтения.</summary>
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
        }

        /// <summary>Процент масштаба для отображения (например 100, 150).</summary>
        public int ZoomPercent => (int)(_zoom * 100);

        /// <summary>Обновляет статистику из plain text документа.</summary>
        public void UpdateFromText(string fullText, int paragraphCount, int pageCount)
        {
            ParagraphCount = paragraphCount;
            PageCount = pageCount;

            if (string.IsNullOrEmpty(fullText))
            {
                WordCount = 0;
                CharCount = 0;
                CharCountNoSpaces = 0;
                return;
            }

            CharCount = fullText.Length;

            int noSpaces = 0;
            foreach (char c in fullText)
                if (!char.IsWhiteSpace(c)) noSpaces++;
            CharCountNoSpaces = noSpaces;

            // Подсчёт слов: разбиваем по пробельным символам.
            WordCount = CountWords(fullText);
        }

        private static int CountWords(string text)
        {
            bool inWord = false;
            int count = 0;

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '\'')
                {
                    if (!inWord) { inWord = true; count++; }
                }
                else
                {
                    inWord = false;
                }
            }

            return count;
        }
    }
}
