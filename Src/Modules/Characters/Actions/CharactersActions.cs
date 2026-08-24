using Writersword.Core.Interfaces.Modules;
using System;
using System.Collections.Generic;

namespace Writersword.Modules.Characters.Actions
{
    // Переименование персонажа
    public class RenameCharacterCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _oldName;
        private readonly string _newName;
        private readonly Action<string, string> _applyName; // (id, name)

        public string Description => $"переименование «{_oldName}»";

        public RenameCharacterCommand(
            string characterId,
            string oldName,
            string newName,
            Action<string, string> applyName)
        {
            _characterId = characterId;
            _oldName = oldName;
            _newName = newName;
            _applyName = applyName;
        }

        public void Execute() => _applyName(_characterId, _newName);
        public void Undo() => _applyName(_characterId, _oldName);
    }

    // Переименование папки
    public class RenameFolderCommand : IUndoableCommand
    {
        private readonly string _folderId;
        private readonly string _oldName;
        private readonly string _newName;
        private readonly Action<string, string> _applyName;

        public string Description => $"переименование папки «{_oldName}»";

        public RenameFolderCommand(
            string folderId,
            string oldName,
            string newName,
            Action<string, string> applyName)
        {
            _folderId = folderId;
            _oldName = oldName;
            _newName = newName;
            _applyName = applyName;
        }

        public void Execute() => _applyName(_folderId, _newName);
        public void Undo() => _applyName(_folderId, _oldName);
    }

    // Удаление персонажа — Undo восстанавливает из корзины, Redo удаляет снова
    public class DeleteCharacterCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _characterName;
        private readonly Action<string> _delete;  // удалить (переместить в корзину)
        private readonly Action<string> _restore; // восстановить из корзины

        public string Description => $"удаление «{_characterName}»";

        public DeleteCharacterCommand(
            string characterId,
            string characterName,
            Action<string> delete,
            Action<string> restore)
        {
            _characterId = characterId;
            _characterName = characterName;
            _delete = delete;
            _restore = restore;
        }

        public void Execute() => _delete(_characterId);
        public void Undo() => _restore(_characterId);
    }

    // Смена аватара из галереи. Отменяемая: аватар — вещь, которую меняют
    // на пробу, и вернуть прежний должно быть так же просто, как поставить
    // новый.
    public class SetAvatarCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string? _oldAvatar;
        private readonly string? _newAvatar;
        private readonly Action<string, string?> _apply;

        public string Description => "Смена аватара";

        public SetAvatarCommand(string characterId, string? oldAvatar, string? newAvatar,
            Action<string, string?> apply)
        {
            _characterId = characterId;
            _oldAvatar = oldAvatar;
            _newAvatar = newAvatar;
            _apply = apply;
        }

        public void Execute() => _apply(_characterId, _newAvatar);
        public void Undo() => _apply(_characterId, _oldAvatar);
    }

    // Перемещение персонажа (drag-and-drop)
    public class MoveCharacterCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _characterName;
        private readonly string _fromFolderId;
        private readonly int _fromIndex;
        private readonly string _toFolderId;
        private readonly int _toIndex;
        private readonly Action<string, string, int> _applyMove; // (charId, folderId, index)

        public string Description => $"перемещение «{_characterName}»";

        public MoveCharacterCommand(
            string characterId,
            string characterName,
            string fromFolderId,
            int fromIndex,
            string toFolderId,
            int toIndex,
            Action<string, string, int> applyMove)
        {
            _characterId = characterId;
            _characterName = characterName;
            _fromFolderId = fromFolderId;
            _fromIndex = fromIndex;
            _toFolderId = toFolderId;
            _toIndex = toIndex;
            _applyMove = applyMove;
        }

        public void Execute() => _applyMove(_characterId, _toFolderId, _toIndex);
        public void Undo() => _applyMove(_characterId, _fromFolderId, _fromIndex);
    }

    // Изменение цвета персонажа
    public class ChangeCharacterColorCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _characterName;
        private readonly string _oldColor;
        private readonly string _newColor;
        private readonly Action<string, string> _applyColor; // (id, color)

        public string Description => $"изменение цвета «{_characterName}»";

        public ChangeCharacterColorCommand(
            string characterId,
            string characterName,
            string oldColor,
            string newColor,
            Action<string, string> applyColor)
        {
            _characterId = characterId;
            _characterName = characterName;
            _oldColor = oldColor;
            _newColor = newColor;
            _applyColor = applyColor;
        }

        public void Execute() => _applyColor(_characterId, _newColor);
        public void Undo() => _applyColor(_characterId, _oldColor);
    }

    // Массовое включение/выключение кольца вокруг аватара у всех персонажей.
    // Undo восстанавливает прежнее состояние каждого (кто был с кольцом, кто без).
    public class ApplyAvatarRingToAllCommand : IUndoableCommand
    {
        private readonly List<(string id, bool old)> _previous;
        private readonly bool _newValue;
        private readonly Action<string, bool> _applyRing; // (id, ringEnabled)

        public string Description => _newValue ? "кольца — всем" : "убрать кольца у всех";

        public ApplyAvatarRingToAllCommand(
            List<(string id, bool old)> previous,
            bool newValue,
            Action<string, bool> applyRing)
        {
            _previous = previous;
            _newValue = newValue;
            _applyRing = applyRing;
        }

        public void Execute()
        {
            foreach (var (id, _) in _previous) _applyRing(id, _newValue);
        }

        public void Undo()
        {
            foreach (var (id, old) in _previous) _applyRing(id, old);
        }
    }

    // Толщина цветной рамки карточки. Правится ползунком, поэтому команда
    // кладётся в историю не на каждое движение, а один раз — когда ползунок
    // отпустили. Пока его тянут, карточка перерисовывается предпросмотром,
    // который в проект не пишет.
    public class ChangeFrameThicknessCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _characterName;
        private readonly double _oldValue;
        private readonly double _newValue;
        private readonly Action<string, double> _applyThickness; // (id, толщина)

        public string Description => $"толщина рамки «{_characterName}»";

        public ChangeFrameThicknessCommand(
            string characterId,
            string characterName,
            double oldValue,
            double newValue,
            Action<string, double> applyThickness)
        {
            _characterId = characterId;
            _characterName = characterName;
            _oldValue = oldValue;
            _newValue = newValue;
            _applyThickness = applyThickness;
        }

        public void Execute() => _applyThickness(_characterId, _newValue);
        public void Undo() => _applyThickness(_characterId, _oldValue);
    }

    // Ступень важности персонажа: I, II или III.
    public class ChangeImportanceCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _characterName;
        private readonly Models.Enums.CharacterImportanceLevel _oldValue;
        private readonly Models.Enums.CharacterImportanceLevel _newValue;
        private readonly Action<string, Models.Enums.CharacterImportanceLevel> _applyLevel;

        public string Description => $"важность «{_characterName}»";

        public ChangeImportanceCommand(
            string characterId,
            string characterName,
            Models.Enums.CharacterImportanceLevel oldValue,
            Models.Enums.CharacterImportanceLevel newValue,
            Action<string, Models.Enums.CharacterImportanceLevel> applyLevel)
        {
            _characterId = characterId;
            _characterName = characterName;
            _oldValue = oldValue;
            _newValue = newValue;
            _applyLevel = applyLevel;
        }

        public void Execute() => _applyLevel(_characterId, _newValue);
        public void Undo() => _applyLevel(_characterId, _oldValue);
    }

    // Кольцо вокруг аватарки у одного персонажа. Массовое переключение
    // живёт отдельно, в ApplyAvatarRingToAllCommand: у него своё описание и
    // свой снимок прежних значений.
    public class ChangeAvatarRingCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _characterName;
        private readonly bool _oldValue;
        private readonly bool _newValue;
        private readonly Action<string, bool> _applyRing; // (id, кольцо)

        public string Description => _newValue
            ? $"кольцо у «{_characterName}»"
            : $"убрать кольцо у «{_characterName}»";

        public ChangeAvatarRingCommand(
            string characterId,
            string characterName,
            bool oldValue,
            bool newValue,
            Action<string, bool> applyRing)
        {
            _characterId = characterId;
            _characterName = characterName;
            _oldValue = oldValue;
            _newValue = newValue;
            _applyRing = applyRing;
        }

        public void Execute() => _applyRing(_characterId, _newValue);
        public void Undo() => _applyRing(_characterId, _oldValue);
    }

    // Вид аватара: кружок или полоска на всю верхнюю зону карточки.
    public class ChangeAvatarStripCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _characterName;
        private readonly bool _oldValue;
        private readonly bool _newValue;
        private readonly Action<string, bool> _applyStrip; // (id, полоска)

        public string Description => _newValue
            ? $"аватар полоской у «{_characterName}»"
            : $"аватар кружком у «{_characterName}»";

        public ChangeAvatarStripCommand(
            string characterId,
            string characterName,
            bool oldValue,
            bool newValue,
            Action<string, bool> applyStrip)
        {
            _characterId = characterId;
            _characterName = characterName;
            _oldValue = oldValue;
            _newValue = newValue;
            _applyStrip = applyStrip;
        }

        public void Execute() => _applyStrip(_characterId, _newValue);
        public void Undo() => _applyStrip(_characterId, _oldValue);
    }

    // Закладка-ленточка на карточке группы.
    public class ChangeGroupBookmarkCommand : IUndoableCommand
    {
        private readonly string _characterId;
        private readonly string _characterName;
        private readonly bool _oldValue;
        private readonly bool _newValue;
        private readonly Action<string, bool> _applyBookmark; // (id, закладка)

        public string Description => _newValue
            ? $"закладка у «{_characterName}»"
            : $"убрать закладку у «{_characterName}»";

        public ChangeGroupBookmarkCommand(
            string characterId,
            string characterName,
            bool oldValue,
            bool newValue,
            Action<string, bool> applyBookmark)
        {
            _characterId = characterId;
            _characterName = characterName;
            _oldValue = oldValue;
            _newValue = newValue;
            _applyBookmark = applyBookmark;
        }

        public void Execute() => _applyBookmark(_characterId, _newValue);
        public void Undo() => _applyBookmark(_characterId, _oldValue);
    }

    // Создание папки — Undo удаляет, Redo пересоздаёт с тем же id
    public class CreateFolderCommand : IUndoableCommand
    {
        private readonly string _folderId;
        private readonly Action<string> _create; // создать папку с конкретным id
        private readonly Action<string> _delete; // удалить папку по id

        public string Description => "создание папки";

        public CreateFolderCommand(
            string folderId,
            Action<string> create,
            Action<string> delete)
        {
            _folderId = folderId;
            _create = create;
            _delete = delete;
        }

        public void Execute() => _create(_folderId);
        public void Undo() => _delete(_folderId);
    }
}