using System;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ReactiveUI;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.ViewModels.StatusBar
{
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
        private EditorViewMode _viewMode = EditorViewMode.Page;

        private bool _viewModeChanging;
        private bool _zoomChanging;

        // --- Статистика ---
        public int WordCount { get => _wordCount; set => this.RaiseAndSetIfChanged(ref _wordCount, value); }
        public int CharCount { get => _charCount; set => this.RaiseAndSetIfChanged(ref _charCount, value); }
        public int CharCountNoSpaces { get => _charCountNoSpaces; set => this.RaiseAndSetIfChanged(ref _charCountNoSpaces, value); }
        public int ParagraphCount { get => _paragraphCount; set => this.RaiseAndSetIfChanged(ref _paragraphCount, value); }
        public int PageCount { get => _pageCount; set => this.RaiseAndSetIfChanged(ref _pageCount, Math.Max(1, value)); }
        public int CurrentPage { get => _currentPage; set => this.RaiseAndSetIfChanged(ref _currentPage, value); }
        public string Language { get => _language; set => this.RaiseAndSetIfChanged(ref _language, value); }
        public bool IsSpellCheckActive { get => _isSpellCheckActive; set => this.RaiseAndSetIfChanged(ref _isSpellCheckActive, value); }
        public bool IsReadOnly { get => _isReadOnly; set => this.RaiseAndSetIfChanged(ref _isReadOnly, value); }

        // --- Масштаб ---
        public double Zoom
        {
            get => _zoom;
            set
            {
                if (_zoomChanging) return;
                _zoomChanging = true;
                try
                {
                    double clamped = Math.Max(0.25, Math.Min(5.0, value));
                    this.RaiseAndSetIfChanged(ref _zoom, clamped);
                    this.RaisePropertyChanged(nameof(ZoomPercent));
                    ZoomChanged?.Invoke(clamped);
                }
                finally
                {
                    _zoomChanging = false;
                }
            }
        }

        public int ZoomPercent => (int)Math.Round(_zoom * 100);

        public Action<double>? ZoomChanged { get; set; }

        // --- Режим отображения ---
        public EditorViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                if (_viewModeChanging || _viewMode == value) return;
                _viewModeChanging = true;
                try
                {
                    this.RaiseAndSetIfChanged(ref _viewMode, value);
                    this.RaisePropertyChanged(nameof(IsPageMode));
                    this.RaisePropertyChanged(nameof(IsDraftMode));
                    this.RaisePropertyChanged(nameof(IsWebMode));
                    this.RaisePropertyChanged(nameof(IsReadingMode));
                    ViewModeChanged?.Invoke(value);
                }
                finally
                {
                    _viewModeChanging = false;
                }
            }
        }

        public bool IsPageMode => _viewMode == EditorViewMode.Page;
        public bool IsDraftMode => _viewMode == EditorViewMode.Draft;
        public bool IsWebMode => _viewMode == EditorViewMode.Web;
        public bool IsReadingMode => _viewMode == EditorViewMode.Reading;

        public Action<EditorViewMode>? ViewModeChanged { get; set; }

        public ICommand SetPageModeCommand { get; }
        public ICommand SetDraftModeCommand { get; }
        public ICommand SetWebModeCommand { get; }
        public ICommand SetReadingModeCommand { get; }

        public StatusBarViewModel()
        {
            SetPageModeCommand = ReactiveCommand.Create(() => { ViewMode = EditorViewMode.Page; });
            SetDraftModeCommand = ReactiveCommand.Create(() => { ViewMode = EditorViewMode.Draft; });
            SetWebModeCommand = ReactiveCommand.Create(() => { ViewMode = EditorViewMode.Web; });
            SetReadingModeCommand = ReactiveCommand.Create(() => { ViewMode = EditorViewMode.Reading; });
        }

        // --- Обновление статистики ---
        public void UpdateFromText(string fullText, int paragraphCount, int pageCount)
        {
            ParagraphCount = paragraphCount;
            PageCount = pageCount;
            CharCount = fullText.Length;
            CharCountNoSpaces = fullText.Replace(" ", "").Replace("\t", "").Length;
            WordCount = CountWords(fullText);
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return Regex.Matches(text, @"\S+").Count;
        }
    }
}