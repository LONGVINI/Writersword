namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Разбор и сборка ссылки на аватар.
    ///
    /// Ссылка состоит из адреса файла и необязательных кадров:
    ///     project:portrait.png
    ///     project:portrait.png|crop=0.05,0.1,0.4,0.4
    ///     project:portrait.png|crop=0.05,0.1,0.4,0.4|strip=0,0.2,1,0.4
    ///
    /// Кадров два, потому что карточка показывает аватарку двумя разными
    /// способами. Кружку нужен квадрат вокруг лица, полоске — широкая полоса,
    /// и один кадр на оба вида означал бы, что один из них всегда обрезан не
    /// туда. Кадр «crop» отвечает за кружок и мелкую плитку, «strip» — за
    /// полоску; если «strip» не задан, полоска берёт «crop», и старые ссылки
    /// ведут себя ровно как прежде.
    ///
    /// Адрес остаётся ровно таким, каким был до кадров — старые ссылки в уже
    /// сохранённых проектах читаются без переноса данных, а служба работает с
    /// файлом по адресу, ничего не зная про кадры.
    ///
    /// Разделителем взята вертикальная черта: Windows не пропускает её в имени
    /// файла, поэтому в адресе она встретиться не может, и разбор никогда не
    /// разрежет ссылку по символу из имени картинки.
    /// </summary>
    public static class CharacterAvatarRef
    {
        /// <summary>Метка кадра кружка, вместе с разделителем.</summary>
        public const string CropMarker = "|crop=";

        /// <summary>Метка кадра полоски, вместе с разделителем.</summary>
        public const string StripMarker = "|strip=";

        /// <summary>
        /// Адрес файла без кадров. Обрезается по первому разделителю, а не по
        /// известным меткам: так адрес не поедет, если к ссылке когда-нибудь
        /// припишут ещё одну часть.
        ///
        /// На пустой ссылке возвращает её саму — вызывающая сторона проверяет
        /// пустоту по своим правилам.
        /// </summary>
        public static string? BaseOf(string? avatarRef)
        {
            if (string.IsNullOrEmpty(avatarRef)) return avatarRef;

            var index = avatarRef.IndexOf('|');
            return index < 0 ? avatarRef : avatarRef[..index];
        }

        /// <summary>
        /// Значение части ссылки по её метке. Читается до следующего
        /// разделителя, а не до конца строки: за одной частью может стоять
        /// другая.
        /// </summary>
        private static string? PayloadOf(string? avatarRef, string marker)
        {
            if (string.IsNullOrEmpty(avatarRef)) return null;

            var index = avatarRef.IndexOf(marker, System.StringComparison.Ordinal);
            if (index < 0) return null;

            var start = index + marker.Length;
            var end = avatarRef.IndexOf('|', start);
            return end < 0 ? avatarRef[start..] : avatarRef[start..end];
        }

        /// <summary>
        /// Кадр кружка. null — кадра нет или запись испорчена: в обоих случаях
        /// картинка показывается целиком.
        /// </summary>
        public static CharacterAvatarCrop? CropOf(string? avatarRef)
        {
            var payload = PayloadOf(avatarRef, CropMarker);
            if (payload is null) return null;
            return CharacterAvatarCrop.TryParse(payload, out var crop) ? crop : null;
        }

        /// <summary>
        /// Кадр полоски. null — отдельного кадра для полоски не задавали, и
        /// она берёт кадр кружка.
        /// </summary>
        public static CharacterAvatarCrop? StripCropOf(string? avatarRef)
        {
            var payload = PayloadOf(avatarRef, StripMarker);
            if (payload is null) return null;
            return CharacterAvatarCrop.TryParse(payload, out var crop) ? crop : null;
        }

        /// <summary>
        /// Кадр под нужный вид карточки. Полоска без своего кадра берёт кадр
        /// кружка — иначе смена вида аватара внезапно показывала бы картинку
        /// целиком там, где её уже подрезали.
        /// </summary>
        public static CharacterAvatarCrop? CropFor(string? avatarRef, bool forStrip)
        {
            if (!forStrip) return CropOf(avatarRef);
            return StripCropOf(avatarRef) ?? CropOf(avatarRef);
        }

        /// <summary>Адрес и кадр кружка за один разбор.</summary>
        public static (string? BaseRef, CharacterAvatarCrop? Crop) Split(string? avatarRef)
            => (BaseOf(avatarRef), CropOf(avatarRef));

        /// <summary>
        /// Собрать ссылку. Кадр во всю картинку не пишется: ссылка без кадра и
        /// ссылка с полным кадром означают одно и то же, и две записи одного
        /// смысла разошлись бы при сравнении ссылок.
        /// </summary>
        public static string? Combine(string? baseRef, CharacterAvatarCrop? crop)
            => Combine(baseRef, crop, null);

        /// <summary>
        /// Собрать ссылку с обоими кадрами. Кадр полоски не пишется, когда он
        /// совпадает с кадром кружка: полоска и так берёт кружковый, а вторая
        /// запись того же значения разошлась бы с первой при следующей правке.
        /// </summary>
        public static string? Combine(
            string? baseRef,
            CharacterAvatarCrop? crop,
            CharacterAvatarCrop? stripCrop)
        {
            if (string.IsNullOrEmpty(baseRef)) return baseRef;

            // Адрес мог прийти уже со старыми кадрами — берём из него только адрес.
            var clean = BaseOf(baseRef);
            if (string.IsNullOrEmpty(clean)) return clean;

            var result = clean;
            if (crop is not null && !crop.IsFull) result += CropMarker + crop;

            var stripMatchesCrop = stripCrop is null
                || (crop is null ? stripCrop.IsFull : stripCrop.Equals(crop));
            if (!stripMatchesCrop) result += StripMarker + stripCrop;

            return result;
        }

        /// <summary>
        /// Заменить кадр кружка, оставив адрес и кадр полоски. Отличие от
        /// Combine в том, что уже заданный кадр полоски отсюда не теряется.
        /// </summary>
        public static string? WithCrop(string? avatarRef, CharacterAvatarCrop? crop)
            => Combine(BaseOf(avatarRef), crop, StripCropOf(avatarRef));

        /// <summary>Ссылки указывают на один файл, кадры при этом могут отличаться.</summary>
        public static bool SameFile(string? left, string? right)
        {
            var a = BaseOf(left);
            var b = BaseOf(right);
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Пара кадров, которую отдаёт окно обрезки: свой кадр кружку и свой —
    /// полоске. Простой класс, а не кортеж: он ездит через несколько границ, и
    /// имена полей на месте вызова читаются лучше, чем Item1 и Item2.
    /// </summary>
    public sealed class CharacterAvatarCropPair
    {
        public CharacterAvatarCropPair(CharacterAvatarCrop? circle, CharacterAvatarCrop? strip)
        {
            Circle = circle;
            Strip = strip;
        }

        /// <summary>Кадр для кружка и мелкой плитки.</summary>
        public CharacterAvatarCrop? Circle { get; }

        /// <summary>Кадр для полоски. null — полоска берёт кадр кружка.</summary>
        public CharacterAvatarCrop? Strip { get; }
    }
}
