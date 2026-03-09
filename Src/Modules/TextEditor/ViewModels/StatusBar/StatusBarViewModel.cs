using System;
using System.Text.RegularExpressions;
using ReactiveUI;

namespace Writersword.Modules.TextEditor.ViewModels.StatusBar
{
    /// <summary>
    /// ViewModel строки состояния редактора.
    /// Отображает статистику текста, язык, масштаб и флаги режимов.
    /// </summary>
    public sealed class StatusBarViewModel : ReactiveObject
    {
        private int _wordCount;
        private int _charCount;
        private int _charCountNoSpaces;
        private int _paragraphCount;
        private int _pageCount = 1;
        private int _currentPage = 1;
        private double _zoom = 1.0;
        private string _language = "ru";
        private bool _isSpellCheckActive;
        private bool _isReadOnly;

        // --- Статистика текста ---

        public int WordCount
        {
            get => _wordCount;
            set => this.RaiseAndSetIfChanged(ref _wordCount, value);
        }

        public int CharCount
        {
            get => _charCount;
            set => this.RaiseAndSetIfChanged(ref _charCount, value);
        }

        public int CharCountNoSpaces
        {
            get => _charCountNoSpaces;
            set => this.RaiseAndSetIfChanged(ref _charCountNoSpaces, value);
        }

        public int ParagraphCount
        {
            get => _paragraphCount;
            set => this.RaiseAndSetIfChanged(ref _paragraphCount, value);
        }

        public int PageCount
        {
            get => _pageCount;
            set => this.RaiseAndSetIfChanged(ref _pageCount, value);
        }

        public int CurrentPage
        {
            get => _currentPage;
            set => this.RaiseAndSetIfChanged(ref _currentPage, value);
        }

        // --- Масштаб ---

        public double Zoom
        {
            get => _zoom;
            set
            {
                double clamped = Math.Max(0.25, Math.Min(5.0, value));
                this.RaiseAndSetIfChanged(ref _zoom, clamped);
                this.RaisePropertyChanged(nameof(ZoomPercent));
            }
        }

        /// <summary>Масштаб в процентах для отображения в строке состояния (25–500).</summary>
        public int ZoomPercent => (int)Math.Round(_zoom * 100);

        // --- Язык и режимы ---

        public string Language
        {
            get => _language;
            set => this.RaiseAndSetIfChanged(ref _language, value);
        }

        public bool IsSpellCheckActive
        {
            get => _isSpellCheckActive;
            set => this.RaiseAndSetIfChanged(ref _isSpellCheckActive, value);
        }

        public bool IsReadOnly
        {
            get => _isReadOnly;
            set => this.RaiseAndSetIfChanged(ref _isReadOnly, value);
        }

        // --- Обновление ---

        /// <summary>
        /// Пересчитывает статистику по полному тексту документа.
        /// Слова считаются по пробельным границам.
        /// </summary>
        public void UpdateFromText(string fullText, int paragraphCount, int pageCount)
        {
            ParagraphCount = paragraphCount;
            PageCount      = Math.Max(1, pageCount);
            CharCount      = fullText.Length;
            CharCountNoSpaces = fullText.Replace(" ", "").Replace("\t", "").Length;
            WordCount      = CountWords(fullText);
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return Regex.Matches(text, @"\S+").Count;
        }
    }
}
