using ReactiveUI;
using System.Collections.ObjectModel;

namespace Writersword.Modules.Synonyms.ViewModels
{
    /// <summary>
    /// ViewModel для модуля синонимов
    /// Управляет поиском и отображением списка синонимов
    /// </summary>
    public class SynonymsViewModel : ReactiveObject
    {
        /// <summary>Текст поискового запроса</summary>
        private string _searchText = string.Empty;

        /// <summary>
        /// Текст для поиска синонимов
        /// При изменении автоматически загружаются синонимы
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                this.RaiseAndSetIfChanged(ref _searchText, value);
                LoadSynonyms(); // Загружаем синонимы при изменении текста
            }
        }

        /// <summary>
        /// Список найденных синонимов
        /// Привязан к ItemsControl в View
        /// </summary>
        public ObservableCollection<string> Synonyms { get; } = new();

        /// <summary>
        /// Конструктор - создаём начальный список примеров
        /// TODO: Заменить на реальную загрузку через API
        /// </summary>
        public SynonymsViewModel()
        {
            // Заглушка - добавляем примеры синонимов
            Synonyms.Add("писать");
            Synonyms.Add("сочинять");
            Synonyms.Add("излагать");
            Synonyms.Add("записывать");
            Synonyms.Add("набрасывать");
        }

        /// <summary>
        /// Загрузить синонимы для текущего слова
        /// Пока это заглушка, позже добавим реальный API (например, Яндекс.Словари)
        /// </summary>
        private void LoadSynonyms()
        {
            // TODO: Реальная загрузка синонимов через API
            // Например: var synonyms = await _apiService.GetSynonyms(SearchText);
            System.Console.WriteLine($"[SynonymsViewModel] Searching for: {SearchText}");
        }
    }
}