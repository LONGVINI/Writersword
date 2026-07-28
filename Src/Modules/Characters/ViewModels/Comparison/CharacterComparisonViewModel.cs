using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.ViewModels.Comparison
{
    /// <summary>
    /// Сравнение карточек: персонажи в колонках, поля в строках.
    ///
    /// Ради этого поля и заводились. Сравнение ловит то, чего автор сам
    /// не видит: пять героев с одинаковыми значениями — значит все говорят
    /// одним голосом, и это болезнь, которую иначе замечают на третьей главе.
    ///
    /// Сравниваются поля, помеченные в наборе как данные. Упражнения вроде
    /// «опишите, как персонаж повёл бы себя» в таблицу не идут: они ценны тем,
    /// что заставили подумать, а не тем, что попали в клетку.
    /// </summary>
    public class CharacterComparisonViewModel : ReactiveObject
    {
        /// <summary>Ширина колонки одного персонажа. Общая для шапки и строк.</summary>
        public const double ColumnWidth = 150;

        public ObservableCollection<CharacterComparisonColumn> Columns { get; } = new();
        public ObservableCollection<CharacterComparisonRow> Rows { get; } = new();

        public bool HasRows => Rows.Count > 0;
        public bool IsEmpty => Rows.Count == 0;

        private string _summary = string.Empty;
        /// <summary>Строка-итог: сколько карточек и сколько общих полей.</summary>
        public string Summary { get => _summary; set => this.RaiseAndSetIfChanged(ref _summary, value); }

        public void Build(IEnumerable<Character> characters)
        {
            Columns.Clear();
            Rows.Clear();

            var list = characters?.ToList() ?? new List<Character>();
            foreach (var character in list)
                Columns.Add(new CharacterComparisonColumn(character.Name, character.Color));

            // Поле попадает в таблицу, только если оно есть у всех выбранных:
            // столбец с прочерками у половины карточек ничего не сравнивает,
            // он просто шумит.
            var shared = list
                .Select(c => c.Parameters
                    .Where(p => p.IsComparable)
                    .Select(p => CharacterFieldId.Resolve(p))
                    .ToHashSet())
                .Aggregate((IEnumerable<string>?)null, (acc, ids) =>
                    acc == null ? ids : acc.Intersect(ids))
                ?.ToList() ?? new List<string>();

            foreach (var fieldId in shared)
            {
                var first = list[0].Parameters.First(p => CharacterFieldId.Resolve(p) == fieldId);
                var row = new CharacterComparisonRow(first.Name);

                foreach (var character in list)
                {
                    var parameter = character.Parameters
                        .First(p => CharacterFieldId.Resolve(p) == fieldId);

                    row.Cells.Add(new CharacterComparisonCell(Format(parameter)));
                }

                // Одинаковые значения по всей строке отмечаются: именно они
                // и означают, что персонажи неразличимы в этом.
                var distinct = row.Cells.Select(c => c.Text).Distinct().Count();
                row.AllSame = distinct <= 1 && row.Cells.Count > 1;

                Rows.Add(row);
            }

            this.RaisePropertyChanged(nameof(HasRows));
            this.RaisePropertyChanged(nameof(IsEmpty));
        }

        /// <summary>
        /// Значение человеческим языком. Для шкалы — подпись деления, если она
        /// задана: «заводится» говорит больше, чем «3 / 5».
        /// </summary>
        private static string Format(CharacterParameter parameter)
        {
            if (parameter.IsNotApplicable) return "—";

            switch (parameter.Type)
            {
                case CharacterParameterType.Numeric:
                    if (parameter.ScalePoints != null &&
                        parameter.ScalePoints.TryGetValue(parameter.NumericValue, out var label) &&
                        !string.IsNullOrWhiteSpace(label))
                        return label;

                    return parameter.NumericValue.ToString("0.##", CultureInfo.CurrentCulture)
                        + " / "
                        + parameter.MaxValue.ToString("0.##", CultureInfo.CurrentCulture);

                case CharacterParameterType.Text:
                    return parameter.TextValue ?? string.Empty;

                case CharacterParameterType.StateList:
                    if (parameter.States != null &&
                        parameter.CurrentStateIndex >= 0 &&
                        parameter.CurrentStateIndex < parameter.States.Count)
                        return parameter.States[parameter.CurrentStateIndex];
                    return string.Empty;

                case CharacterParameterType.Boolean:
                    return parameter.BoolValue ? parameter.TrueLabel : parameter.FalseLabel;

                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// Строка с одинаковыми значениями приглушается: она и есть тот сигнал,
    /// ради которого сравнение затевалось.
    /// </summary>
    public class SameRowOpacityConverter : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly SameRowOpacityConverter Instance = new();

        public object? Convert(object? value, Type t, object? p, CultureInfo c)
            => value is bool same && same ? 0.55 : 1.0;

        public object? ConvertBack(object? value, Type t, object? p, CultureInfo c)
            => throw new NotImplementedException();
    }

    public class CharacterComparisonColumn
    {
        public string Name { get; }
        public string Color { get; }
        public double Width => CharacterComparisonViewModel.ColumnWidth;

        public CharacterComparisonColumn(string name, string color)
        {
            Name = name;
            Color = color;
        }
    }

    public class CharacterComparisonRow
    {
        public string FieldName { get; }
        public ObservableCollection<CharacterComparisonCell> Cells { get; } = new();

        /// <summary>Все значения в строке совпали — персонажи здесь неразличимы.</summary>
        public bool AllSame { get; set; }

        public CharacterComparisonRow(string fieldName) => FieldName = fieldName;
    }

    public class CharacterComparisonCell
    {
        public string Text { get; }
        public double Width => CharacterComparisonViewModel.ColumnWidth;

        public CharacterComparisonCell(string text) => Text = text;
    }
}
