using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Rendering.Charts
{
    /// <summary>
    /// Расчёты аналитики диаграммы: линии тренда по методу наименьших квадратов (как у
    /// Excel: экспонента и степенная — по логарифмам, R² — там же), скользящее среднее
    /// и величины планок погрешностей.
    /// </summary>
    internal static class ChartTrend
    {
        /// <summary>Найденная линия тренда: функция, уравнение для подписи и R².</summary>
        internal sealed record TrendFit(Func<double, double> Evaluate, string Equation, double RSquared);

        /// <summary>
        /// Подбирает линию тренда по точкам (X, Y). Null — точек мало или они не годятся
        /// для вида (у экспоненты Y должны быть больше нуля, у логарифма и степенной — X).
        /// Скользящее среднее строится отдельно (<see cref="MovingAverage"/>).
        /// </summary>
        public static TrendFit? Fit(ChartTrendline trendline, IReadOnlyList<(double X, double Y)> points)
        {
            var usable = points.Where(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y)
                                           && !double.IsInfinity(p.X) && !double.IsInfinity(p.Y)).ToList();
            switch (trendline.Type)
            {
                case ChartTrendlineType.Linear:
                {
                    if (usable.Count < 2) return null;
                    var (a, b) = LinearFit(usable.Select(p => p.X).ToList(), usable.Select(p => p.Y).ToList());
                    if (double.IsNaN(a) || double.IsNaN(b)) return null;
                    double r2 = RSquared(usable.Select(p => p.Y).ToList(), usable.Select(p => a + b * p.X).ToList());
                    return new TrendFit(x => a + b * x, "y = " + Term(b, "x", first: true) + Constant(a), r2);
                }

                case ChartTrendlineType.Exponential:
                {
                    var fit = usable.Where(p => p.Y > 0).ToList();
                    if (fit.Count < 2) return null;
                    var logs = fit.Select(p => Math.Log(p.Y)).ToList();
                    var (lnA, b) = LinearFit(fit.Select(p => p.X).ToList(), logs);
                    if (double.IsNaN(lnA) || double.IsNaN(b)) return null;
                    double a = Math.Exp(lnA);
                    double r2 = RSquared(logs, fit.Select(p => lnA + b * p.X).ToList());
                    return new TrendFit(x => a * Math.Exp(b * x), "y = " + Number(a) + "e^" + Number(b) + "x", r2);
                }

                case ChartTrendlineType.Logarithmic:
                {
                    var fit = usable.Where(p => p.X > 0).ToList();
                    if (fit.Count < 2) return null;
                    var logs = fit.Select(p => Math.Log(p.X)).ToList();
                    var (a, b) = LinearFit(logs, fit.Select(p => p.Y).ToList());
                    if (double.IsNaN(a) || double.IsNaN(b)) return null;
                    double r2 = RSquared(fit.Select(p => p.Y).ToList(), logs.Select(l => a + b * l).ToList());
                    return new TrendFit(x => x > 0 ? a + b * Math.Log(x) : double.NaN, "y = " + Term(b, "ln(x)", first: true) + Constant(a), r2);
                }

                case ChartTrendlineType.Power:
                {
                    var fit = usable.Where(p => p.X > 0 && p.Y > 0).ToList();
                    if (fit.Count < 2) return null;
                    var logX = fit.Select(p => Math.Log(p.X)).ToList();
                    var logY = fit.Select(p => Math.Log(p.Y)).ToList();
                    var (lnA, b) = LinearFit(logX, logY);
                    if (double.IsNaN(lnA) || double.IsNaN(b)) return null;
                    double a = Math.Exp(lnA);
                    double r2 = RSquared(logY, logX.Select(l => lnA + b * l).ToList());
                    return new TrendFit(x => x > 0 ? a * Math.Pow(x, b) : double.NaN, "y = " + Number(a) + "x^" + Number(b), r2);
                }

                case ChartTrendlineType.Polynomial:
                {
                    int order = Math.Clamp(trendline.Order, 2, 6);
                    if (usable.Count <= order) return null;
                    var coefficients = PolynomialFit(usable.Select(p => p.X).ToList(), usable.Select(p => p.Y).ToList(), order);
                    if (coefficients is null) return null;
                    double Evaluate(double x)
                    {
                        double y = 0, power = 1;
                        foreach (double c in coefficients) { y += c * power; power *= x; }
                        return y;
                    }
                    double r2 = RSquared(usable.Select(p => p.Y).ToList(), usable.Select(p => Evaluate(p.X)).ToList());

                    var equation = new System.Text.StringBuilder("y = ");
                    bool first = true;
                    for (int k = order; k >= 1; k--)
                    {
                        if (Math.Abs(coefficients[k]) < 1e-12) continue;
                        equation.Append(Term(coefficients[k], k == 1 ? "x" : "x" + Superscript(k), first));
                        first = false;
                    }
                    equation.Append(first ? Number(coefficients[0]) : Constant(coefficients[0]));
                    return new TrendFit(Evaluate, equation.ToString(), r2);
                }

                default:
                    return null;
            }
        }

        /// <summary>Скользящее среднее: точка на каждый X начиная с period-й, как у Excel.</summary>
        public static List<(double X, double Y)> MovingAverage(IReadOnlyList<(double X, double Y)> points, int period)
        {
            period = Math.Max(2, period);
            var result = new List<(double X, double Y)>();
            for (int i = period - 1; i < points.Count; i++)
            {
                double sum = 0;
                int count = 0;
                for (int k = i - period + 1; k <= i; k++)
                {
                    if (double.IsNaN(points[k].Y)) continue;
                    sum += points[k].Y;
                    count++;
                }
                if (count > 0) result.Add((points[i].X, sum / count));
            }
            return result;
        }

        /// <summary>
        /// Планка погрешности точки: середина и величина в обе стороны. У стандартного
        /// отклонения середина — среднее ряда, как у Excel; у прочих — само значение.
        /// </summary>
        public static (double Center, double Amount) ErrorBar(ChartErrorBars bars, IReadOnlyList<double> seriesValues, double value)
        {
            switch (bars.Type)
            {
                case ChartErrorBarType.Percentage:
                    return (value, Math.Abs(value) * Math.Max(0, bars.Value) / 100.0);

                case ChartErrorBarType.FixedValue:
                    return (value, Math.Abs(bars.Value));

                case ChartErrorBarType.StandardDeviation:
                {
                    var (mean, deviation) = MeanAndDeviation(seriesValues);
                    return (mean, deviation * Math.Max(0, bars.Value));
                }

                default:
                {
                    var (_, deviation) = MeanAndDeviation(seriesValues);
                    int n = seriesValues.Count;
                    return (value, n > 0 ? deviation / Math.Sqrt(n) : 0);
                }
            }
        }

        private static (double Mean, double Deviation) MeanAndDeviation(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return (0, 0);
            double mean = values.Average();
            if (values.Count < 2) return (mean, 0);
            double sum = 0;
            foreach (double v in values) sum += (v - mean) * (v - mean);
            return (mean, Math.Sqrt(sum / (values.Count - 1)));
        }

        private static (double A, double B) LinearFit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        {
            int n = xs.Count;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                sx += xs[i]; sy += ys[i];
                sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i];
            }
            double denominator = n * sxx - sx * sx;
            if (Math.Abs(denominator) < 1e-12) return (double.NaN, double.NaN);
            double b = (n * sxy - sx * sy) / denominator;
            double a = (sy - b * sx) / n;
            return (a, b);
        }

        /// <summary>Коэффициенты многочлена от свободного члена к старшему; null — система вырождена.</summary>
        private static double[]? PolynomialFit(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int order)
        {
            int size = order + 1;
            var matrix = new double[size, size + 1];
            for (int i = 0; i < xs.Count; i++)
            {
                var powers = new double[2 * order + 1];
                powers[0] = 1;
                for (int k = 1; k < powers.Length; k++) powers[k] = powers[k - 1] * xs[i];
                for (int row = 0; row < size; row++)
                {
                    for (int column = 0; column < size; column++) matrix[row, column] += powers[row + column];
                    matrix[row, size] += powers[row] * ys[i];
                }
            }

            // Метод Гаусса с выбором главного элемента.
            for (int column = 0; column < size; column++)
            {
                int pivot = column;
                for (int row = column + 1; row < size; row++)
                    if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column])) pivot = row;
                if (Math.Abs(matrix[pivot, column]) < 1e-12) return null;
                if (pivot != column)
                    for (int k = 0; k <= size; k++) (matrix[column, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[column, k]);

                for (int row = 0; row < size; row++)
                {
                    if (row == column) continue;
                    double factor = matrix[row, column] / matrix[column, column];
                    for (int k = column; k <= size; k++) matrix[row, k] -= factor * matrix[column, k];
                }
            }

            var result = new double[size];
            for (int i = 0; i < size; i++) result[i] = matrix[i, size] / matrix[i, i];
            return result;
        }

        private static double RSquared(IReadOnlyList<double> actual, IReadOnlyList<double> predicted)
        {
            if (actual.Count == 0) return 0;
            double mean = actual.Average();
            double total = 0, residual = 0;
            for (int i = 0; i < actual.Count; i++)
            {
                total += (actual[i] - mean) * (actual[i] - mean);
                residual += (actual[i] - predicted[i]) * (actual[i] - predicted[i]);
            }
            return total <= 0 ? 1 : 1 - residual / total;
        }

        private static string Number(double value)
            => value.ToString(Math.Abs(value) >= 1000 || (Math.Abs(value) < 0.001 && value != 0) ? "0.###E+0" : "0.####", CultureInfo.CurrentCulture);

        private static string Term(double coefficient, string variable, bool first)
        {
            string number = Number(Math.Abs(coefficient));
            string sign = coefficient < 0 ? (first ? "-" : " - ") : (first ? string.Empty : " + ");
            return sign + number + variable;
        }

        private static string Constant(double value)
        {
            if (Math.Abs(value) < 1e-12) return string.Empty;
            return (value < 0 ? " - " : " + ") + Number(Math.Abs(value));
        }

        private static string Superscript(int power) => power switch
        {
            2 => "²",
            3 => "³",
            4 => "⁴",
            5 => "⁵",
            6 => "⁶",
            _ => "^" + power.ToString(CultureInfo.InvariantCulture)
        };

        /// <summary>Подпись R², как у Excel: «R² = 0,9876».</summary>
        public static string RSquaredText(double value) => "R² = " + value.ToString("0.####", CultureInfo.CurrentCulture);
    }
}
