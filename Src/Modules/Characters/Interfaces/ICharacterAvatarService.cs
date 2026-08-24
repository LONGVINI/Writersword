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

        /// <summary>
        /// Положить картинку в пак — пользовательский, глобальный или
        /// локальный. Для встроенного пака возвращает null: ресурсы сборки не
        /// пополняются.
        /// </summary>
        Task<string?> SaveToPackAsync(byte[] imageData, string suggestedName, string packId);

        Task<string?> CopyProjectAvatarToLibraryAsync(string projectRef);
        void DeleteAvatar(string? avatarRef);

        /// <summary>
        /// Найти уже сохранённую картинку с тем же содержимым. Возвращает
        /// адрес найденной (без кадра) или null.
        ///
        /// Нужна при добавлении: одна и та же фотография, брошенная на две
        /// карточки, должна лечь в проект один раз. Различаются такие аватарки
        /// не файлом, а кадром, который живёт в ссылке персонажа.
        /// </summary>
        string? FindStoredByContent(byte[] imageData);

        // ── Загрузка ──────────────────────────────────────────────────────

        Bitmap? LoadBitmap(string? avatarRef);
        Bitmap? LoadBitmap(string? avatarRef, int maxSide);

        /// <summary>
        /// Картинка под нужный вид карточки. forStrip выбирает кадр полоски;
        /// если своего кадра у полоски нет, берётся кадр кружка.
        /// </summary>
        Bitmap? LoadBitmap(string? avatarRef, int maxSide, bool forStrip);
        byte[]? LoadAvatarBytes(string? avatarRef);

        /// <summary>
        /// Размер картинки в пикселях без её раскодирования целиком в битмап
        /// экрана. Нужен окну обрезки: оно считает кадр в долях и должно знать
        /// пропорции исходника до того, как что-то показать.
        /// </summary>
        Avalonia.PixelSize? GetImageSize(string? avatarRef);

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

        /// <summary>
        /// Создать пак в заданной области: Global — папка в %AppData%,
        /// Local — папка внутри архива проекта. Локальный пак уезжает вместе с
        /// проектом, глобальный виден во всех.
        /// </summary>
        CharacterAvatarPackInfo? CreatePack(string name, CharacterAvatarPackScope scope);

        void DeleteUserPack(string packId);

        /// <summary>
        /// Удалить пак вместе с его картинками. Встроенные паки не удаляются.
        /// </summary>
        void DeletePack(string packId, CharacterAvatarPackScope scope);

        /// <summary>
        /// Правка описания папки: имя и обложка. name == null оставляет имя
        /// прежним, iconFileName == null снимает обложку — папка снова будет
        /// показываться первой своей картинкой.
        ///
        /// Одно действие на оба поля: они лежат в одном pack.json, и разделять
        /// их значило бы переписывать файл дважды подряд.
        /// </summary>
        void UpdatePackMeta(
            string packId,
            CharacterAvatarPackScope scope,
            string? name,
            string? iconFileName);

        /// <summary>
        /// Перенести пак в другую область, сохранив имя и содержимое: то же
        /// действие, что у палитр цветов — «локальная/глобальная». Возвращает
        /// пак на новом месте или null, если перенос не удался.
        /// </summary>
        Task<CharacterAvatarPackInfo?> MovePackToScopeAsync(string packId, CharacterAvatarPackScope targetScope);

        Task MoveAvatarToPackAsync(string avatarRef, string targetPackId);
        Task<CharacterAvatarPackInfo?> ImportPackFromZipAsync(string zipPath);
        Task ExportPackToZipAsync(string packId, string outputPath);

        // ── Недавние ──────────────────────────────────────────────────────

        /// <summary>
        /// Недавно поставленные аватарки, свежие впереди. Ссылки на картинки
        /// чужих проектов из выдачи отсеиваются: прочитать их нечем.
        /// </summary>
        IReadOnlyList<CharacterAvatarItem> GetRecentAvatars();

        /// <summary>
        /// Отметить аватарку использованной. Повторная отметка той же картинки
        /// поднимает существующую запись, а не заводит вторую: совпадение
        /// считается по адресу файла, кадр при этом обновляется на последний.
        /// </summary>
        void AddRecentAvatar(string? avatarRef);

        /// <summary>
        /// Убрать запись из «Недавних». Файл картинки не трогается, персонажи
        /// с этой аватаркой остаются с ней — список только помнит порядок
        /// обращений, ничего не храня сам.
        /// </summary>
        void RemoveRecentAvatar(string? avatarRef);

        /// <summary>Очистить «Недавние» целиком. Картинки остаются на местах.</summary>
        void ClearRecentAvatars();
    }
}
