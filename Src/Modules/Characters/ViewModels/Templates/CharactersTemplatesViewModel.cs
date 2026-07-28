using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.ViewModels.Templates
{
    public class CharactersTemplatesViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersTemplatesViewModel>();

        private readonly ICharacterAnketaService _anketaService;
        private readonly ObservableCollection<string> _activeTemplateIds;

        // Нужен, чтобы правка набора доезжала до карточек, к которым он
        // подключён. Необязателен: вкладка шаблонов работает и без него,
        // просто без разноса изменений.
        private readonly ICharacterService? _characterService;

        public ObservableCollection<TemplateFilterItemViewModel> Filters { get; } = new();
        public ObservableCollection<TemplateItemViewModel> FilteredTemplates { get; } = new();
        public ObservableCollection<TemplateItemViewModel> CustomTemplates { get; } = new();

        public ReactiveCommand<string, Unit> ToggleTemplateCommand { get; }
        public ReactiveCommand<string, Unit> DuplicateTemplateCommand { get; }
        public ReactiveCommand<string, Unit> DeleteCustomTemplateCommand { get; }
        public ReactiveCommand<Unit, Unit> CreateCustomTemplateCommand { get; }
        public ReactiveCommand<Unit, Unit> RestartOnboardingCommand { get; }

        public event Action? OnboardingRestartRequested;

        public CharactersTemplatesViewModel(
            ICharacterAnketaService anketaService,
            ObservableCollection<string> activeTemplateIds,
            ICharacterService? characterService = null)
        {
            _anketaService = anketaService;
            _activeTemplateIds = activeTemplateIds;
            _characterService = characterService;

            ToggleTemplateCommand = ReactiveCommand.Create<string>(ToggleTemplate);
            DuplicateTemplateCommand = ReactiveCommand.Create<string>(DuplicateTemplate);
            DeleteCustomTemplateCommand = ReactiveCommand.Create<string>(DeleteCustomTemplate);
            CreateCustomTemplateCommand = ReactiveCommand.Create(CreateCustomTemplate);
            RestartOnboardingCommand = ReactiveCommand.Create(() => OnboardingRestartRequested?.Invoke());

            BuildFilters();
            Refresh();
        }

        private void BuildFilters()
        {
            Filters.Add(new TemplateFilterItemViewModel("Фэнтези / РПГ",       new[] { "Фэнтези", "РПГ", "Эпик" },                    OnFilterChanged));
            Filters.Add(new TemplateFilterItemViewModel("Sci-fi / Киберпанк",  new[] { "Sci-fi", "Киберпанк", "Космическая опера" },   OnFilterChanged));
            Filters.Add(new TemplateFilterItemViewModel("Детектив / Нуар",     new[] { "Детектив", "Нуар", "Триллер" },                OnFilterChanged));
            Filters.Add(new TemplateFilterItemViewModel("Хоррор / Мистика",    new[] { "Хоррор", "Психологический триллер", "Мистика" }, OnFilterChanged));
            Filters.Add(new TemplateFilterItemViewModel("Коллективный",        new[] { "Коллектив" },                                  OnFilterChanged));
            Filters.Add(new TemplateFilterItemViewModel("Универсальные",       Array.Empty<string>(),                                  OnFilterChanged));
        }

        public void Refresh()
        {
            var activeTags = Filters.Where(f => f.IsChecked).SelectMany(f => f.Tags).ToList();

            var builtIn = activeTags.Any()
                ? _anketaService.GetRecommended(activeTags).Where(a => a.IsBuiltIn)
                : _anketaService.GetBuiltIn().AsEnumerable();

            FilteredTemplates.Clear();
            foreach (var a in builtIn)
                FilteredTemplates.Add(new TemplateItemViewModel(a, _activeTemplateIds.Contains(a.Id), true));

            CustomTemplates.Clear();
            foreach (var a in _anketaService.GetCustom())
                CustomTemplates.Add(new TemplateItemViewModel(a, _activeTemplateIds.Contains(a.Id), false));
        }

        private void OnFilterChanged() => Refresh();

        private void ToggleTemplate(string id)
        {
            if (_activeTemplateIds.Contains(id)) _activeTemplateIds.Remove(id);
            else _activeTemplateIds.Add(id);

            foreach (var item in FilteredTemplates.Concat(CustomTemplates))
                if (item.AnketaId == id) item.IsActive = _activeTemplateIds.Contains(id);
        }

        private void DuplicateTemplate(string id)
        {
            _anketaService.Duplicate(id);
            Refresh();
        }

        private void DeleteCustomTemplate(string id)
        {
            _activeTemplateIds.Remove(id);
            _anketaService.Delete(id);
            Refresh();
        }

        private void CreateCustomTemplate()
        {
            _anketaService.Create("Мой шаблон");
            Refresh();
        }

        /// <summary>Набор по идентификатору — для конструктора полей.</summary>
        public CharacterAnketa? GetAnketa(string id) => _anketaService.GetById(id);

        /// <summary>
        /// Все поля, известные проекту — по одному на имя. Конструктор
        /// подсказывает их при вводе: «Цвет волос» из встроенного набора
        /// и «Цвет волос» из своего должны стать одним полем, иначе значения
        /// окажутся несравнимыми при одинаковом смысле.
        /// </summary>
        public IReadOnlyList<CharacterAnketaField> GetKnownFields()
        {
            return _anketaService.GetAll()
                .SelectMany(a => a.Fields)
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .GroupBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Принять набор из файла. Идентификатор выдаётся новый, а признак
        /// встроенного снимается: чужой набор становится обычным своим,
        /// его можно править и не страшно сломать.
        ///
        /// Идентификаторы полей при этом сохраняются как есть — именно они
        /// делают карточки сравнимыми с чужими: сравнимость приходит от общей
        /// анкеты, а не от угадывания смысла имён.
        /// </summary>
        public CharacterAnketa? ImportAnketa(CharacterAnketa imported)
        {
            if (imported == null) return null;

            imported.Id = Guid.NewGuid().ToString();
            imported.IsBuiltIn = false;
            imported.CreatedAt = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(imported.Name))
                imported.Name = "Набор без названия";

            // Сервис умеет создавать и обновлять; создаём пустой и наполняем
            // принятыми данными, чтобы набор попал в список пользовательских.
            var created = _anketaService.Create(imported.Name);
            imported.Id = created.Id;
            _anketaService.Update(imported);

            Refresh();
            _logger.Debug("Anketa imported: '{Name}', {Count} fields", imported.Name, imported.Fields.Count);
            return imported;
        }

        /// <summary>
        /// Сохранить набор после правки в конструкторе. Список обновляется
        /// сразу: число полей на карточке набора должно измениться на глазах,
        /// иначе непонятно, применилось ли что-нибудь.
        /// </summary>
        public void SaveCustomTemplate(CharacterAnketa anketa)
        {
            _anketaService.Update(anketa);

            // Новые поля разъезжаются по карточкам, к которым набор подключён:
            // иначе автор добавил поле и не нашёл его там, где ждал. Значения
            // уже заполненных полей при этом не трогаются.
            _characterService?.SyncAnketa(anketa);

            Refresh();
        }
    }

    public class TemplateFilterItemViewModel : ReactiveObject
    {
        private bool _isChecked;
        private readonly Action _onChanged;

        public string Label { get; }
        public string[] Tags { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set { this.RaiseAndSetIfChanged(ref _isChecked, value); _onChanged(); }
        }

        public TemplateFilterItemViewModel(string label, string[] tags, Action onChanged)
        {
            Label = label; Tags = tags; _onChanged = onChanged;
        }
    }

    public class TemplateItemViewModel : ReactiveObject
    {
        private bool _isActive;

        public string AnketaId { get; }
        public string Name { get; }
        public string Description { get; }
        public bool IsBuiltIn { get; }
        public int FieldCount { get; }

        public bool IsActive
        {
            get => _isActive;
            set => this.RaiseAndSetIfChanged(ref _isActive, value);
        }

        public TemplateItemViewModel(CharacterAnketa anketa, bool isActive, bool isBuiltIn)
        {
            AnketaId = anketa.Id;
            Name = anketa.Name;
            Description = anketa.Description;
            IsBuiltIn = isBuiltIn;
            FieldCount = anketa.Fields.Count;
            _isActive = isActive;
        }
    }
}
