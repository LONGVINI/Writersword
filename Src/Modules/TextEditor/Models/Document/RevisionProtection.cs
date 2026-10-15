using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>Как записан отпечаток пароля защиты.</summary>
    public enum RevisionProtectionFormat
    {
        /// <summary>
        /// Как пишет Word: из пароля сначала получается старый 32-битный ключ Word,
        /// он хешируется с солью и затем повторно (w:cryptAlgorithmSid, w:hash, w:salt).
        /// </summary>
        WordLegacyKey,

        /// <summary>
        /// Атрибуты строгой схемы (w:algorithmName, w:hashValue, w:saltValue) — так
        /// пишут некоторые программы. Читается и проверяется, пишется так же, как
        /// пришло.
        /// </summary>
        AlgorithmName
    }

    /// <summary>
    /// Защита записи исправлений: правки можно только предлагать.
    ///
    /// Пароль не хранится — хранится его отпечаток, как в файле Word. Без пароля
    /// защита снимается одной кнопкой: это защита от «забыл включить запись», а не
    /// от взлома, — как и у Word.
    /// </summary>
    public sealed class RevisionProtection
    {
        /// <summary>Способ получения отпечатка.</summary>
        [JsonPropertyName("format")]
        public RevisionProtectionFormat Format { get; set; } = RevisionProtectionFormat.WordLegacyKey;

        /// <summary>Хеш-функция: SHA-1, SHA-256, SHA-384 или SHA-512.</summary>
        [JsonPropertyName("algorithm")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AlgorithmName { get; set; }

        /// <summary>Отпечаток пароля, Base64. Null — защита без пароля.</summary>
        [JsonPropertyName("hash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HashValue { get; set; }

        /// <summary>Соль, Base64.</summary>
        [JsonPropertyName("salt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SaltValue { get; set; }

        /// <summary>Сколько раз хеш прогоняется повторно.</summary>
        [JsonPropertyName("spin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int SpinCount { get; set; }

        /// <summary>Защита снимается только паролем.</summary>
        [JsonIgnore]
        public bool HasPassword => !string.IsNullOrEmpty(HashValue);

        public RevisionProtection Clone() => (RevisionProtection)MemberwiseClone();
    }
}
