using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.ViewModels.Templates
{
    /// <summary>
    /// Черновик набора полей. Правка идёт на копии: закрытие без сохранения
    /// оставляет исходный набор нетронутым.
    ///
    /// Набор — это определение полей, а не значения. Определение живёт отдельно
    /// от карточек: максимум шкалы, подписи делений и подсказки хранятся здесь,
    /// иначе шкалы разных персонажей несравнимы между собой.
    /// </summary>
    public class AnketaEditorDraft : ReactiveObject
    {
        public string AnketaId { get; }

        private string _name = string.Empty;
        public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

        private string _description = string.Empty;
        public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }

        public ObservableCollection<AnketaFieldDraft> Fields { get; } = new();

        public bool HasFields => Fields.Count > 0;

        /// <summary>
        /// Имена полей, уже существующих в проекте. Подсказываются при вводе:
        /// выбрал существующее — получил его идентификатор, и значения стали
        /// сравнимы с теми, что уже введены в карточках. Это и есть «сравнимость
        /// приходит от общей анкеты», только на уровне отдельного поля.
        /// </summary>
        public ObservableCollection<string> KnownFieldNames { get; } = new();

        // Имя поля -> его идентификатор. Программа ничего не угадывает:
        // связь возникает, только если автор выбрал существующее имя.
        private readonly Dictionary<string, string> _knownFieldIds =
            new(StringComparer.CurrentCultureIgnoreCase);

        public AnketaEditorDraft(CharacterAnketa anketa, IEnumerable<CharacterAnketaField>? knownFields = null)
        {
            AnketaId = anketa.Id;
            _name = anketa.Name;
            _description = anketa.Description;

            if (knownFields != null)
            {
                foreach (var field in knownFields)
                {
                    if (string.IsNullOrWhiteSpace(field.Name)) continue;
                    if (_knownFieldIds.ContainsKey(field.Name)) continue;

                    _knownFieldIds[field.Name] = CharacterFieldId.Resolve(field);
                    KnownFieldNames.Add(field.Name);
                }
            }

            foreach (var field in anketa.Fields.OrderBy(f => f.Order))
                Fields.Add(new AnketaFieldDraft(field));

            Fields.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasFields));
        }

        /// <summary>Идентификатор существующего поля с таким именем, если оно есть.</summary>
        public string? FindFieldId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return _knownFieldIds.TryGetValue(name.Trim(), out var id) ? id : null;
        }

        public void AddField(CharacterParameterType type)
        {
            var field = new CharacterAnketaField
            {
                Name = string.Empty,
                Type = type,
                Order = Fields.Count
            };

            // Короткая шкала по умолчанию: «3 из 5» читается сразу, значение
            // на диапазоне 0..100 не говорит ничего без объяснений.
            if (type == CharacterParameterType.Numeric)
            {
                field.DefaultMinValue = 0;
                field.DefaultMaxValue = 5;
                field.Step = 1;
            }

            Fields.Add(new AnketaFieldDraft(field));
        }

        public void RemoveField(AnketaFieldDraft field) => Fields.Remove(field);

        public void MoveField(AnketaFieldDraft field, int delta)
        {
            var index = Fields.IndexOf(field);
            if (index < 0) return;

            var target = index + delta;
            if (target < 0 || target >= Fields.Count) return;

            Fields.Move(index, target);
        }

        /// <summary>
        /// Собрать набор из черновика. Поля без имени отбрасываются: безымянное
        /// поле нечем ни спросить, ни показать.
        /// </summary>
        public CharacterAnketa ToAnketa(CharacterAnketa original)
        {
            original.Name = string.IsNullOrWhiteSpace(Name) ? original.Name : Name.Trim();
            original.Description = Description?.Trim() ?? string.Empty;

            original.Fields = Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .Select((f, index) => f.ToField(index, FindFieldId(f.Name)))
                .ToList();

            return original;
        }
    }

    /// <summary>Одно поле набора в черновике.</summary>
    public class AnketaFieldDraft : ReactiveObject
    {
        private readonly CharacterAnketaField _source;

        public AnketaFieldDraft(CharacterAnketaField source)
        {
            _source = source;
            _name = source.Name;
            _description = source.Description;
            _type = source.Type;
            _isComparable = source.IsComparable;
            _maxValue = source.DefaultMaxValue;
            _states = source.StatesRaw;

            // Подписи делений вводятся одной строкой через запятую: «спокоен,
            // сдержан, заводится, взрывается, неуправляем». Так их набирают
            // за один заход, не открывая по диалогу на каждое деление.
            _scaleLabels = string.Join(", ", source.ScalePoints
                .OrderBy(p => p.Key)
                .Select(p => p.Value));
        }

        private string _name;
        public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

        private string _description;
        public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }

        private CharacterParameterType _type;
        public CharacterParameterType Type
        {
            get => _type;
            set
            {
                this.RaiseAndSetIfChanged(ref _type, value);
                this.RaisePropertyChanged(nameof(IsScale));
                this.RaisePropertyChanged(nameof(IsChoice));
            }
        }

        public bool IsScale => _type == CharacterParameterType.Numeric;
        public bool IsChoice => _type == CharacterParameterType.StateList;

        private bool _isComparable;
        /// <summary>
        /// Данные сравниваются между карточками, упражнения — нет. «Опишите,
        /// как персонаж повёл бы себя» ценно тем, что заставило подумать,
        /// а не тем, что попало в таблицу.
        /// </summary>
        public bool IsComparable { get => _isComparable; set => this.RaiseAndSetIfChanged(ref _isComparable, value); }

        private double _maxValue;
        public double MaxValue { get => _maxValue; set => this.RaiseAndSetIfChanged(ref _maxValue, value); }

        private string _scaleLabels;
        public string ScaleLabels { get => _scaleLabels; set => this.RaiseAndSetIfChanged(ref _scaleLabels, value); }

        private string _states;
        public string States { get => _states; set => this.RaiseAndSetIfChanged(ref _states, value); }

        /// <param name="knownFieldId">
        /// Идентификатор существующего поля с таким же именем, если автор выбрал
        /// его из подсказок. Тогда значения нового поля встают в один ряд
        /// с уже введёнными в карточках.
        /// </param>
        public CharacterAnketaField ToField(int order, string? knownFieldId = null)
        {
            _source.Name = Name.Trim();
            _source.Description = Description?.Trim() ?? string.Empty;
            _source.Type = Type;
            _source.IsComparable = IsComparable;
            _source.Order = order;
            _source.StatesRaw = States?.Trim() ?? string.Empty;

            // Идентификатор поля задаётся один раз и дальше не меняется:
            // переименование вопроса не должно рвать связь со значениями,
            // уже введёнными в карточках.
            if (string.IsNullOrWhiteSpace(_source.FieldId))
            {
                _source.FieldId = !string.IsNullOrWhiteSpace(knownFieldId)
                    ? knownFieldId
                    : CharacterFieldId.FromName(_source.Name);
            }

            if (Type == CharacterParameterType.Numeric)
            {
                _source.DefaultMinValue = 0;
                _source.DefaultMaxValue = MaxValue > 0 ? MaxValue : 5;
                _source.Step = 1;
                _source.ScalePoints = BuildScalePoints(_source.DefaultMinValue, _source.DefaultMaxValue);
            }

            return _source;
        }

        /// <summary>
        /// Разложить подписи по делениям шкалы. Подписей может быть меньше,
        /// чем делений — лишние деления останутся без слова, и это нормально:
        /// подпись помогает выбрать, а не обязана быть у каждого.
        /// </summary>
        private Dictionary<double, string> BuildScalePoints(double min, double max)
        {
            var result = new Dictionary<double, string>();
            if (string.IsNullOrWhiteSpace(ScaleLabels)) return result;

            var labels = ScaleLabels
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (labels.Count == 0) return result;

            for (int i = 0; i < labels.Count; i++)
            {
                double value = min + i;
                if (value > max) break;
                result[value] = labels[i];
            }

            return result;
        }
    }
}
