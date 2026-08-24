namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Откуда прочитана картинка.
    /// Project — из архива текущего проекта.
    /// Library — из несгруппированной библиотеки пользователя в %AppData%.
    /// BuiltIn — из ресурсов сборки.
    /// UserPack — из пользовательского пака, глобального или локального.
    /// </summary>
    public enum CharacterAvatarSource { Project, Library, BuiltIn, UserPack }

    public class CharacterAvatarItem
    {
        public string AvatarRef { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public CharacterAvatarSource Source { get; set; }
        public string? PackId { get; set; }

        /// <summary>
        /// Область хранения пака, из которого пришла картинка. У проектных
        /// аватарок и локальных паков — Local, у остальных Global. Нужна
        /// пикеру: удаление и перенос между паками разрешены только внутри
        /// своей области, а встроенное не трогается вовсе.
        /// </summary>
        public CharacterAvatarPackScope Scope { get; set; } = CharacterAvatarPackScope.Global;

        /// <summary>
        /// Картинку разрешено удалить из хранилища. Встроенные паки лежат в
        /// ресурсах сборки и удалению не подлежат.
        /// </summary>
        public bool CanDelete => Source != CharacterAvatarSource.BuiltIn;
    }
}
