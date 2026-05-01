using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;
using Serilog;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Services
{
    public class DeletedCharacterEntry
    {
        public Character Character { get; init; } = null!;
        public string? OriginalFolderId { get; init; }
        public int OriginalIndex { get; init; }
        public DateTime DeletedAt { get; init; }
    }

    public class CharactersTrashService : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersTrashService>();
        private const int MaxTrashSize = 50;

        private readonly ICharacterService _characterService;

        public ObservableCollection<DeletedCharacterEntry> Items { get; } = new();

        private int _count;
        public int Count
        {
            get => _count;
            private set => this.RaiseAndSetIfChanged(ref _count, value);
        }

        public CharactersTrashService(ICharacterService characterService)
        {
            _characterService = characterService;
            Items.CollectionChanged += (_, _) => Count = Items.Count;
        }

        // Добавляет персонажа в корзину.
        // Вызывается ДО удаления из CharacterService — объект ещё живой.
        // Храним ссылку напрямую: после Delete из сервиса объект больше не используется сервисом,
        // поэтому ссылка безопасна.
        public void Add(Character character, string? folderId, int folderIndex)
        {
            var entry = new DeletedCharacterEntry
            {
                Character = character,
                OriginalFolderId = folderId,
                OriginalIndex = folderIndex,
                DeletedAt = DateTime.Now
            };

            Items.Insert(0, entry);

            while (Items.Count > MaxTrashSize)
                Items.RemoveAt(Items.Count - 1);

            _logger.Debug("CharactersTrashService.Add: '{Name}' ({Id}), folder={Folder}, idx={Idx}",
                character.Name, character.Id, folderId, folderIndex);
        }

        // Восстанавливает персонажа из корзины через ICharacterService.CreateWithId.
        // Возвращает (character, originalFolderId, originalIndex) или null если не найден.
        public (Character character, string? folderId, int folderIndex)? Restore(string characterId)
        {
            var entry = Items.FirstOrDefault(e => e.Character.Id == characterId);
            if (entry is null)
            {
                _logger.Warning("CharactersTrashService.Restore: entry not found for {Id}", characterId);
                return null;
            }

            Items.Remove(entry);

            // Если почему-то персонаж уже есть в сервисе — не создаём повторно
            var existing = _characterService.GetById(characterId);
            if (existing is not null)
            {
                _logger.Debug("CharactersTrashService.Restore: already exists, skipping create for {Id}", characterId);
                return (existing, entry.OriginalFolderId, entry.OriginalIndex);
            }

            var restored = _characterService.CreateWithId(entry.Character);
            _logger.Debug("CharactersTrashService.Restore: created '{Name}' ({Id}), folder={Folder}, idx={Idx}",
                restored.Name, restored.Id, entry.OriginalFolderId, entry.OriginalIndex);

            return (restored, entry.OriginalFolderId, entry.OriginalIndex);
        }

        public void DeletePermanently(string characterId)
        {
            var entry = Items.FirstOrDefault(e => e.Character.Id == characterId);
            if (entry is not null)
            {
                Items.Remove(entry);
                _logger.Debug("CharactersTrashService.DeletePermanently: '{Name}' ({Id})",
                    entry.Character.Name, characterId);
            }
        }

        public void Clear()
        {
            Items.Clear();
            _logger.Debug("CharactersTrashService.Clear");
        }
    }
}