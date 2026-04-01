using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;

namespace Writersword.Modules.Characters.ViewModels.Onboarding
{
    /// <summary>
    /// ViewModel онбординга — опрос при первом открытии модуля в проекте.
    /// Результат сохраняется в данных проекта (IsFirstLaunch = false).
    /// </summary>
    public class CharactersOnboardingViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersOnboardingViewModel>();

        public ObservableCollection<OnboardingQuestionViewModel> Questions { get; } = new();

        public ReactiveCommand<Unit, Unit> CompleteCommand { get; }
        public ReactiveCommand<Unit, Unit> SkipCommand { get; }

        public event Action<bool>? Completed;

        public CharactersOnboardingViewModel()
        {
            Questions.Add(new OnboardingQuestionViewModel("Жанр истории", new[]
            {
                new OnboardingOptionViewModel("Фэнтези / РПГ",        new[] { "Фэнтези", "РПГ", "Эпик" }),
                new OnboardingOptionViewModel("Sci-fi / Киберпанк",   new[] { "Sci-fi", "Киберпанк", "Космическая опера" }),
                new OnboardingOptionViewModel("Детектив / Нуар",      new[] { "Детектив", "Нуар", "Триллер" }),
                new OnboardingOptionViewModel("Хоррор / Мистика",     new[] { "Хоррор", "Психологический триллер", "Мистика" }),
                new OnboardingOptionViewModel("Реализм / Драма",      Array.Empty<string>()),
            }));

            Questions.Add(new OnboardingQuestionViewModel("Особенности мира", new[]
            {
                new OnboardingOptionViewModel("Есть нечеловеческие существа / расы", new[] { "Фэнтези", "Sci-fi", "РПГ" }),
                new OnboardingOptionViewModel("Важна психология персонажей",         new[] { "Хоррор", "Детектив", "Драма" }),
                new OnboardingOptionViewModel("Важна физическая составляющая",       new[] { "Фэнтези", "Sci-fi", "Боевик" }),
                new OnboardingOptionViewModel("Есть народы / коллективные персонажи", Array.Empty<string>()),
            }));

            CompleteCommand = ReactiveCommand.Create(Complete);
            SkipCommand = ReactiveCommand.Create(Skip);
        }

        public IEnumerable<string> GetSelectedTags()
        {
            return Questions
                .SelectMany(q => q.Options)
                .Where(o => o.IsSelected)
                .SelectMany(o => o.Tags)
                .Distinct();
        }

        private void Complete()
        {
            _logger.Debug("Onboarding completed");
            Completed?.Invoke(true);
        }

        private void Skip()
        {
            _logger.Debug("Onboarding skipped");
            Completed?.Invoke(false);
        }
    }

    public class OnboardingQuestionViewModel : ReactiveObject
    {
        public string Label { get; }
        public ObservableCollection<OnboardingOptionViewModel> Options { get; } = new();

        public OnboardingQuestionViewModel(string label, OnboardingOptionViewModel[] options)
        {
            Label = label;
            foreach (var o in options) Options.Add(o);
        }
    }

    public class OnboardingOptionViewModel : ReactiveObject
    {
        private bool _isSelected;
        public string Label { get; }
        public string[] Tags { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public OnboardingOptionViewModel(string label, string[] tags)
        {
            Label = label;
            Tags = tags;
        }
    }
}