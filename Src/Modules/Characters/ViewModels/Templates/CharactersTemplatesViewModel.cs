using ReactiveUI;
using Serilog;
using System;
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
            ObservableCollection<string> activeTemplateIds)
        {
            _anketaService = anketaService;
            _activeTemplateIds = activeTemplateIds;

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
