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

        // ── Сохранение ────────────────────────────────────────────────────

        Task<string?> SaveToProjectAsync(byte[] imageData, string suggestedName);

        /// <summary>
        /// Сохранить картинку значка метки. Отдельно от аватаров: у значков
        /// шире список форматов — сюда идут и мелкие растры, и векторный SVG,
        /// который аватаром быть не может (его некуда обрезать по кругу без
        /// растеризации и он не проходит через загрузчик битмапов).
        /// </summary>
        Task<string?> SaveIconToProjectAsync(byte[] imageData, string suggestedName);
        Task<string?> SaveToLibraryAsync(byte[] imageData, string suggestedName);
        Task<string?> CopyProjectAvatarToLibraryAsync(string projectRef);
        void DeleteAvatar(string? avatarRef);

        // ── Загрузка ──────────────────────────────────────────────────────

        Bitmap? LoadBitmap(string? avatarRef);
        Bitmap? LoadBitmap(string? avatarRef, int maxSide);
        byte[]? LoadAvatarBytes(string? avatarRef);

        // ── Паки ──────────────────────────────────────────────────────────

        /// <summary>
        /// Зарегистрировать директорию с паками аватарок.
        /// Каждая подпапка = один пак.
        /// Любой модуль может вызвать этот метод со своей папкой ресурсов.
        /// Вызывается несколько раз — директории накапливаются.
        /// </summary>
        void RegisterPackDirectory(string directory);

        IReadOnlyList<CharacterAvatarItem> GetProjectAvatars();
        IReadOnlyList<CharacterAvatarPackInfo> GetAllPacks();

        CharacterAvatarPackInfo CreateUserPack(string name);
        void DeleteUserPack(string packId);
        Task MoveAvatarToPackAsync(string avatarRef, string targetPackId);
        Task<CharacterAvatarPackInfo?> ImportPackFromZipAsync(string zipPath);
        Task ExportPackToZipAsync(string packId, string outputPath);
    }
}