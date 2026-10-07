using System;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Декодер JPEG с четырьмя компонентами: CMYK и YCCK (Adobe).
    ///
    /// Системный декодер листа такие файлы переводит в RGB сам и наивно — краска
    /// просто вычитается из белого, — а чёрный канал при этом растворяется в трёх
    /// остальных и вернуть его уже нельзя. Цвета выходят кислотными: печатный
    /// красный (C10 M100 Y90) становится чистым (228, 0, 25), тогда как Word, переводя
    /// краску через цветовой профиль печати, показывает (208, 51, 51). Чтобы перевести
    /// так же, нужны сами доли краски — их и отдаёт этот декодер.
    ///
    /// Поддерживаются базовый, расширенный последовательный и прогрессивный JPEG с
    /// кодированием Хаффмана, 8 бит на отсчёт, любое прореживание компонент и
    /// интервалы перезапуска. Арифметическое кодирование и JPEG без потерь в
    /// документах не встречаются, для них возвращается null.
    /// </summary>
    internal sealed class CmykJpegDecoder
    {
        /// <summary>Результат: доли краски по точкам и встроенный профиль.</summary>
        public sealed class Result
        {
            public Result(int width, int height, byte[] ink, byte[]? iccProfile)
            {
                Width = width;
                Height = height;
                Ink = ink;
                IccProfile = iccProfile;
            }

            /// <summary>Ширина, точек.</summary>
            public int Width { get; }

            /// <summary>Высота, точек.</summary>
            public int Height { get; }

            /// <summary>
            /// Краска по точкам: C, M, Y, K подряд, 0 — краски нет, 255 — сплошная.
            /// </summary>
            public byte[] Ink { get; }

            /// <summary>Встроенный цветовой профиль (APP2 ICC_PROFILE) или null.</summary>
            public byte[]? IccProfile { get; }
        }

        /// <summary>Предел стороны картинки, точек: защита от испорченного файла.</summary>
        private const int MaxSidePx = 30000;

        /// <summary>
        /// Предел числа точек: краска и цвет картинки держатся в памяти целиком, по
        /// четыре байта на точку каждый.
        /// </summary>
        private const long MaxPixels = 120_000_000;

        /// <summary>Позиция в зигзаге → позиция в блоке 8×8 по строкам.</summary>
        private static readonly int[] ZigZag =
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
            12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
        };

        /// <summary>Косинусы обратного ДКП: [x * 8 + u] = C(u) / 2 · cos((2x + 1)uπ / 16).</summary>
        private static readonly float[] IdctCos = BuildIdctCos();

        private sealed class HuffmanTable
        {
            private const int LookupBits = 9;

            public readonly int[] MaxCode = new int[18];
            public readonly int[] MinCode = new int[17];
            public readonly int[] ValuePtr = new int[17];
            public readonly byte[] Values;
            public readonly byte[] LookupLength = new byte[1 << LookupBits];
            public readonly byte[] LookupValue = new byte[1 << LookupBits];

            public HuffmanTable(byte[] counts, byte[] values)
            {
                Values = values;

                int code = 0;
                int k = 0;
                for (int length = 1; length <= 16; length++)
                {
                    int count = counts[length - 1];
                    ValuePtr[length] = k;
                    MinCode[length] = code;
                    code += count;
                    k += count;
                    MaxCode[length] = count > 0 ? code - 1 : -1;
                    code <<= 1;
                }
                MaxCode[17] = int.MaxValue;

                // Быстрый просмотр: коды не длиннее LookupBits читаются одним взглядом.
                for (int length = 1; length <= LookupBits; length++)
                {
                    if (MaxCode[length] < 0) continue;

                    for (int c = MinCode[length]; c <= MaxCode[length]; c++)
                    {
                        int valueIndex = ValuePtr[length] + c - MinCode[length];
                        if (valueIndex >= Values.Length) continue;

                        int shift = LookupBits - length;
                        int first = c << shift;
                        int last = first + (1 << shift);
                        for (int e = first; e < last && e < LookupLength.Length; e++)
                        {
                            LookupLength[e] = (byte)length;
                            LookupValue[e] = Values[valueIndex];
                        }
                    }
                }
            }

            public static int LookupWidth => LookupBits;
        }

        private sealed class Component
        {
            public int Id;
            public int H;
            public int V;
            public int QuantIndex;
            public ushort[]? Quant;
            public int BlocksPerLine;
            public int BlocksPerColumn;
            public int BlocksPerLineForMcu;
            public int BlocksPerColumnForMcu;
            public short[] Coefficients = Array.Empty<short>();
            public int Pred;
            public HuffmanTable? DcTable;
            public HuffmanTable? AcTable;
            public byte[] Plane = Array.Empty<byte>();
            public int PlaneStride;
        }

        private readonly byte[] _data;
        private int _pos;
        private uint _bitBuffer;
        private int _bitCount;
        private bool _hitMarker;
        private int _eobRun;

        private readonly ushort[]?[] _quantTables = new ushort[4][];
        private readonly HuffmanTable?[] _dcTables = new HuffmanTable[4];
        private readonly HuffmanTable?[] _acTables = new HuffmanTable[4];
        private readonly List<Component> _components = new();
        private int _width;
        private int _height;
        private int _maxH = 1;
        private int _maxV = 1;
        private int _mcusPerLine;
        private int _mcusPerColumn;
        private bool _progressive;
        private int _restartInterval;
        private bool _hasAdobe;
        private int _adobeTransform;
        private readonly SortedDictionary<int, byte[]> _iccChunks = new();

        private CmykJpegDecoder(byte[] data)
        {
            _data = data;
        }

        /// <summary>
        /// JPEG с четырьмя компонентами. Определяется по заголовку кадра, без
        /// декодирования самого изображения.
        /// </summary>
        public static bool IsFourComponent(byte[] data)
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;

            int pos = 2;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] != 0xFF) { pos++; continue; }

                int marker = data[pos + 1];
                if (marker == 0xFF) { pos++; continue; }
                pos += 2;

                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (marker == 0xD9 || marker == 0xDA) return false;
                if (pos + 2 > data.Length) return false;

                int length = (data[pos] << 8) | data[pos + 1];
                if (length < 2) return false;

                bool isFrame = marker >= 0xC0 && marker <= 0xCF
                    && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (isFrame)
                    return pos + 7 < data.Length && data[pos + 7] == 4;

                pos += length;
            }

            return false;
        }

        /// <summary>
        /// Декодирует JPEG с четырьмя компонентами в доли краски. Null — файл не такой,
        /// в нём неподдерживаемое кодирование или он испорчен.
        /// </summary>
        public static Result? Decode(byte[] data)
        {
            try
            {
                var decoder = new CmykJpegDecoder(data);
                return decoder.Run();
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
            catch (OutOfMemoryException)
            {
                return null;
            }
        }

        private Result? Run()
        {
            if (_data.Length < 4 || _data[0] != 0xFF || _data[1] != 0xD8) return null;

            _pos = 2;
            bool frameSeen = false;
            bool anyScan = false;

            while (_pos < _data.Length)
            {
                if (_data[_pos] != 0xFF) { _pos++; continue; }
                if (_pos + 1 >= _data.Length) break;

                int marker = _data[_pos + 1];
                if (marker == 0xFF) { _pos++; continue; }
                _pos += 2;

                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (marker == 0xD9) break;

                if (_pos + 2 > _data.Length) break;
                int length = (_data[_pos] << 8) | _data[_pos + 1];
                if (length < 2 || _pos + length > _data.Length) return null;
                int segment = _pos + 2;
                int segmentEnd = _pos + length;

                switch (marker)
                {
                    case 0xC0:
                    case 0xC1:
                    case 0xC2:
                        if (frameSeen) return null;
                        if (!ReadFrame(segment, segmentEnd, marker == 0xC2)) return null;
                        frameSeen = true;
                        _pos = segmentEnd;
                        break;

                    case 0xC3:
                    case 0xC5:
                    case 0xC6:
                    case 0xC7:
                    case 0xC9:
                    case 0xCA:
                    case 0xCB:
                    case 0xCD:
                    case 0xCE:
                    case 0xCF:
                        // Без потерь, иерархический и арифметический — не встречаются.
                        return null;

                    case 0xC4:
                        if (!ReadHuffmanTables(segment, segmentEnd)) return null;
                        _pos = segmentEnd;
                        break;

                    case 0xDB:
                        if (!ReadQuantTables(segment, segmentEnd)) return null;
                        _pos = segmentEnd;
                        break;

                    case 0xDD:
                        _restartInterval = (_data[segment] << 8) | _data[segment + 1];
                        _pos = segmentEnd;
                        break;

                    case 0xEE:
                        ReadAdobe(segment, segmentEnd);
                        _pos = segmentEnd;
                        break;

                    case 0xE2:
                        ReadIccChunk(segment, segmentEnd);
                        _pos = segmentEnd;
                        break;

                    case 0xDA:
                        if (!frameSeen) return null;
                        if (!ReadScan(segment, segmentEnd)) return null;
                        anyScan = true;
                        break;

                    default:
                        _pos = segmentEnd;
                        break;
                }
            }

            if (!frameSeen || !anyScan || _components.Count != 4) return null;

            foreach (var component in _components)
                BuildPlane(component);

            return new Result(_width, _height, ComposeInk(), AssembleIcc());
        }

        // ── Заголовки ──────────────────────────────────────────────────────

        private bool ReadFrame(int pos, int end, bool progressive)
        {
            if (end - pos < 6) return false;
            if (_data[pos] != 8) return false;

            _height = (_data[pos + 1] << 8) | _data[pos + 2];
            _width = (_data[pos + 3] << 8) | _data[pos + 4];
            int count = _data[pos + 5];
            if (_width <= 0 || _height <= 0 || _width > MaxSidePx || _height > MaxSidePx) return false;
            if ((long)_width * _height > MaxPixels) return false;
            if (count != 4 || end - pos < 6 + count * 3) return false;

            _progressive = progressive;
            int p = pos + 6;
            for (int i = 0; i < count; i++)
            {
                var component = new Component
                {
                    Id = _data[p],
                    H = _data[p + 1] >> 4,
                    V = _data[p + 1] & 15,
                    QuantIndex = _data[p + 2] & 3
                };
                if (component.H < 1 || component.H > 4 || component.V < 1 || component.V > 4) return false;

                _components.Add(component);
                p += 3;
            }

            foreach (var component in _components)
            {
                if (component.H > _maxH) _maxH = component.H;
                if (component.V > _maxV) _maxV = component.V;
            }

            _mcusPerLine = (_width + 8 * _maxH - 1) / (8 * _maxH);
            _mcusPerColumn = (_height + 8 * _maxV - 1) / (8 * _maxV);

            foreach (var component in _components)
            {
                int componentWidth = (_width * component.H + _maxH - 1) / _maxH;
                int componentHeight = (_height * component.V + _maxV - 1) / _maxV;
                component.BlocksPerLine = (componentWidth + 7) / 8;
                component.BlocksPerColumn = (componentHeight + 7) / 8;
                component.BlocksPerLineForMcu = _mcusPerLine * component.H;
                component.BlocksPerColumnForMcu = _mcusPerColumn * component.V;

                long size = (long)component.BlocksPerLineForMcu * component.BlocksPerColumnForMcu * 64;
                if (size > int.MaxValue) return false;
                component.Coefficients = new short[size];
            }

            return true;
        }

        private bool ReadHuffmanTables(int pos, int end)
        {
            while (pos < end)
            {
                if (end - pos < 17) return false;

                int tableClass = _data[pos] >> 4;
                int tableIndex = _data[pos] & 15;
                if (tableIndex > 3 || tableClass > 1) return false;

                var counts = new byte[16];
                int total = 0;
                for (int i = 0; i < 16; i++)
                {
                    counts[i] = _data[pos + 1 + i];
                    total += counts[i];
                }
                pos += 17;
                if (end - pos < total || total > 256) return false;

                var values = new byte[total];
                Array.Copy(_data, pos, values, 0, total);
                pos += total;

                var table = new HuffmanTable(counts, values);
                if (tableClass == 0) _dcTables[tableIndex] = table;
                else _acTables[tableIndex] = table;
            }

            return true;
        }

        private bool ReadQuantTables(int pos, int end)
        {
            while (pos < end)
            {
                int precision = _data[pos] >> 4;
                int index = _data[pos] & 15;
                if (index > 3) return false;
                pos++;

                var table = new ushort[64];
                for (int k = 0; k < 64; k++)
                {
                    int value;
                    if (precision == 0)
                    {
                        if (pos >= end) return false;
                        value = _data[pos++];
                    }
                    else
                    {
                        if (pos + 1 >= end) return false;
                        value = (_data[pos] << 8) | _data[pos + 1];
                        pos += 2;
                    }
                    table[ZigZag[k]] = (ushort)value;
                }

                _quantTables[index] = table;
            }

            return true;
        }

        private void ReadAdobe(int pos, int end)
        {
            if (end - pos < 12) return;
            if (_data[pos] != (byte)'A' || _data[pos + 1] != (byte)'d' || _data[pos + 2] != (byte)'o'
                || _data[pos + 3] != (byte)'b' || _data[pos + 4] != (byte)'e')
                return;

            _hasAdobe = true;
            _adobeTransform = _data[pos + 11];
        }

        private void ReadIccChunk(int pos, int end)
        {
            const string signature = "ICC_PROFILE";
            if (end - pos < signature.Length + 3) return;

            for (int i = 0; i < signature.Length; i++)
                if (_data[pos + i] != (byte)signature[i]) return;
            if (_data[pos + signature.Length] != 0) return;

            int sequence = _data[pos + signature.Length + 1];
            int start = pos + signature.Length + 3;

            var chunk = new byte[end - start];
            Array.Copy(_data, start, chunk, 0, chunk.Length);
            _iccChunks[sequence] = chunk;
        }

        private byte[]? AssembleIcc()
        {
            if (_iccChunks.Count == 0) return null;

            int total = 0;
            foreach (var chunk in _iccChunks.Values) total += chunk.Length;

            var profile = new byte[total];
            int offset = 0;
            foreach (var chunk in _iccChunks.Values)
            {
                Array.Copy(chunk, 0, profile, offset, chunk.Length);
                offset += chunk.Length;
            }

            return profile;
        }

        // ── Скан ───────────────────────────────────────────────────────────

        private bool ReadScan(int pos, int end)
        {
            int count = _data[pos];
            if (count < 1 || count > 4 || end - pos < 1 + count * 2 + 3) return false;

            var scanComponents = new List<Component>(count);
            int p = pos + 1;
            for (int i = 0; i < count; i++)
            {
                int id = _data[p];
                int tables = _data[p + 1];
                p += 2;

                Component? component = null;
                foreach (var candidate in _components)
                {
                    if (candidate.Id == id) { component = candidate; break; }
                }
                if (component is null) return false;

                component.DcTable = _dcTables[tables >> 4];
                component.AcTable = _acTables[tables & 15];

                // Таблица квантования закрепляется за компонентой на её первом скане,
                // как у libjpeg: переопределение таблицы позже её уже не меняет.
                component.Quant ??= _quantTables[component.QuantIndex];

                scanComponents.Add(component);
            }

            int spectralStart = _data[p];
            int spectralEnd = _data[p + 1];
            int successiveHigh = _data[p + 2] >> 4;
            int successiveLow = _data[p + 2] & 15;

            if (!_progressive)
            {
                spectralStart = 0;
                spectralEnd = 63;
                successiveHigh = 0;
                successiveLow = 0;
            }
            if (spectralEnd > 63 || spectralStart > spectralEnd) return false;

            _pos = end;
            ResetBits();
            _eobRun = 0;
            foreach (var component in scanComponents) component.Pred = 0;

            DecodeScan(scanComponents, spectralStart, spectralEnd, successiveHigh, successiveLow);

            SkipToMarker();
            return true;
        }

        private void DecodeScan(
            List<Component> scanComponents, int spectralStart, int spectralEnd,
            int successiveHigh, int successiveLow)
        {
            bool single = scanComponents.Count == 1;
            int total = single
                ? scanComponents[0].BlocksPerLine * scanComponents[0].BlocksPerColumn
                : _mcusPerLine * _mcusPerColumn;

            int restartLeft = _restartInterval;

            for (int mcu = 0; mcu < total; mcu++)
            {
                if (_restartInterval > 0 && restartLeft == 0)
                {
                    ProcessRestart();
                    foreach (var component in scanComponents) component.Pred = 0;
                    _eobRun = 0;
                    restartLeft = _restartInterval;
                }

                if (single)
                {
                    var component = scanComponents[0];
                    int row = mcu / component.BlocksPerLine;
                    int col = mcu % component.BlocksPerLine;
                    DecodeBlock(component, row, col, spectralStart, spectralEnd, successiveHigh, successiveLow);
                }
                else
                {
                    int mcuRow = mcu / _mcusPerLine;
                    int mcuCol = mcu % _mcusPerLine;
                    foreach (var component in scanComponents)
                    {
                        for (int v = 0; v < component.V; v++)
                        {
                            for (int h = 0; h < component.H; h++)
                            {
                                DecodeBlock(component,
                                    mcuRow * component.V + v, mcuCol * component.H + h,
                                    spectralStart, spectralEnd, successiveHigh, successiveLow);
                            }
                        }
                    }
                }

                if (_restartInterval > 0) restartLeft--;
            }
        }

        private void DecodeBlock(
            Component component, int row, int col,
            int spectralStart, int spectralEnd, int successiveHigh, int successiveLow)
        {
            if (row >= component.BlocksPerColumnForMcu || col >= component.BlocksPerLineForMcu) return;

            int offset = (row * component.BlocksPerLineForMcu + col) * 64;
            var coefficients = component.Coefficients;

            if (!_progressive)
            {
                DecodeBaseline(component, coefficients, offset);
                return;
            }

            if (spectralStart == 0)
            {
                if (successiveHigh == 0)
                {
                    int t = DecodeHuffman(component.DcTable);
                    int diff = t == 0 ? 0 : ReceiveExtend(t);
                    component.Pred += diff;
                    coefficients[offset] = (short)(component.Pred << successiveLow);
                }
                else if (ReadBit() != 0)
                {
                    coefficients[offset] |= (short)(1 << successiveLow);
                }
                return;
            }

            if (successiveHigh == 0)
                DecodeAcFirst(component, coefficients, offset, spectralStart, spectralEnd, successiveLow);
            else
                DecodeAcRefine(component, coefficients, offset, spectralStart, spectralEnd, successiveLow);
        }

        private void DecodeBaseline(Component component, short[] coefficients, int offset)
        {
            int t = DecodeHuffman(component.DcTable);
            int diff = t == 0 ? 0 : ReceiveExtend(t);
            component.Pred += diff;
            coefficients[offset] = (short)component.Pred;

            int k = 1;
            while (k < 64)
            {
                int rs = DecodeHuffman(component.AcTable);
                int s = rs & 15;
                int r = rs >> 4;

                if (s == 0)
                {
                    if (r < 15) break;
                    k += 16;
                    continue;
                }

                k += r;
                if (k > 63) break;
                coefficients[offset + ZigZag[k]] = (short)ReceiveExtend(s);
                k++;
            }
        }

        private void DecodeAcFirst(
            Component component, short[] coefficients, int offset,
            int spectralStart, int spectralEnd, int successiveLow)
        {
            if (_eobRun > 0)
            {
                _eobRun--;
                return;
            }

            int k = spectralStart;
            while (k <= spectralEnd)
            {
                int rs = DecodeHuffman(component.AcTable);
                int s = rs & 15;
                int r = rs >> 4;

                if (s == 0)
                {
                    if (r < 15)
                    {
                        _eobRun = (1 << r) - 1;
                        if (r > 0) _eobRun += GetBits(r);
                        break;
                    }
                    k += 16;
                    continue;
                }

                k += r;
                if (k > 63) break;
                coefficients[offset + ZigZag[k]] = (short)(ReceiveExtend(s) * (1 << successiveLow));
                k++;
            }
        }

        private void DecodeAcRefine(
            Component component, short[] coefficients, int offset,
            int spectralStart, int spectralEnd, int successiveLow)
        {
            int p1 = 1 << successiveLow;
            int m1 = -1 << successiveLow;
            int k = spectralStart;

            if (_eobRun == 0)
            {
                for (; k <= spectralEnd; k++)
                {
                    int rs = DecodeHuffman(component.AcTable);
                    int r = rs >> 4;
                    int s = rs & 15;
                    int value = 0;

                    if (s != 0)
                    {
                        value = ReadBit() != 0 ? p1 : m1;
                    }
                    else if (r != 15)
                    {
                        _eobRun = 1 << r;
                        if (r > 0) _eobRun += GetBits(r);
                        break;
                    }

                    // Пропуск r нулевых коэффициентов с уточнением уже ненулевых по пути.
                    do
                    {
                        int index = offset + ZigZag[k];
                        int current = coefficients[index];
                        if (current != 0)
                        {
                            if (ReadBit() != 0 && (current & p1) == 0)
                                coefficients[index] = (short)(current >= 0 ? current + p1 : current + m1);
                        }
                        else
                        {
                            if (--r < 0) break;
                        }
                        k++;
                    }
                    while (k <= spectralEnd);

                    if (value != 0 && k <= 63)
                        coefficients[offset + ZigZag[k]] = (short)value;
                }
            }

            if (_eobRun > 0)
            {
                for (; k <= spectralEnd; k++)
                {
                    int index = offset + ZigZag[k];
                    int current = coefficients[index];
                    if (current == 0) continue;

                    if (ReadBit() != 0 && (current & p1) == 0)
                        coefficients[index] = (short)(current >= 0 ? current + p1 : current + m1);
                }
                _eobRun--;
            }
        }

        // ── Биты ───────────────────────────────────────────────────────────

        private void ResetBits()
        {
            _bitBuffer = 0;
            _bitCount = 0;
            _hitMarker = false;
        }

        private void FillBits()
        {
            while (_bitCount <= 24)
            {
                int value = 0;
                if (!_hitMarker && _pos < _data.Length)
                {
                    value = _data[_pos];
                    if (value == 0xFF)
                    {
                        int next = _pos + 1 < _data.Length ? _data[_pos + 1] : 0xD9;
                        if (next == 0x00)
                        {
                            _pos += 2;
                        }
                        else
                        {
                            // Маркер: данные скана кончились, дальше идут нули.
                            _hitMarker = true;
                            value = 0;
                        }
                    }
                    else
                    {
                        _pos++;
                    }
                }

                _bitBuffer |= (uint)value << (24 - _bitCount);
                _bitCount += 8;
            }
        }

        private int GetBits(int count)
        {
            if (count == 0) return 0;

            FillBits();
            int value = (int)(_bitBuffer >> (32 - count));
            _bitBuffer <<= count;
            _bitCount -= count;
            return value;
        }

        private int ReadBit() => GetBits(1);

        private int ReceiveExtend(int size)
        {
            if (size == 0) return 0;
            if (size > 16) return 0;

            int value = GetBits(size);
            return value < (1 << (size - 1)) ? value - (1 << size) + 1 : value;
        }

        private int DecodeHuffman(HuffmanTable? table)
        {
            if (table is null) return 0;

            FillBits();

            int lookupWidth = HuffmanTable.LookupWidth;
            int peek = (int)(_bitBuffer >> (32 - lookupWidth));
            int length = table.LookupLength[peek];
            if (length != 0)
            {
                _bitBuffer <<= length;
                _bitCount -= length;
                return table.LookupValue[peek];
            }

            for (length = lookupWidth + 1; length <= 16; length++)
            {
                int code = (int)(_bitBuffer >> (32 - length));
                if (code <= table.MaxCode[length])
                {
                    _bitBuffer <<= length;
                    _bitCount -= length;

                    int valueIndex = table.ValuePtr[length] + code - table.MinCode[length];
                    return valueIndex >= 0 && valueIndex < table.Values.Length ? table.Values[valueIndex] : 0;
                }
            }

            // Испорченный код: пропускаем его длину, чтобы не зациклиться.
            _bitBuffer <<= 16;
            _bitCount -= 16;
            return 0;
        }

        private void ProcessRestart()
        {
            ResetBits();

            while (_pos + 1 < _data.Length)
            {
                if (_data[_pos] == 0xFF && _data[_pos + 1] >= 0xD0 && _data[_pos + 1] <= 0xD7)
                {
                    _pos += 2;
                    return;
                }
                _pos++;
            }
        }

        private void SkipToMarker()
        {
            while (_pos + 1 < _data.Length)
            {
                if (_data[_pos] == 0xFF)
                {
                    int next = _data[_pos + 1];
                    if (next != 0x00 && next != 0xFF && (next < 0xD0 || next > 0xD7)) return;
                }
                _pos++;
            }

            _pos = _data.Length;
        }

        // ── Отсчёты ────────────────────────────────────────────────────────

        private static float[] BuildIdctCos()
        {
            var table = new float[64];
            for (int x = 0; x < 8; x++)
            {
                for (int u = 0; u < 8; u++)
                {
                    double cu = u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                    table[x * 8 + u] = (float)(cu / 2.0 * Math.Cos((2 * x + 1) * u * Math.PI / 16.0));
                }
            }
            return table;
        }

        private void BuildPlane(Component component)
        {
            component.PlaneStride = component.BlocksPerLineForMcu * 8;
            int planeHeight = component.BlocksPerColumnForMcu * 8;
            component.Plane = new byte[component.PlaneStride * planeHeight];

            var quant = component.Quant ?? _quantTables[component.QuantIndex] ?? new ushort[64];
            var input = new float[64];
            var rows = new float[64];

            for (int blockRow = 0; blockRow < component.BlocksPerColumnForMcu; blockRow++)
            {
                for (int blockCol = 0; blockCol < component.BlocksPerLineForMcu; blockCol++)
                {
                    int offset = (blockRow * component.BlocksPerLineForMcu + blockCol) * 64;
                    for (int i = 0; i < 64; i++)
                        input[i] = component.Coefficients[offset + i] * (float)quant[i];

                    // По строкам частот: rows[v][x] = Σu cos[x][u] · F[v][u].
                    for (int v = 0; v < 8; v++)
                    {
                        for (int x = 0; x < 8; x++)
                        {
                            float sum = 0f;
                            for (int u = 0; u < 8; u++)
                                sum += IdctCos[x * 8 + u] * input[v * 8 + u];
                            rows[v * 8 + x] = sum;
                        }
                    }

                    // По столбцам: out[y][x] = Σv cos[y][v] · rows[v][x].
                    int planeY = blockRow * 8;
                    int planeX = blockCol * 8;
                    for (int y = 0; y < 8; y++)
                    {
                        int line = (planeY + y) * component.PlaneStride + planeX;
                        for (int x = 0; x < 8; x++)
                        {
                            float sum = 0f;
                            for (int v = 0; v < 8; v++)
                                sum += IdctCos[y * 8 + v] * rows[v * 8 + x];

                            int sample = (int)Math.Round(sum + 128f);
                            component.Plane[line + x] = (byte)Math.Clamp(sample, 0, 255);
                        }
                    }
                }
            }

            // Коэффициенты больше не нужны: картинка может быть крупной.
            component.Coefficients = Array.Empty<short>();
        }

        private byte[] ComposeInk()
        {
            var ink = new byte[(long)_width * _height * 4];

            // Adobe пишет CMYK обращённым: 255 — краски нет. YCCK у него — те же
            // обращённые C, M, Y, переведённые в яркость и цветность, и обращённый K.
            bool ycck = _hasAdobe && _adobeTransform == 2;
            bool inverted = _hasAdobe;

            var p0 = Upsample(_components[0]);
            var p1 = Upsample(_components[1]);
            var p2 = Upsample(_components[2]);
            var p3 = Upsample(_components[3]);

            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    long at = (long)y * _width + x;
                    int s0 = p0[at];
                    int s1 = p1[at];
                    int s2 = p2[at];
                    int s3 = p3[at];

                    int c, m, yy, k;
                    if (ycck)
                    {
                        // Яркость и цветность → обращённые C, M, Y (как R, G, B), а
                        // обращённое обращение даёт краску напрямую.
                        float luma = s0;
                        float cb = s1 - 128f;
                        float cr = s2 - 128f;
                        c = ClampByte(luma + 1.402f * cr);
                        m = ClampByte(luma - 0.344136f * cb - 0.714136f * cr);
                        yy = ClampByte(luma + 1.772f * cb);
                        k = 255 - s3;
                    }
                    else if (inverted)
                    {
                        c = 255 - s0;
                        m = 255 - s1;
                        yy = 255 - s2;
                        k = 255 - s3;
                    }
                    else
                    {
                        c = s0;
                        m = s1;
                        yy = s2;
                        k = s3;
                    }

                    long o = ((long)y * _width + x) * 4;
                    ink[o] = (byte)c;
                    ink[o + 1] = (byte)m;
                    ink[o + 2] = (byte)yy;
                    ink[o + 3] = (byte)k;
                }
            }

            return ink;
        }

        /// <summary>
        /// Компонента в полном размере картинки. Прореживание вдвое по горизонтали,
        /// по вертикали и по обеим осям восстанавливается треугольным фильтром — так
        /// же, как это делает libjpeg (fancy upsampling), которым читают JPEG и Word, и
        /// большинство программ: повтором точек края цветных пятен выходили бы
        /// ступеньками. Прочие кратности — повтором, как у libjpeg.
        /// </summary>
        private byte[] Upsample(Component component)
        {
            int hRatio = _maxH / component.H;
            int vRatio = _maxV / component.V;
            bool exact = hRatio * component.H == _maxH && vRatio * component.V == _maxV;

            int sourceWidth = (_width * component.H + _maxH - 1) / _maxH;
            int sourceHeight = (_height * component.V + _maxV - 1) / _maxV;
            var plane = component.Plane;
            int stride = component.PlaneStride;

            var full = new byte[(long)_width * _height];

            if (exact && hRatio == 1 && vRatio == 1)
            {
                for (int y = 0; y < _height; y++)
                    Array.Copy(plane, y * stride, full, (long)y * _width, _width);
                return full;
            }

            bool fancy = exact && (hRatio == 2 || hRatio == 1) && (vRatio == 2 || vRatio == 1);
            if (!fancy)
            {
                for (int y = 0; y < _height; y++)
                {
                    int sy = Math.Min(y * component.V / _maxV, sourceHeight - 1);
                    for (int x = 0; x < _width; x++)
                    {
                        int sx = Math.Min(x * component.H / _maxH, sourceWidth - 1);
                        full[(long)y * _width + x] = plane[sy * stride + sx];
                    }
                }
                return full;
            }

            // Вертикаль: строка выхода — 3/4 своей строки и 1/4 соседней (верхней для
            // верхней строки пары, нижней — для нижней); у края соседняя — она же.
            // Горизонталь — так же по столбцам. Округление и смещения — как в libjpeg.
            var columnSums = new int[sourceWidth];
            for (int y = 0; y < _height; y++)
            {
                int sourceRow;
                int neighbourRow;
                int weightSelf;
                int weightNeighbour;
                if (vRatio == 2)
                {
                    sourceRow = Math.Min(y / 2, sourceHeight - 1);
                    neighbourRow = (y & 1) == 0
                        ? Math.Max(sourceRow - 1, 0)
                        : Math.Min(sourceRow + 1, sourceHeight - 1);
                    weightSelf = 3;
                    weightNeighbour = 1;
                }
                else
                {
                    sourceRow = Math.Min(y, sourceHeight - 1);
                    neighbourRow = sourceRow;
                    weightSelf = 4;
                    weightNeighbour = 0;
                }

                int rowSelf = sourceRow * stride;
                int rowNeighbour = neighbourRow * stride;
                for (int i = 0; i < sourceWidth; i++)
                    columnSums[i] = weightSelf * plane[rowSelf + i] + weightNeighbour * plane[rowNeighbour + i];

                long line = (long)y * _width;

                if (hRatio == 1)
                {
                    // Только вертикаль: сумма уже в четвертях.
                    int bias = (y & 1) == 0 ? 1 : 2;
                    for (int x = 0; x < _width; x++)
                    {
                        int i = Math.Min(x, sourceWidth - 1);
                        full[line + x] = vRatio == 2
                            ? (byte)((columnSums[i] + bias) >> 2)
                            : plane[rowSelf + i];
                    }
                    continue;
                }

                // Вдвое по горизонтали. Без вертикали сумма столбца — в четвертях,
                // с вертикалью — в шестнадцатых: сводим к одной формуле libjpeg.
                for (int x = 0; x < _width; x++)
                {
                    int i = Math.Min(x / 2, sourceWidth - 1);
                    bool left = (x & 1) == 0;
                    int self = columnSums[i];
                    int value;

                    if (vRatio == 2)
                    {
                        if (left)
                            value = i == 0 ? (self * 4 + 8) >> 4 : (self * 3 + columnSums[i - 1] + 8) >> 4;
                        else
                            value = i == sourceWidth - 1 ? (self * 4 + 7) >> 4 : (self * 3 + columnSums[i + 1] + 7) >> 4;
                    }
                    else
                    {
                        int sample = self >> 2;
                        if (left)
                            value = i == 0 ? sample : (sample * 3 + (columnSums[i - 1] >> 2) + 1) >> 2;
                        else
                            value = i == sourceWidth - 1 ? sample : (sample * 3 + (columnSums[i + 1] >> 2) + 2) >> 2;
                    }

                    full[line + x] = (byte)Math.Clamp(value, 0, 255);
                }
            }

            return full;
        }

        private static int ClampByte(float value)
        {
            int rounded = (int)Math.Round(value);
            return rounded < 0 ? 0 : rounded > 255 ? 255 : rounded;
        }
    }
}
