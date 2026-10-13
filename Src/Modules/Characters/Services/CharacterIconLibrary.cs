using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Services
{
    /// <summary>
    /// Библиотека своих значков проекта.
    ///
    /// Значки встречаются в программе в нескольких местах — у анкет, шаблонов,
    /// меток персонажей, у полей анкеты, — и своя картинка, принесённая в
    /// одном месте, нужна во всех остальных. Поэтому свои значки живут здесь,
    /// одним списком на проект, а не по отдельности в каждой анкете и метке.
    ///
    /// Библиотека хранит ссылки, а не сами картинки: файлы лежат в проекте,
    /// как аватары. Убранный из библиотеки значок у тех, кому он уже стоит,
    /// остаётся — ссылка записана у них самих, удаляется только строка
    /// списка.
    /// </summary>
    public class CharacterIconLibrary
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterIconLibrary>();

        private readonly List<CharacterIconEntry> _entries = new();

        /// <summary>Состав библиотеки изменился: добавили, убрали, переименовали.</summary>
        public event Action? Changed;

        public IReadOnlyList<CharacterIconEntry> Entries => _entries;

        /// <summary>
        /// Режим сравнения версий: библиотеку можно смотреть, но не
        /// пополнять, не переименовывать и не чистить. Загрузка — как обычно.
        /// </summary>
        public Func<bool>? IsReadOnlyProvider { get; set; }

        private bool IsReadOnly(string operation)
        {
            if (IsReadOnlyProvider?.Invoke() != true) return false;
            _logger.Debug("Icon library {Operation} ignored (compare mode)", operation);
            return true;
        }

        /// <summary>Загрузить библиотеку проекта. Пустые и повторные ссылки отбрасываются.</summary>
        public void Load(IEnumerable<CharacterIconEntry>? entries)
        {
            _entries.Clear();

            if (entries != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.ImageRef)) continue;
                    if (!seen.Add(entry.ImageRef)) continue;

                    if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                    entry.Name ??= string.Empty;
                    _entries.Add(entry);
                }
            }

            _logger.Debug("Icon library loaded: {Count}", _entries.Count);
            Changed?.Invoke();
        }

        /// <summary>Копия списка — то, что уходит в файл проекта.</summary>
        public List<CharacterIconEntry> GetForSave() => _entries.Select(e => e.Clone()).ToList();

        public CharacterIconEntry? FindByRef(string? imageRef) =>
            string.IsNullOrWhiteSpace(imageRef)
                ? null
                : _entries.FirstOrDefault(e => string.Equals(e.ImageRef, imageRef, StringComparison.Ordinal));

        /// <summary>
        /// Положить картинку в библиотеку. Та же ссылка второй раз не
        /// добавляется — возвращается уже лежащая запись. Одна картинка с
        /// разными кадрами — разные значки: кадр живёт в ссылке.
        /// </summary>
        public CharacterIconEntry? Add(string imageRef, string name)
        {
            if (string.IsNullOrWhiteSpace(imageRef)) return null;

            var existing = FindByRef(imageRef);
            if (existing != null) return existing;

            if (IsReadOnly("add")) return null;

            var entry = new CharacterIconEntry
            {
                ImageRef = imageRef,
                Name = string.IsNullOrWhiteSpace(name) ? "Свой значок" : name.Trim()
            };

            _entries.Add(entry);
            _logger.Debug("Icon added to library: {Name}", entry.Name);
            Changed?.Invoke();
            return entry;
        }

        /// <summary>
        /// Убрать значок из библиотеки. Файл картинки остаётся в проекте:
        /// значок может стоять у анкет, шаблонов и меток, и стирать его у них
        /// вслед за строкой списка было бы неожиданно.
        /// </summary>
        public bool Remove(string id)
        {
            if (IsReadOnly("remove")) return false;

            var removed = _entries.RemoveAll(e => e.Id == id) > 0;
            if (removed) Changed?.Invoke();
            return removed;
        }

        public void Rename(string id, string name)
        {
            if (IsReadOnly("rename")) return;

            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null || string.IsNullOrWhiteSpace(name)) return;

            var trimmed = name.Trim();
            if (entry.Name == trimmed) return;

            entry.Name = trimmed;
            Changed?.Invoke();
        }
    }
}
