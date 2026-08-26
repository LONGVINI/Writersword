using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Services;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Шрифты, уложенные в проект.
    ///
    /// Шрифт хранится в рукописи именем семейства — «Garamond», — и это имя у
    /// того, кому проект передали, может не значить ничего. Скиа в таком случае
    /// молча подставит похожий, и книга откроется другой: другие пропорции,
    /// другие переносы, другое число страниц. Ни сообщения, ни следа.
    ///
    /// Поэтому файл шрифта можно уложить в архив проекта. Тогда он уезжает
    /// вместе с рукописью и подставляется вместо системного: книга выглядит
    /// так, как её сделал автор, у кого бы она ни открылась.
    ///
    /// В систему получателя при этом ничего не ставится. Файл живёт в архиве и
    /// читается на лету, только для показа: в Word такой шрифт не появится. Так
    /// и задумано — тихо менять чужую систему присланным файлом нельзя, а
    /// лицензии на шрифты обычно и прямо запрещают их установку и раздачу.
    /// Захочет получатель тот же шрифт в другой программе — есть «сохранить
    /// файл шрифта на диск», и дальше он решает сам.
    ///
    /// Имя файла в архиве — отпечаток содержимого: один шрифт ложится один раз,
    /// сколько бы раз его ни укладывали.
    /// </summary>
    public static class ProjectFonts
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(ProjectFonts));

        /// <summary>Папка шрифтов в архиве проекта. Общая, а не редакторская:
        /// шрифт может понадобиться не только рукописи.</summary>
        public const string ZipFolder = "Assets/Fonts";

        private static readonly object _lock = new();

        // Загруженные гарнитуры. Ключ — имя семейства в нижнем регистре; в
        // списке лежат все начертания, которые нашлись в архиве.
        private static readonly Dictionary<string, List<Face>> _byFamily =
            new(StringComparer.OrdinalIgnoreCase);

        // Данные шрифтов держатся живыми всё время работы: SKTypeface читает из
        // них по мере надобности, и освобождённый буфер оставил бы гарнитуру с
        // чужой памятью — падение в нативном коде без стека.
        private static readonly List<SKData> _data = new();

        private static bool _loaded;

        /// <summary>
        /// В проекте есть хоть один уложенный шрифт. Проверяется на каждом
        /// обращении к гарнитуре, поэтому не делает ничего тяжёлого.
        /// </summary>
        public static bool HasAny
        {
            get
            {
                EnsureLoaded();
                lock (_lock) return _byFamily.Count > 0;
            }
        }

        private sealed class Face
        {
            public SKTypeface Typeface { get; init; } = null!;
            public string FileName { get; init; } = string.Empty;
            public long Bytes { get; init; }
        }

        /// <summary>
        /// Забыть загруженное. Зовётся при смене проекта: шрифты одного проекта
        /// не должны подставляться в другом.
        /// </summary>
        public static void Invalidate()
        {
            lock (_lock)
            {
                foreach (var faces in _byFamily.Values)
                    foreach (var face in faces)
                        face.Typeface.Dispose();

                _byFamily.Clear();

                foreach (var data in _data) data.Dispose();
                _data.Clear();

                _loaded = false;
            }

            // Кеш отрисовщика держит гарнитуры прошлого проекта по именам
            // семейств. Оставить его значит рисовать новую рукопись чужим
            // шрифтом до первой перезагрузки программы.
            Rendering.SKTextRenderer.TrimFontCache();
        }

        private static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;

                var context = Context;
                if (context is null) return;

                try
                {
                    foreach (var path in context.GetFiles(ZipFolder).ToList())
                    {
                        var bytes = context.ReadFile(path);
                        if (bytes is null || bytes.Length == 0) continue;

                        var data = SKData.CreateCopy(bytes);
                        var typeface = SKTypeface.FromData(data);

                        if (typeface is null)
                        {
                            data.Dispose();
                            _logger.Warning("Шрифт в проекте прочитать не удалось: {Path}", path);
                            continue;
                        }

                        _data.Add(data);

                        var family = typeface.FamilyName ?? string.Empty;
                        if (!_byFamily.TryGetValue(family, out var faces))
                        {
                            faces = new List<Face>();
                            _byFamily[family] = faces;
                        }

                        faces.Add(new Face
                        {
                            Typeface = typeface,
                            FileName = Path.GetFileName(path),
                            Bytes = bytes.LongLength
                        });
                    }

                    if (_byFamily.Count > 0)
                        _logger.Debug("Шрифтов в проекте: {Count} семейств", _byFamily.Count);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Не удалось прочитать шрифты проекта");
                }
            }
        }

        /// <summary>
        /// Гарнитура уложенного в проект шрифта или null, если такого семейства
        /// в проекте нет.
        ///
        /// Возвращённая гарнитура принадлежит хранилищу и живёт до смены
        /// проекта. Освобождать её вызывающей стороне нельзя.
        /// </summary>
        public static SKTypeface? Match(string? family, SKFontStyle style)
        {
            if (string.IsNullOrWhiteSpace(family)) return null;

            EnsureLoaded();

            lock (_lock)
            {
                if (!_byFamily.TryGetValue(family!, out var faces) || faces.Count == 0)
                    return null;

                Face? best = null;
                int bestScore = int.MinValue;

                foreach (var face in faces)
                {
                    int score = StyleScore(face.Typeface.FontStyle, style);
                    if (score <= bestScore) continue;

                    best = face;
                    bestScore = score;
                }

                return best?.Typeface;
            }
        }

        /// <summary>
        /// Насколько начертание подходит запрошенному. Совпадение наклона важнее
        /// совпадения насыщенности: курсив, подменённый прямым, виден сразу, а
        /// полужирный, подменённый обычным, — гораздо меньше.
        /// </summary>
        private static int StyleScore(SKFontStyle have, SKFontStyle want)
        {
            int score = 0;

            if (have.Slant == want.Slant) score += 1000;
            score -= Math.Abs(have.Weight - want.Weight) / 10;
            score -= Math.Abs(have.Width - want.Width);

            return score;
        }

        /// <summary>Семейство уложено в проект.</summary>
        public static bool IsEmbedded(string? family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;

            EnsureLoaded();
            lock (_lock) return _byFamily.ContainsKey(family!);
        }

        /// <summary>
        /// Семейство установлено в системе.
        ///
        /// Проверяется по списку семейств, а не попыткой создать гарнитуру:
        /// SKTypeface.FromFamilyName на незнакомое имя молча отдаёт похожий
        /// шрифт, и «нашёлся» по нему значит только «что-то нашлось».
        /// </summary>
        public static bool IsInstalled(string? family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;

            try
            {
                foreach (var installed in SKFontManager.Default.FontFamilies)
                    if (string.Equals(installed, family, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Список системных шрифтов прочитать не удалось");
            }

            return false;
        }

        /// <summary>Сколько места занимают уложенные начертания семейства.</summary>
        public static long BytesOf(string? family)
        {
            if (string.IsNullOrWhiteSpace(family)) return 0;

            EnsureLoaded();
            lock (_lock)
            {
                if (!_byFamily.TryGetValue(family!, out var faces)) return 0;

                long sum = 0;
                foreach (var face in faces) sum += face.Bytes;
                return sum;
            }
        }

        /// <summary>
        /// Уложить в проект начертания системного шрифта. Возвращает число
        /// уложенных файлов.
        ///
        /// Укладываются только те начертания, которые система отдала именно для
        /// этого семейства. Подмену Скиа здесь ловить обязательно: она на любой
        /// запрос отдаёт хоть что-нибудь, и без проверки в проект лёг бы
        /// системный Segoe UI под именем ненайденного Garamond — то есть ровно
        /// та подмена, ради избавления от которой всё и делается.
        /// </summary>
        public static int Embed(string? family, IEnumerable<SKFontStyle> styles)
        {
            if (string.IsNullOrWhiteSpace(family)) return 0;

            var context = Context;
            if (context is null) return 0;

            int written = 0;

            foreach (var style in styles)
            {
                SKTypeface? typeface = null;

                try
                {
                    typeface = SKTypeface.FromFamilyName(family, style);
                    if (typeface is null) continue;

                    if (!string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Debug("Шрифт {Family} в системе не найден — уложена была бы подмена",
                            family);
                        continue;
                    }

                    using var stream = typeface.OpenStream(out _);
                    if (stream is null) continue;

                    var bytes = ReadAll(stream);
                    if (bytes.Length == 0) continue;

                    var name = StoredName(bytes);
                    var path = $"{ZipFolder}/{name}";

                    if (context.FileExists(path)) continue;

                    context.WriteFile(path, bytes);
                    written++;

                    _logger.Debug("Шрифт уложен в проект: {Family} {Style} ({Size} байт)",
                        family, style, bytes.Length);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Не удалось уложить шрифт {Family}", family);
                }
                finally
                {
                    typeface?.Dispose();
                }
            }

            if (written > 0)
            {
                context.FlushStorage();
                Invalidate();
            }

            return written;
        }

        /// <summary>
        /// Сохранить уложенный шрифт файлом на диск. Дальше человек сам решает,
        /// ставить его в систему или нет: программа за него этого не делает.
        /// Возвращает путь сохранённого файла или null.
        /// </summary>
        public static string? SaveToDisk(string? family, string directory)
        {
            if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(directory))
                return null;

            EnsureLoaded();

            var context = Context;
            if (context is null) return null;

            lock (_lock)
            {
                if (!_byFamily.TryGetValue(family!, out var faces) || faces.Count == 0)
                    return null;

                try
                {
                    Directory.CreateDirectory(directory);

                    string? first = null;

                    foreach (var face in faces)
                    {
                        var bytes = context.ReadFile($"{ZipFolder}/{face.FileName}");
                        if (bytes is null || bytes.Length == 0) continue;

                        var name = Sanitize(family!) + ExtensionOf(bytes);
                        var path = Path.Combine(directory, Unique(directory, name));

                        File.WriteAllBytes(path, bytes);
                        first ??= path;
                    }

                    return first;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Не удалось сохранить шрифт {Family} на диск", family);
                    return null;
                }
            }
        }

        // ── Мелочи ────────────────────────────────────────────────────────

        private static byte[] ReadAll(SKStreamAsset stream)
        {
            var length = (int)stream.Length;
            if (length <= 0) return Array.Empty<byte>();

            var buffer = new byte[length];
            var read = stream.Read(buffer, length);
            return read == length ? buffer : buffer.AsSpan(0, Math.Max(0, read)).ToArray();
        }

        private static string StoredName(byte[] data)
        {
            var hash = Convert.ToHexString(SHA256.HashData(data))
                .ToLower(CultureInfo.InvariantCulture);

            return hash[..16] + ExtensionOf(data);
        }

        /// <summary>
        /// Расширение по началу файла. Внутри архива оно ни на что не влияет —
        /// шрифт читается по содержимому, — но файл, сохранённый на диск, без
        /// верного расширения система не опознает.
        /// </summary>
        private static string ExtensionOf(byte[] data)
        {
            if (data.Length < 4) return ".ttf";

            uint tag = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);

            return tag switch
            {
                0x4F54544F => ".otf",   // OTTO
                0x74746366 => ".ttc",   // ttcf
                0x774F4646 => ".woff",  // wOFF
                0x774F4632 => ".woff2", // wOF2
                _ => ".ttf"
            };
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Where(c => Array.IndexOf(invalid, c) < 0).ToArray();
            var clean = new string(chars).Trim();
            return string.IsNullOrEmpty(clean) ? "font" : clean;
        }

        private static string Unique(string directory, string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);

            var candidate = name;
            int n = 1;

            while (File.Exists(Path.Combine(directory, candidate)))
                candidate = $"{stem} ({n++}){ext}";

            return candidate;
        }

        private static DocumentContext? Context
            => CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
    }
}
