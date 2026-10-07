using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Цветовой профиль печати (ICC, пространство CMYK): перевод долей краски в sRGB.
    ///
    /// Читается таблица профиля «устройство → пространство связи» (AToB) в любом из
    /// трёх видов, которые бывают у CMYK-профилей: 16-битная и 8-битная таблица ICC v2
    /// (mft2, mft1) и таблица ICC v4 (mAB). Пространство связи — Lab или XYZ при
    /// белом D50; из него цвет переводится в sRGB с хроматической адаптацией к D65
    /// (матрица Брэдфорда), как это делают системы управления цветом.
    ///
    /// Сам профиль вычисляется один раз на сетке 17⁴ узлов; точки картинки берутся
    /// интерполяцией по сетке — тот же приём, которым ускоряет перевод LittleCMS.
    /// </summary>
    internal sealed class CmykColorProfile
    {
        /// <summary>Узлов сетки по каждой краске.</summary>
        private const int GridPoints = 17;

        private enum PcsEncoding
        {
            /// <summary>Lab ICC v2, 16 бит: L 0…100 ↔ 0…0xFF00, a и b −128…128 ↔ 0…0xFFFF.</summary>
            LabLegacy16,

            /// <summary>Lab, 8 бит или ICC v4: L 0…100 ↔ 0…1, a и b −128…127 ↔ 0…1.</summary>
            LabNormalized,

            /// <summary>XYZ, u1Fixed15: 0…1,99997 ↔ 0…0xFFFF.</summary>
            Xyz
        }

        /// <summary>Шаг преобразования: доли входа 0…1 → доли выхода 0…1.</summary>
        private interface IStage
        {
            float[] Evaluate(float[] input);
        }

        private readonly float[] _grid;

        private CmykColorProfile(float[] grid)
        {
            _grid = grid;
        }

        /// <summary>
        /// Разбирает профиль. Null — это не CMYK-профиль, в нём нет таблицы AToB или
        /// она устроена так, как профили печати не устраивают.
        /// </summary>
        public static CmykColorProfile? Parse(byte[]? icc)
        {
            if (icc is null || icc.Length < 132) return null;

            try
            {
                if (ReadSignature(icc, 16) != "CMYK") return null;

                string pcs = ReadSignature(icc, 20);
                if (pcs != "Lab " && pcs != "XYZ ") return null;
                bool pcsIsLab = pcs == "Lab ";

                var tags = ReadTags(icc);
                uint intent = ReadUInt32(icc, 64);

                // Таблица по способу перевода, записанному в профиле: восприятие
                // (A2B0), колориметрия (A2B1, абсолютная — тоже по ней), насыщенность
                // (A2B2). Нет нужной — берётся восприятие: она обязательна.
                string preferred = intent switch
                {
                    1 or 3 => "A2B1",
                    2 => "A2B2",
                    _ => "A2B0"
                };
                if (!tags.TryGetValue(preferred, out var tag) && !tags.TryGetValue("A2B0", out tag))
                    return null;

                var (stage, encoding) = ReadLut(icc, tag.Offset, tag.Size, pcsIsLab);
                if (stage is null) return null;

                return new CmykColorProfile(BuildGrid(stage, encoding));
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        /// <summary>
        /// Переводит краску всех точек в sRGB. ink — C, M, Y, K подряд (0 — краски нет,
        /// 255 — сплошная); rgba получает R, G, B, A подряд, непрозрачные.
        /// </summary>
        public void ConvertInk(byte[] ink, byte[] rgba)
        {
            long points = ink.Length / 4;
            var cache = new Dictionary<uint, uint>();

            for (long i = 0; i < points; i++)
            {
                long o = i * 4;
                uint key = (uint)(ink[o] << 24 | ink[o + 1] << 16 | ink[o + 2] << 8 | ink[o + 3]);

                if (!cache.TryGetValue(key, out uint packed))
                {
                    Interpolate(ink[o], ink[o + 1], ink[o + 2], ink[o + 3], out byte r, out byte g, out byte b);
                    packed = (uint)(r << 16 | g << 8 | b);

                    // Кэш держит цвета однотонных мест; на фотографии он бесполезен и
                    // разрастался бы на каждую точку.
                    if (cache.Count < 65536) cache[key] = packed;
                }

                rgba[o] = (byte)(packed >> 16);
                rgba[o + 1] = (byte)(packed >> 8);
                rgba[o + 2] = (byte)packed;
                rgba[o + 3] = 255;
            }
        }

        // ── Сетка ──────────────────────────────────────────────────────────

        private static float[] BuildGrid(IStage stage, PcsEncoding encoding)
        {
            int n = GridPoints;
            var grid = new float[n * n * n * n * 3];
            var input = new float[4];

            int index = 0;
            for (int c = 0; c < n; c++)
            {
                input[0] = c / (float)(n - 1);
                for (int m = 0; m < n; m++)
                {
                    input[1] = m / (float)(n - 1);
                    for (int y = 0; y < n; y++)
                    {
                        input[2] = y / (float)(n - 1);
                        for (int k = 0; k < n; k++)
                        {
                            input[3] = k / (float)(n - 1);

                            var pcs = stage.Evaluate(input);
                            var (r, g, b) = PcsToSrgb(pcs, encoding);
                            grid[index++] = r;
                            grid[index++] = g;
                            grid[index++] = b;
                        }
                    }
                }
            }

            return grid;
        }

        private void Interpolate(byte c, byte m, byte y, byte k, out byte r, out byte g, out byte b)
        {
            int n = GridPoints;
            float scale = (n - 1) / 255f;

            Split(c * scale, n, out int ci, out float cf);
            Split(m * scale, n, out int mi, out float mf);
            Split(y * scale, n, out int yi, out float yf);
            Split(k * scale, n, out int ki, out float kf);

            float sr = 0f, sg = 0f, sb = 0f;
            for (int corner = 0; corner < 16; corner++)
            {
                int dc = (corner >> 3) & 1;
                int dm = (corner >> 2) & 1;
                int dy = (corner >> 1) & 1;
                int dk = corner & 1;

                float weight = (dc == 1 ? cf : 1f - cf)
                             * (dm == 1 ? mf : 1f - mf)
                             * (dy == 1 ? yf : 1f - yf)
                             * (dk == 1 ? kf : 1f - kf);
                if (weight == 0f) continue;

                int node = (((ci + dc) * n + (mi + dm)) * n + (yi + dy)) * n + (ki + dk);
                int at = node * 3;
                sr += weight * _grid[at];
                sg += weight * _grid[at + 1];
                sb += weight * _grid[at + 2];
            }

            r = ToByte(sr);
            g = ToByte(sg);
            b = ToByte(sb);
        }

        private static void Split(float position, int points, out int index, out float fraction)
        {
            index = (int)position;
            if (index >= points - 1) index = points - 2;
            if (index < 0) index = 0;
            fraction = position - index;
            if (fraction < 0f) fraction = 0f;
            if (fraction > 1f) fraction = 1f;
        }

        private static byte ToByte(float value)
        {
            int rounded = (int)Math.Round(value * 255f);
            return (byte)(rounded < 0 ? 0 : rounded > 255 ? 255 : rounded);
        }

        // ── Пространство связи → sRGB ──────────────────────────────────────

        private static (float R, float G, float B) PcsToSrgb(float[] pcs, PcsEncoding encoding)
        {
            double x, y, z;

            if (encoding == PcsEncoding.Xyz)
            {
                const double scale = 65535.0 / 32768.0;
                x = pcs[0] * scale;
                y = pcs[1] * scale;
                z = pcs[2] * scale;
            }
            else
            {
                double l, a, b;
                if (encoding == PcsEncoding.LabLegacy16)
                {
                    l = pcs[0] * 65535.0 / 65280.0 * 100.0;
                    a = pcs[1] * 65535.0 / 256.0 - 128.0;
                    b = pcs[2] * 65535.0 / 256.0 - 128.0;
                }
                else
                {
                    l = pcs[0] * 100.0;
                    a = pcs[1] * 255.0 - 128.0;
                    b = pcs[2] * 255.0 - 128.0;
                }

                // Lab → XYZ при белом D50 пространства связи.
                double fy = (l + 16.0) / 116.0;
                double fx = fy + a / 500.0;
                double fz = fy - b / 200.0;
                x = 0.9642 * LabInverse(fx);
                y = 1.0 * LabInverse(fy);
                z = 0.8249 * LabInverse(fz);
            }

            // XYZ (D50) → линейный sRGB: матрица sRGB, адаптированная к D50 по Брэдфорду.
            double rl = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z;
            double gl = -0.9787684 * x + 1.9161415 * y + 0.0334540 * z;
            double bl = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z;

            return ((float)SrgbEncode(rl), (float)SrgbEncode(gl), (float)SrgbEncode(bl));
        }

        private static double LabInverse(double t)
        {
            const double delta = 6.0 / 29.0;
            return t > delta ? t * t * t : 3.0 * delta * delta * (t - 4.0 / 29.0);
        }

        private static double SrgbEncode(double linear)
        {
            if (linear <= 0.0) return 0.0;
            if (linear >= 1.0) return 1.0;
            return linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        }

        // ── Разбор профиля ─────────────────────────────────────────────────

        private static Dictionary<string, (int Offset, int Size)> ReadTags(byte[] icc)
        {
            var tags = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            uint count = ReadUInt32(icc, 128);
            if (count > 1000) return tags;

            for (int i = 0; i < count; i++)
            {
                int entry = 132 + i * 12;
                if (entry + 12 > icc.Length) break;

                string signature = ReadSignature(icc, entry);
                int offset = (int)ReadUInt32(icc, entry + 4);
                int size = (int)ReadUInt32(icc, entry + 8);
                if (offset < 0 || size < 0 || offset + (long)size > icc.Length) continue;

                tags[signature] = (offset, size);
            }

            return tags;
        }

        private static (IStage? Stage, PcsEncoding Encoding) ReadLut(byte[] icc, int offset, int size, bool pcsIsLab)
        {
            string type = ReadSignature(icc, offset);
            return type switch
            {
                "mft2" => (ReadLut16(icc, offset), pcsIsLab ? PcsEncoding.LabLegacy16 : PcsEncoding.Xyz),
                "mft1" => (ReadLut8(icc, offset), pcsIsLab ? PcsEncoding.LabNormalized : PcsEncoding.Xyz),
                "mAB " => (ReadLutAToB(icc, offset, size), pcsIsLab ? PcsEncoding.LabNormalized : PcsEncoding.Xyz),
                _ => (null, PcsEncoding.LabNormalized)
            };
        }

        private static IStage? ReadLut16(byte[] icc, int offset)
        {
            int inputs = icc[offset + 8];
            int outputs = icc[offset + 9];
            int gridPoints = icc[offset + 10];
            if (inputs != 4 || outputs != 3 || gridPoints < 2) return null;

            int inputEntries = ReadUInt16(icc, offset + 48);
            int outputEntries = ReadUInt16(icc, offset + 50);
            if (inputEntries < 2 || outputEntries < 2) return null;

            int p = offset + 52;

            var inputCurves = new ICurve[inputs];
            for (int i = 0; i < inputs; i++)
            {
                inputCurves[i] = new TableCurve(ReadUInt16Table(icc, p, inputEntries));
                p += inputEntries * 2;
            }

            var dims = new int[inputs];
            for (int i = 0; i < inputs; i++) dims[i] = gridPoints;
            int nodes = Pow(gridPoints, inputs);
            var clut = new float[nodes * outputs];
            for (int i = 0; i < clut.Length; i++)
            {
                clut[i] = ReadUInt16(icc, p) / 65535f;
                p += 2;
            }

            var outputCurves = new ICurve[outputs];
            for (int i = 0; i < outputs; i++)
            {
                outputCurves[i] = new TableCurve(ReadUInt16Table(icc, p, outputEntries));
                p += outputEntries * 2;
            }

            return new Pipeline(inputCurves, new Clut(dims, outputs, clut), outputCurves, null, null);
        }

        private static IStage? ReadLut8(byte[] icc, int offset)
        {
            int inputs = icc[offset + 8];
            int outputs = icc[offset + 9];
            int gridPoints = icc[offset + 10];
            if (inputs != 4 || outputs != 3 || gridPoints < 2) return null;

            int p = offset + 48;

            var inputCurves = new ICurve[inputs];
            for (int i = 0; i < inputs; i++)
            {
                inputCurves[i] = new TableCurve(ReadUInt8Table(icc, p, 256));
                p += 256;
            }

            var dims = new int[inputs];
            for (int i = 0; i < inputs; i++) dims[i] = gridPoints;
            int nodes = Pow(gridPoints, inputs);
            var clut = new float[nodes * outputs];
            for (int i = 0; i < clut.Length; i++)
                clut[i] = icc[p++] / 255f;

            var outputCurves = new ICurve[outputs];
            for (int i = 0; i < outputs; i++)
            {
                outputCurves[i] = new TableCurve(ReadUInt8Table(icc, p, 256));
                p += 256;
            }

            return new Pipeline(inputCurves, new Clut(dims, outputs, clut), outputCurves, null, null);
        }

        private static IStage? ReadLutAToB(byte[] icc, int offset, int size)
        {
            int inputs = icc[offset + 8];
            int outputs = icc[offset + 9];
            if (inputs != 4 || outputs != 3) return null;

            int bOffset = (int)ReadUInt32(icc, offset + 12);
            int matrixOffset = (int)ReadUInt32(icc, offset + 16);
            int mOffset = (int)ReadUInt32(icc, offset + 20);
            int clutOffset = (int)ReadUInt32(icc, offset + 24);
            int aOffset = (int)ReadUInt32(icc, offset + 28);

            ICurve[]? aCurves = aOffset != 0 ? ReadCurveSet(icc, offset + aOffset, inputs) : null;

            Clut? clut = null;
            if (clutOffset != 0)
            {
                int p = offset + clutOffset;
                var dims = new int[inputs];
                for (int i = 0; i < inputs; i++)
                {
                    dims[i] = icc[p + i];
                    if (dims[i] < 2) return null;
                }

                int precision = icc[p + 16];
                p += 20;

                int nodes = 1;
                foreach (int d in dims) nodes *= d;
                var data = new float[nodes * outputs];
                for (int i = 0; i < data.Length; i++)
                {
                    if (precision == 1)
                    {
                        data[i] = icc[p] / 255f;
                        p += 1;
                    }
                    else
                    {
                        data[i] = ReadUInt16(icc, p) / 65535f;
                        p += 2;
                    }
                }

                clut = new Clut(dims, outputs, data);
            }
            else if (inputs != outputs)
            {
                // Без таблицы число каналов не сводится: четыре краски в три величины.
                return null;
            }

            ICurve[]? mCurves = mOffset != 0 ? ReadCurveSet(icc, offset + mOffset, outputs) : null;

            double[]? matrix = null;
            if (matrixOffset != 0)
            {
                matrix = new double[12];
                for (int i = 0; i < 12; i++)
                    matrix[i] = ReadS15Fixed16(icc, offset + matrixOffset + i * 4);
            }

            ICurve[]? bCurves = bOffset != 0 ? ReadCurveSet(icc, offset + bOffset, outputs) : null;

            // Порядок AToB: кривые A → таблица → кривые M → матрица → кривые B.
            return new Pipeline(aCurves, clut, mCurves, matrix, bCurves);
        }

        private static ICurve[] ReadCurveSet(byte[] icc, int offset, int count)
        {
            var curves = new ICurve[count];
            int p = offset;
            for (int i = 0; i < count; i++)
            {
                var (curve, length) = ReadCurve(icc, p);
                curves[i] = curve;

                // Кривые выровнены по четырём байтам.
                p += (length + 3) & ~3;
            }
            return curves;
        }

        private static (ICurve Curve, int Length) ReadCurve(byte[] icc, int offset)
        {
            string type = ReadSignature(icc, offset);

            if (type == "curv")
            {
                int entries = (int)ReadUInt32(icc, offset + 8);
                int length = 12 + entries * 2;

                if (entries == 0) return (IdentityCurve.Instance, length);
                if (entries == 1) return (new GammaCurve(ReadUInt16(icc, offset + 12) / 256.0), length);

                return (new TableCurve(ReadUInt16Table(icc, offset + 12, entries)), length);
            }

            if (type == "para")
            {
                int function = ReadUInt16(icc, offset + 8);
                int parameterCount = function switch
                {
                    0 => 1,
                    1 => 3,
                    2 => 4,
                    3 => 5,
                    4 => 7,
                    _ => 0
                };
                if (parameterCount == 0) throw new ArgumentException("Неизвестная параметрическая кривая.");

                var parameters = new double[parameterCount];
                for (int i = 0; i < parameterCount; i++)
                    parameters[i] = ReadS15Fixed16(icc, offset + 12 + i * 4);

                return (new ParametricCurve(function, parameters), 12 + parameterCount * 4);
            }

            throw new ArgumentException("Неизвестный тип кривой.");
        }

        // ── Элементы преобразования ────────────────────────────────────────

        private interface ICurve
        {
            float Evaluate(float x);
        }

        private sealed class IdentityCurve : ICurve
        {
            public static readonly IdentityCurve Instance = new();

            public float Evaluate(float x) => x;
        }

        private sealed class GammaCurve : ICurve
        {
            private readonly double _gamma;

            public GammaCurve(double gamma)
            {
                _gamma = gamma;
            }

            public float Evaluate(float x) => x <= 0f ? 0f : (float)Math.Pow(x, _gamma);
        }

        private sealed class TableCurve : ICurve
        {
            private readonly float[] _table;

            public TableCurve(float[] table)
            {
                _table = table;
            }

            public float Evaluate(float x)
            {
                if (x <= 0f) return _table[0];
                if (x >= 1f) return _table[^1];

                float position = x * (_table.Length - 1);
                int index = (int)position;
                if (index >= _table.Length - 1) return _table[^1];
                float fraction = position - index;
                return _table[index] + (_table[index + 1] - _table[index]) * fraction;
            }
        }

        private sealed class ParametricCurve : ICurve
        {
            private readonly int _function;
            private readonly double[] _p;

            public ParametricCurve(int function, double[] parameters)
            {
                _function = function;
                _p = parameters;
            }

            public float Evaluate(float input)
            {
                double x = input;
                double g = _p[0];
                double result = _function switch
                {
                    0 => Math.Pow(Math.Max(x, 0.0), g),
                    1 => x >= -_p[2] / _p[1] ? Math.Pow(Math.Max(_p[1] * x + _p[2], 0.0), g) : 0.0,
                    2 => x >= -_p[2] / _p[1] ? Math.Pow(Math.Max(_p[1] * x + _p[2], 0.0), g) + _p[3] : _p[3],
                    3 => x >= _p[4] ? Math.Pow(Math.Max(_p[1] * x + _p[2], 0.0), g) : _p[3] * x,
                    _ => x >= _p[4] ? Math.Pow(Math.Max(_p[1] * x + _p[2], 0.0), g) + _p[5] : _p[3] * x + _p[6]
                };

                return (float)Math.Clamp(result, 0.0, 1.0);
            }
        }

        /// <summary>
        /// Многомерная таблица: узлы по каждому входу, выходы подряд. Первый вход
        /// меняется медленнее всех, как велит ICC. Между узлами — линейно по всем осям.
        /// </summary>
        private sealed class Clut
        {
            private readonly int[] _dims;
            private readonly int _outputs;
            private readonly float[] _data;

            public Clut(int[] dims, int outputs, float[] data)
            {
                _dims = dims;
                _outputs = outputs;
                _data = data;
            }

            public float[] Evaluate(float[] input)
            {
                int count = _dims.Length;
                var index = new int[count];
                var fraction = new float[count];

                for (int i = 0; i < count; i++)
                {
                    float x = Math.Clamp(input[i], 0f, 1f) * (_dims[i] - 1);
                    int at = (int)x;
                    if (at >= _dims[i] - 1) at = _dims[i] - 2;
                    index[i] = at;
                    fraction[i] = x - at;
                }

                var output = new float[_outputs];
                int corners = 1 << count;
                for (int corner = 0; corner < corners; corner++)
                {
                    float weight = 1f;
                    int node = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int bit = (corner >> (count - 1 - i)) & 1;
                        weight *= bit == 1 ? fraction[i] : 1f - fraction[i];
                        node = node * _dims[i] + index[i] + bit;
                    }
                    if (weight == 0f) continue;

                    int at = node * _outputs;
                    for (int o = 0; o < _outputs; o++)
                        output[o] += weight * _data[at + o];
                }

                return output;
            }
        }

        private sealed class Pipeline : IStage
        {
            private readonly ICurve[]? _aCurves;
            private readonly Clut? _clut;
            private readonly ICurve[]? _mCurves;
            private readonly double[]? _matrix;
            private readonly ICurve[]? _bCurves;

            public Pipeline(ICurve[]? aCurves, Clut? clut, ICurve[]? mCurves, double[]? matrix, ICurve[]? bCurves)
            {
                _aCurves = aCurves;
                _clut = clut;
                _mCurves = mCurves;
                _matrix = matrix;
                _bCurves = bCurves;
            }

            public float[] Evaluate(float[] input)
            {
                var values = (float[])input.Clone();

                if (_aCurves is not null)
                    for (int i = 0; i < values.Length && i < _aCurves.Length; i++)
                        values[i] = _aCurves[i].Evaluate(values[i]);

                if (_clut is not null) values = _clut.Evaluate(values);

                if (_mCurves is not null)
                    for (int i = 0; i < values.Length && i < _mCurves.Length; i++)
                        values[i] = _mCurves[i].Evaluate(values[i]);

                if (_matrix is not null && values.Length == 3)
                {
                    var m = _matrix;
                    float x = values[0], y = values[1], z = values[2];
                    values = new[]
                    {
                        (float)Math.Clamp(m[0] * x + m[1] * y + m[2] * z + m[9], 0.0, 1.0),
                        (float)Math.Clamp(m[3] * x + m[4] * y + m[5] * z + m[10], 0.0, 1.0),
                        (float)Math.Clamp(m[6] * x + m[7] * y + m[8] * z + m[11], 0.0, 1.0)
                    };
                }

                if (_bCurves is not null)
                    for (int i = 0; i < values.Length && i < _bCurves.Length; i++)
                        values[i] = _bCurves[i].Evaluate(values[i]);

                return values;
            }
        }

        // ── Чтение чисел ───────────────────────────────────────────────────

        private static string ReadSignature(byte[] data, int offset)
        {
            return new string(new[]
            {
                (char)data[offset], (char)data[offset + 1], (char)data[offset + 2], (char)data[offset + 3]
            });
        }

        private static uint ReadUInt32(byte[] data, int offset)
            => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

        private static int ReadUInt16(byte[] data, int offset)
            => data[offset] << 8 | data[offset + 1];

        private static double ReadS15Fixed16(byte[] data, int offset)
            => (int)ReadUInt32(data, offset) / 65536.0;

        private static float[] ReadUInt16Table(byte[] data, int offset, int count)
        {
            var table = new float[count];
            for (int i = 0; i < count; i++)
                table[i] = ReadUInt16(data, offset + i * 2) / 65535f;
            return table;
        }

        private static float[] ReadUInt8Table(byte[] data, int offset, int count)
        {
            var table = new float[count];
            for (int i = 0; i < count; i++)
                table[i] = data[offset + i] / 255f;
            return table;
        }

        private static int Pow(int value, int power)
        {
            int result = 1;
            for (int i = 0; i < power; i++) result *= value;
            return result;
        }
    }
}
