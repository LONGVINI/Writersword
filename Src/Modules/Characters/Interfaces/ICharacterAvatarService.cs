using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.Threading.Tasks;
using Writersword.Core.Services;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Interfaces
{
    public interface ICharacterAvatarService
    {
        void SetContext(DocumentContext? context);

        // Сохранить в ZIP проекта. Возвращает "project:filename" или null.
        // При коллизии имён добавляет " (1)", " (2)" и т.д.
        // Запись выполняется в фоновом потоке — не блокирует UI.
        Task<string?> SaveToProjectAsync(byte[] imageData, string suggestedName);

        // Сохранить в библиотеку (%AppData%). Возвращает "lib:filename" или null.
        Task<string?> SaveToLibraryAsync(byte[] imageData, string suggestedName);

        // Скопировать существующий project-аватар в библиотеку.
        Task<string?> CopyProjectAvatarToLibraryAsync(string projectAvatarRef);

        // Загрузить байты по ref ("project:..." или "lib:...").
        byte[]? LoadAvatarBytes(string? avatarRef);

        // Загрузить как Bitmap (удобный метод для VM).
        Bitmap? LoadBitmap(string? avatarRef);

        // Удалить из ZIP или с диска.
        void DeleteAvatar(string? avatarRef);

        // Галерея: аватарки из ZIP текущего проекта.
        IReadOnlyList<CharacterAvatarItem> GetProjectAvatars();

        // Галерея: аватарки из библиотеки.
        IReadOnlyList<CharacterAvatarItem> GetLibraryAvatars();

        // Встроенные дефолтные аватарки (из папки BuiltInPath).
        IReadOnlyList<CharacterAvatarItem> GetBuiltInAvatars();

        // Путь к папке встроенных аватарок (для настройки).
        void SetBuiltInPath(string? path);
    }
}