using System;
using System.Collections.Generic;
using System.Linq;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Перенос определения поля анкеты в значение персонажа.
    ///
    /// Значение хранит копию определения — края шкалы, варианты, подписи,
    /// вид, — чтобы показываться и без анкеты. Поэтому при правке анкеты копию
    /// надо обновить, иначе новый вид или переименованный полюс так и не дойдут
    /// до уже заведённых персонажей. Обновляется только определение: вписанные
    /// числа, отмеченные варианты, текст и примечания не трогаются.
    /// </summary>
    public static class CharacterFieldDefinition
    {
        /// <summary>Скопировать в значение настройки вида поля.</summary>
        public static void CopyDisplay(CharacterAnketaField field, CharacterParameter parameter)
        {
            parameter.Display = field.Display;
            parameter.AccentColor = field.AccentColor ?? string.Empty;
            parameter.Unit = field.Unit ?? string.Empty;
            parameter.NumberModes = field.NumberModes == CharacterNumberModes.None
                ? CharacterNumberModes.Exact
                : field.NumberModes;
            parameter.Stages = (field.Stages ?? new List<CharacterNumberStage>())
                .Select(s => s.Clone())
                .ToList();
            parameter.Palette = (field.Palette ?? new List<string>()).ToList();
            parameter.AllowCustomColor = field.AllowCustomColor;
        }

        /// <summary>
        /// Обновить у уже заведённого значения определение поля после правки
        /// анкеты. Значение, у которого тип разошёлся с полем, не трогается:
        /// число, прочитанное как выбор, исказило бы данные, а менять тип
        /// у заполненного значения молча нельзя.
        /// </summary>
        public static bool Refresh(CharacterAnketaField field, CharacterParameter parameter)
        {
            if (parameter.Type != field.Type) return false;

            parameter.Name = field.Name;
            parameter.Description = field.Description;
            parameter.GroupName = field.GroupName;
            parameter.IsComparable = field.IsComparable;
            parameter.Order = field.Order;

            parameter.MinDescription = field.MinDescription;
            parameter.MaxDescription = field.MaxDescription;
            parameter.TrueLabel = field.TrueLabel;
            parameter.FalseLabel = field.FalseLabel;
            parameter.RandomRangeMin = field.RandomRangeMin;
            parameter.RandomRangeMax = field.RandomRangeMax;
            parameter.ScalePoints = new Dictionary<double, string>(field.ScalePoints ?? new Dictionary<double, string>());

            if (field.Type == CharacterParameterType.Numeric)
            {
                parameter.MinValue = field.DefaultMinValue;
                parameter.MaxValue = field.DefaultMaxValue;
                parameter.Step = field.Step > 0 ? field.Step : 1;

                // Число за новыми краями прижимается к ним: шарики и полоса
                // не умеют показать значение вне шкалы.
                if (parameter.NumericValue < parameter.MinValue) parameter.NumericValue = parameter.MinValue;
                if (parameter.NumericValue > parameter.MaxValue) parameter.NumericValue = parameter.MaxValue;
            }

            if (field.Type == CharacterParameterType.StateList ||
                field.Type == CharacterParameterType.MultiChoice)
            {
                var states = SplitStates(field.StatesRaw);

                // Одиночный выбор хранится номером: после правки списка номер
                // переводится на то же слово, а если слова больше нет —
                // выбор сбрасывается на первый вариант, как у нового значения.
                if (field.Type == CharacterParameterType.StateList)
                {
                    var current = parameter.CurrentStateIndex >= 0 &&
                                  parameter.CurrentStateIndex < parameter.States.Count
                        ? parameter.States[parameter.CurrentStateIndex]
                        : null;

                    var index = current == null ? -1 : states.IndexOf(current);
                    parameter.States = states;
                    parameter.CurrentStateIndex = index >= 0 ? index : 0;
                }
                else
                {
                    parameter.States = states;
                }
            }

            CopyDisplay(field, parameter);

            // Способ, который анкета больше не разрешает, у значения остаётся:
            // вписанное не теряется, а при следующей правке автор выберет
            // разрешённый.
            return true;
        }

        public static List<string> SplitStates(string? raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? new List<string>()
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => s.Trim())
                     .Where(s => s.Length > 0)
                     .ToList();
    }
}
