using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Откуда взялся пак.
    /// BuiltIn — из ресурсов сборки, только для чтения.
    /// UserGlobal — папка пользователя в %AppData%, видна во всех проектах.
    /// UserLocal — папка внутри архива проекта, уезжает вместе с проектом.
    /// </summary>
    public enum CharacterAvatarPackSource { BuiltIn, UserGlobal, UserLocal }

    /// <summary>
    /// Область хранения пака — то же деление, что у палитр цветов: локальная
    /// живёт в проекте, глобальная в настройках приложения. Встроенные паки
    /// считаются глобальными: они одинаковы во всех проектах.
    /// </summary>
    public enum CharacterAvatarPackScope { Local, Global }

    /// <summary>
    /// Встроенные паки: Name == null, локализация через CharactersStrings.AvatarPack_{Id}.
    /// Пользовательские паки: Name задаётся пользователем, хранится в pack.json.
    /// </summary>
    public class CharacterAvatarPackInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        // Только для пользовательских паков.
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonPropertyName("icon")]
        public string? IconFileName { get; set; }

        /// <summary>
        /// Порядок картинок в папке — именами файлов. Пустой список означает,
        /// что порядок не задавали: тогда картинки идут так, как их отдаёт
        /// хранилище, и ни одна существующая папка от появления этого поля не
        /// перестраивается.
        ///
        /// Именами, а не ссылками: ссылка несёт в себе область хранения и
        /// идентификатор папки, и при переносе папки между областями весь
        /// список пришлось бы переписывать, а имена файлов переезжают как есть.
        /// </summary>
        [JsonPropertyName("order")]
        public List<string> Order { get; set; } = new();

        // Runtime — не сериализуется.
        [JsonIgnore] public CharacterAvatarPackSource Source { get; set; }
        [JsonIgnore] public string? FolderPath { get; set; }
        [JsonIgnore] public string? IconRef { get; set; }
        [JsonIgnore] public List<CharacterAvatarItem> Items { get; set; } = new();

        /// <summary>
        /// Область хранения. Выводится из источника, а не задаётся отдельно:
        /// два независимых поля об одном и том же разошлись бы при первой же
        /// правке, а место хранения у пака ровно одно.
        /// </summary>
        [JsonIgnore]
        public CharacterAvatarPackScope Scope =>
            Source == CharacterAvatarPackSource.UserLocal
                ? CharacterAvatarPackScope.Local
                : CharacterAvatarPackScope.Global;

        /// <summary>Пак можно править: встроенные — только для просмотра.</summary>
        [JsonIgnore]
        public bool IsEditable => Source != CharacterAvatarPackSource.BuiltIn;

        // Ключ локализации для встроенных паков: AvatarPack_people_minimalism
        [JsonIgnore] public string LocalizationKey => $"AvatarPack_{Id}";
    }
}
