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
    /// Один вариант ответа. Ведёт к следующему вопросу и приносит теги,
    /// по которым потом подбираются наборы полей.
    /// </summary>
    public class OnboardingOptionViewModel : ReactiveObject
    {
        public string Label { get; }
        public string[] Tags { get; }

        /// <summary>Ключ следующего вопроса; пусто — конец разговора.</summary>
        public string? NextStep { get; }

        public OnboardingOptionViewModel(string label, string[] tags, string? nextStep = null)
        {
            Label = label;
            Tags = tags;
            NextStep = nextStep;
        }
    }

    /// <summary>Шаг разговора: вопрос и варианты ответа.</summary>
    public class OnboardingStepViewModel : ReactiveObject
    {
        public string Key { get; }
        public string Question { get; }

        /// <summary>Пояснение под вопросом — зачем спрашиваем.</summary>
        public string Hint { get; }

        public ObservableCollection<OnboardingOptionViewModel> Options { get; } = new();

        public OnboardingStepViewModel(string key, string question, string hint,
            IEnumerable<OnboardingOptionViewModel> options)
        {
            Key = key;
            Question = question;
            Hint = hint;
            foreach (var option in options) Options.Add(option);
        }
    }

    /// <summary>
    /// Разговор при первом открытии модуля.
    ///
    /// Не анкета из всех вопросов сразу, а граф: один вопрос на экране,
    /// следующий зависит от ответа. Слов «параметр», «шкала», «анкета» здесь
    /// нет и быть не должно — их нечем расшифровать тому, кто открыл программу
    /// впервые.
    ///
    /// Проверка на вменяемость формулировки: поймёт ли писатель шестидесяти лет,
    /// который в компьютере открывал только видео.
    ///
    /// Ответы ничего не запирают: всё пересматривается потом во вкладке
    /// шаблонов, а разговор можно пропустить целиком.
    /// </summary>
    public class CharactersOnboardingViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersOnboardingViewModel>();

        private readonly Dictionary<string, OnboardingStepViewModel> _steps = new();
        private readonly List<OnboardingOptionViewModel> _answers = new();
        private readonly Stack<string> _history = new();

        public ReactiveCommand<OnboardingOptionViewModel, Unit> ChooseCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> SkipCommand { get; }

        /// <summary>true — разговор доведён до конца, false — пропущен.</summary>
        public event Action<bool>? Completed;

        private OnboardingStepViewModel? _current;
        public OnboardingStepViewModel? Current
        {
            get => _current;
            private set
            {
                this.RaiseAndSetIfChanged(ref _current, value);
                this.RaisePropertyChanged(nameof(CanGoBack));
            }
        }

        public bool CanGoBack => _history.Count > 0;

        public CharactersOnboardingViewModel()
        {
            BuildSteps();

            ChooseCommand = ReactiveCommand.Create<OnboardingOptionViewModel>(Choose);
            BackCommand = ReactiveCommand.Create(GoBack);
            SkipCommand = ReactiveCommand.Create(Skip);

            Current = _steps["start"];
        }

        private void BuildSteps()
        {
            Add(new OnboardingStepViewModel("start",
                "Что вы пишете?",
                "Спрашиваем один раз, чтобы не показывать лишнего. Всё это потом меняется.",
                new[]
                {
                    new OnboardingOptionViewModel("Роман или повесть", new[] { "Драма" }, "scale"),
                    new OnboardingOptionViewModel("Рассказ", Array.Empty<string>(), "depth"),
                    new OnboardingOptionViewModel("Сценарий", new[] { "Драма" }, "scale"),
                    new OnboardingOptionViewModel("Игру или мир", new[] { "Фэнтези", "РПГ" }, "scale"),
                    new OnboardingOptionViewModel("Пока не знаю", Array.Empty<string>(), null),
                }));

            Add(new OnboardingStepViewModel("scale",
                "Сколько будет героев?",
                "От этого зависит, нужны ли папки и разбор по группам.",
                new[]
                {
                    new OnboardingOptionViewModel("Несколько главных", Array.Empty<string>(), "depth"),
                    new OnboardingOptionViewModel("Много, целый мир", new[] { "Эпик" }, "groups"),
                }));

            Add(new OnboardingStepViewModel("groups",
                "Есть семьи, банды, организации?",
                "Их удобно держать отдельными карточками — у группы своя, как у человека.",
                new[]
                {
                    new OnboardingOptionViewModel("Да, и они важны", new[] { "Коллектив" }, "depth"),
                    new OnboardingOptionViewModel("Нет, только люди", Array.Empty<string>(), "depth"),
                }));

            Add(new OnboardingStepViewModel("depth",
                "Любите копаться в характерах?",
                "Привычки, страхи, темперамент — или это лишнее, и главное сюжет.",
                new[]
                {
                    new OnboardingOptionViewModel("Да, подробно",
                        new[] { "Психологический триллер", "Детектив" }, null),
                    new OnboardingOptionViewModel("Нет, главное сюжет", Array.Empty<string>(), null),
                }));
        }

        private void Add(OnboardingStepViewModel step) => _steps[step.Key] = step;

        private void Choose(OnboardingOptionViewModel option)
        {
            if (option == null || Current == null) return;

            _answers.Add(option);
            _history.Push(Current.Key);

            if (!string.IsNullOrEmpty(option.NextStep) && _steps.TryGetValue(option.NextStep, out var next))
            {
                Current = next;
                return;
            }

            _logger.Debug("Onboarding completed, answers: {Count}", _answers.Count);
            Completed?.Invoke(true);
        }

        /// <summary>
        /// Шаг назад. Разговор без возврата — ловушка: ошибся на первом
        /// вопросе, и деваться некуда.
        /// </summary>
        private void GoBack()
        {
            if (_history.Count == 0) return;

            if (_answers.Count > 0) _answers.RemoveAt(_answers.Count - 1);

            var key = _history.Pop();
            if (_steps.TryGetValue(key, out var step)) Current = step;

            this.RaisePropertyChanged(nameof(CanGoBack));
        }

        public IEnumerable<string> GetSelectedTags()
            => _answers.SelectMany(a => a.Tags).Distinct();

        private void Skip()
        {
            _logger.Debug("Onboarding skipped");
            Completed?.Invoke(false);
        }
    }
}
