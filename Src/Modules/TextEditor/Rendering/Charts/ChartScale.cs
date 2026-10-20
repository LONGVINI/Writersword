using System;
using System.Collections.Generic;
using System.Globalization;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering.Charts
{
    /// <summary>Шкала оси значений: пределы и шаг делений.</summary>
    public readonly record struct ChartAxisScale(double Min, double Max, double Step)
    {
        /// <summary>Доля длины оси для значения: 0 — начало оси, 1 — конец.</summary>
        public double Fraction(double value) => Max > Min ? (value - Min) / (Max - Min) : 0.0;

        /// <summary>Значения делений от начала до конца оси.</summary>
        public IEnumerable<double> Ticks()
        {
            if (Step <= 0 || Max <= Min) yield break;

            int count = (int)Math.Round((Max - Min) / Step);
            if (count > 200) count = 200;
            for (int i = 0; i <= count; i++)
            {
                double value = Min + i * Step;
                // Сумма шагов накапливает погрешность: 0,1 + 0,2 даёт 0,30000000000000004.
                yield return Math.Round(value / Step) * Step;
            }
        }
    }

    /// <summary>
    /// Расчёт диаграммы без рисования: пределы осей, накопление рядов, места столбцов и
    /// подписи чисел. Отдельно от рендера — чтобы проверяться без графики.
    /// </summary>
    public static class ChartScale
    {
        /// <summary>
        /// Шкала оси, как её подбирает Excel: «круглый» шаг 1, 2 или 5 на степень десяти
        /// так, чтобы делений поместилось столько, сколько позволяет длина оси, и верх
        /// оси строго выше наибольшего значения. Ноль в шкалу входит у столбцов и
        /// областей всегда, у линий — когда разброс данных велик относительно их величины.
        /// </summary>
        /// <param name="dataMin">Наименьшее значение данных.</param>
        /// <param name="dataMax">Наибольшее значение данных.</param>
        /// <param name="lengthPt">Длина оси, пт.</param>
        /// <param name="labelPt">Высота подписи деления, пт.</param>
        /// <param name="forceZero">Ноль обязан быть на оси.</param>
        /// <param name="fixedMin">Заданный минимум или null.</param>
        /// <param name="fixedMax">Заданный максимум или null.</param>
        /// <param name="minStep">
        /// Наименьший шаг: у формата чисел с заданным числом знаков (<see cref="MinStepFor"/>)
        /// деления мельче его последнего знака подписались бы одинаковыми числами — у
        /// целых «0, 0, 1, 2, 2, 2» вместо «0, 1, 2».
        /// </param>
        public static ChartAxisScale Compute(
            double dataMin, double dataMax, double lengthPt, double labelPt,
            bool forceZero, double? fixedMin = null, double? fixedMax = null, double minStep = 0)
        {
            if (double.IsNaN(dataMin) || double.IsNaN(dataMax) || double.IsInfinity(dataMin) || double.IsInfinity(dataMax)
                || dataMin > dataMax)
            {
                dataMin = 0;
                dataMax = 1;
            }

            double min = dataMin;
            double max = dataMax;

            if (forceZero)
            {
                if (min > 0) min = 0;
                if (max < 0) max = 0;
            }
            else if (min > 0 && (max - min) / max >= 1.0 / 6.0)
            {
                min = 0;
            }
            else if (max < 0 && (max - min) / -min >= 1.0 / 6.0)
            {
                max = 0;
            }

            if (fixedMin is double fMin) min = fMin;
            if (fixedMax is double fMax) max = fMax;

            if (max <= min)
            {
                if (fixedMax is null) max = min == 0 ? 1 : min + Math.Abs(min);
                else min = max - (max == 0 ? 1 : Math.Abs(max));
            }

            // Деление на каждые три-четыре высоты подписи: так густо ставит их Excel —
            // у диаграммы Word по умолчанию шесть делений по высоте.
            int target = (int)Math.Clamp(lengthPt / Math.Max(labelPt * 3.6, 24.0), 2.0, 10.0);
            double step = NiceStep((max - min) / target);
            if (minStep > 0 && step < minStep - 1e-12) step = NiceStep(minStep);

            double axisMax = fixedMax ?? (max <= 0 && dataMax <= 0 && (forceZero || max == 0)
                ? 0
                : (Math.Floor(max / step + 1e-9) + 1) * step);

            double axisMin;
            if (fixedMin is double given) axisMin = given;
            else if (min >= 0) axisMin = min == 0 ? 0 : Math.Floor(min / step - 1e-9) * step;
            else axisMin = (Math.Ceiling(min / step - 1e-9) - 1) * step;

            // Нижний предел, поднятый над нулём, не должен оказаться выше данных.
            if (fixedMin is null && axisMin > dataMin) axisMin = Math.Floor(dataMin / step) * step;
            if (fixedMax is null && axisMax < dataMax) axisMax = Math.Ceiling(dataMax / step) * step;

            if (axisMax <= axisMin) axisMax = axisMin + step;

            return new ChartAxisScale(axisMin, axisMax, step);
        }

        /// <summary>
        /// Наименьший шаг делений для формата чисел: единица последнего знака формата.
        /// «0» и «#,##0» — 1, «0.0» — 0,1, «0.00» — 0,01; у процентов на два порядка
        /// мельче («0%» — 0,01, «0.0%» — 0,001). Общий формат и пустой — без ограничения.
        /// </summary>
        public static double MinStepFor(string? format)
        {
            if (string.IsNullOrWhiteSpace(format)) return 0;
            string code = format.Split(';')[0];
            if (code.Equals("General", StringComparison.OrdinalIgnoreCase)) return 0;

            // Текст в кавычках и экранированные знаки к числу знаков не относятся.
            var plain = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (c == '\\') { i++; continue; }
                plain.Append(c);
            }
            code = plain.ToString();
            if (code.IndexOfAny(new[] { '0', '#' }) < 0) return 0;

            int decimals = 0;
            int point = code.IndexOf('.');
            if (point >= 0)
                for (int i = point + 1; i < code.Length && (code[i] == '0' || code[i] == '#'); i++) decimals++;

            if (code.Contains('%')) decimals += 2;
            return Math.Pow(10, -decimals);
        }

        /// <summary>Шаг 1, 2 или 5 на степень десяти, не меньше заданного.</summary>
        public static double NiceStep(double raw)
        {
            if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 1;

            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double normalized = raw / magnitude;

            double nice = normalized <= 1.0 + 1e-9 ? 1
                : normalized <= 2.0 + 1e-9 ? 2
                : normalized <= 5.0 + 1e-9 ? 5
                : 10;

            return nice * magnitude;
        }

        /// <summary>
        /// Ряды, стоящие друг на друге: столбцы, линии и области при накоплении.
        /// Значение точки ряда — пара «от» и «до». У рядов рядом друг с другом «от» — ноль.
        /// Отрицательные значения копятся вниз отдельно от положительных, как у Excel.
        /// </summary>
        /// <returns>Для каждого ряда из списка — по точке на категорию; null — пустая ячейка.</returns>
        public static List<(double From, double To)?[]> Stack(
            IReadOnlyList<ChartSeries> series, int categoryCount, ChartGrouping grouping)
        {
            var result = new List<(double From, double To)?[]>(series.Count);
            for (int s = 0; s < series.Count; s++)
                result.Add(new (double, double)?[categoryCount]);

            for (int c = 0; c < categoryCount; c++)
            {
                double total = 0;
                if (grouping == ChartGrouping.PercentStacked)
                {
                    foreach (var one in series)
                        if (c < one.Values.Count && one.Values[c] is double v) total += Math.Abs(v);
                }

                double positive = 0, negative = 0;
                for (int s = 0; s < series.Count; s++)
                {
                    if (c >= series[s].Values.Count || series[s].Values[c] is not double value) continue;
                    if (double.IsNaN(value) || double.IsInfinity(value)) continue;

                    if (grouping == ChartGrouping.PercentStacked)
                        value = total > 0 ? value / total : 0;

                    if (grouping == ChartGrouping.Standard)
                    {
                        result[s][c] = (0, value);
                        continue;
                    }

                    if (value >= 0)
                    {
                        result[s][c] = (positive, positive + value);
                        positive += value;
                    }
                    else
                    {
                        result[s][c] = (negative, negative + value);
                        negative += value;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Места столбцов в слоте категории, как у Excel: ширина столбца из зазора между
        /// группами (доли ширины столбца) и перекрытия столбцов группы (-1…1).
        /// </summary>
        /// <returns>Смещение первого столбца от начала слота, шаг между столбцами и ширина столбца.</returns>
        public static (double FirstOffset, double Pitch, double Width) BarSlots(
            double slotLength, int barsInCluster, double gapFraction, double overlapFraction)
        {
            int m = Math.Max(1, barsInCluster);
            double overlap = Math.Clamp(overlapFraction, -1.0, 1.0);
            double gap = Math.Clamp(gapFraction, 0.0, 5.0);

            double width = slotLength / (m - (m - 1) * overlap + gap);
            double pitch = width * (1 - overlap);
            double cluster = width + pitch * (m - 1);
            double first = (slotLength - cluster) / 2.0;

            return (first, pitch, width);
        }

        /// <summary>
        /// Подпись числа по формату Excel диаграммы: проценты («0%», «0.0%»), число знаков
        /// после запятой («0.00»), разделитель тысяч («#,##0»). Без формата — общий вид
        /// числа, как у Excel: без лишних нулей.
        /// </summary>
        public static string FormatValue(double value, string? format, int? decimals = null)
        {
            var culture = CultureInfo.CurrentCulture;

            if (!string.IsNullOrEmpty(format) && format != "General")
            {
                string main = format.Split(';')[0];
                bool percent = main.Contains('%');
                int places = 0;
                int dot = main.IndexOf('.');
                if (dot >= 0)
                {
                    for (int i = dot + 1; i < main.Length && (main[i] == '0' || main[i] == '#'); i++) places++;
                }
                bool thousands = main.Contains(',') && (dot < 0 || main.IndexOf(',') < dot);

                double shown = percent ? value * 100.0 : value;
                string pattern = (thousands ? "N" : "F") + places.ToString(CultureInfo.InvariantCulture);
                string text = shown.ToString(pattern, culture);
                return percent ? text + "%" : text;
            }

            if (decimals is int fixedPlaces)
                return value.ToString("F" + Math.Clamp(fixedPlaces, 0, 10).ToString(CultureInfo.InvariantCulture), culture);

            double rounded = Math.Round(value, 10);
            return rounded.ToString("G10", culture);
        }

        /// <summary>Знаков после запятой у подписей делений с этим шагом.</summary>
        public static int DecimalsForStep(double step)
        {
            if (step <= 0 || double.IsNaN(step)) return 0;
            int places = (int)Math.Ceiling(-Math.Log10(step) - 1e-9);
            return Math.Clamp(places, 0, 10);
        }
    }
}
