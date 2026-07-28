using System.Collections.Generic;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Параметр персонажа. Наследует ObservableModel: значения правятся в одном
    /// месте интерфейса, а отображаются в другом (кругляшки шкалы, подпись
    /// значения, состояние «неприменимо»), поэтому модель обязана уведомлять
    /// об изменениях. Событие PropertyChanged не свойство и в JSON не попадает.
    /// </summary>
    public class CharacterParameter : ObservableModel
    {
        private string _id = System.Guid.NewGuid().ToString();
        public string Id { get => _id; set => Set(ref _id, value); }

        /// <summary>
        /// Идентификатор поля, значением которого этот параметр является.
        /// В отличие от Id он одинаков у всех персонажей: Id опознаёт значение,
        /// FieldId — само поле. Без него сравнить «силу» двух карточек нечем,
        /// потому что при применении анкеты каждый персонаж получает свой Id.
        ///
        /// Аддитивно: у старых сохранений выводится из имени при загрузке.
        /// </summary>
        private string _fieldId = string.Empty;
        public string FieldId { get => _fieldId; set => Set(ref _fieldId, value); }

        private string _name = string.Empty;
        public string Name { get => _name; set => Set(ref _name, value); }

        /// <summary>
        /// Примечание к значению — свободная строка рядом с ним: «да, но
        /// в тушёном виде», «187, сутулится и кажется ниже». Значение
        /// сравнивается и считается, примечание живёт для человека.
        /// Структура никогда не вмещает жизнь целиком, каким бы точным
        /// ни был тип поля.
        /// </summary>
        private string _valueNote = string.Empty;
        public string ValueNote { get => _valueNote; set => Set(ref _valueNote, value); }

        /// <summary>
        /// Участвует ли значение в сравнении карточек. Приезжает из определения
        /// поля, но остаётся у значения: анкету могли отключить, а значение
        /// осталось.
        /// </summary>
        private bool _isComparable = true;
        public bool IsComparable { get => _isComparable; set => Set(ref _isComparable, value); }

        private CharacterParameterType _type = CharacterParameterType.Numeric;
        public CharacterParameterType Type { get => _type; set => Set(ref _type, value); }

        private string _groupName = string.Empty;
        public string GroupName { get => _groupName; set => Set(ref _groupName, value); }

        private string _description = string.Empty;
        public string Description { get => _description; set => Set(ref _description, value); }

        private int _order;
        public int Order { get => _order; set => Set(ref _order, value); }

        /// <summary>
        /// Параметр к этому персонажу не относится в принципе. Это не значение,
        /// а переопределение: «страх = 0» и «не испытывает страха» — разные
        /// утверждения, и смешивать их в одном поле нельзя. Значение при этом
        /// сохраняется: снятие отметки возвращает прежние данные.
        /// </summary>
        private bool _isNotApplicable;
        public bool IsNotApplicable { get => _isNotApplicable; set => Set(ref _isNotApplicable, value); }

        // ── Шкала ────────────────────────────────────────────────────────

        private double _minValue;
        public double MinValue { get => _minValue; set => Set(ref _minValue, value); }

        private double _maxValue = 5;
        public double MaxValue { get => _maxValue; set => Set(ref _maxValue, value); }

        private double _numericValue;
        public double NumericValue { get => _numericValue; set => Set(ref _numericValue, value); }

        private double _step = 1;
        public double Step { get => _step; set => Set(ref _step, value); }

        private string _minDescription = string.Empty;
        public string MinDescription { get => _minDescription; set => Set(ref _minDescription, value); }

        private string _maxDescription = string.Empty;
        public string MaxDescription { get => _maxDescription; set => Set(ref _maxDescription, value); }

        /// <summary>Диапазон рандомизации — min. Ручной ввод за пределами не ограничен.</summary>
        private double? _randomRangeMin;
        public double? RandomRangeMin { get => _randomRangeMin; set => Set(ref _randomRangeMin, value); }

        /// <summary>Диапазон рандомизации — max. Ручной ввод за пределами не ограничен.</summary>
        private double? _randomRangeMax;
        public double? RandomRangeMax { get => _randomRangeMax; set => Set(ref _randomRangeMax, value); }

        /// <summary>Точки шкалы с описаниями для тултипов. Ключ — значение, значение — описание.</summary>
        public Dictionary<double, string> ScalePoints { get; set; } = new();

        // ── Текст ────────────────────────────────────────────────────────

        private string _textValue = string.Empty;
        public string TextValue { get => _textValue; set => Set(ref _textValue, value); }

        // ── Выбор ────────────────────────────────────────────────────────

        public List<string> States { get; set; } = new();

        private int _currentStateIndex;
        public int CurrentStateIndex { get => _currentStateIndex; set => Set(ref _currentStateIndex, value); }

        // ── Да или нет ───────────────────────────────────────────────────

        private bool _boolValue;
        public bool BoolValue { get => _boolValue; set => Set(ref _boolValue, value); }

        private string _trueLabel = "Да";
        public string TrueLabel { get => _trueLabel; set => Set(ref _trueLabel, value); }

        private string _falseLabel = "Нет";
        public string FalseLabel { get => _falseLabel; set => Set(ref _falseLabel, value); }
    }
}
