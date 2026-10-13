using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Writersword.Modules.TextEditor.Models.Document
{
    /// <summary>
    /// Вид обсуждения. Word знает одно примечание; у нас их три, по тому, чего ждёт
    /// рецензент: примечание — мысль на полях, предложение — вариант, который можно
    /// принять или нет, требование — то, что исправить нужно обязательно. Вид виден
    /// цветом значка на полях и карточки в панели рецензии и отбирается в ней.
    /// </summary>
    public enum CommentKind
    {
        /// <summary>Примечание: заметка на полях.</summary>
        Note = 0,

        /// <summary>Предложение: вариант на усмотрение автора.</summary>
        Suggestion = 1,

        /// <summary>Требование: исправить обязательно.</summary>
        Requirement = 2
    }

    /// <summary>
    /// Примечание рецензента: заметка на полях к месту в тексте, как в Word.
    ///
    /// Место, к которому оставлено примечание, отмечено на самом тексте — у его
    /// фрагментов в <see cref="Inline.RunProperties.CommentIds"/> стоит Id
    /// примечания. Отметка едет вместе с текстом при любой правке, а примечание без
    /// единого отмеченного знака висит на месте своего начала.
    ///
    /// Ответ на примечание — такое же примечание со ссылкой на исходное
    /// (<see cref="ParentId"/>); отметки на тексте у ответа те же, что у исходного.
    /// </summary>
    public sealed class CommentModel
    {
        /// <summary>Уникальный идентификатор примечания.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Кто оставил примечание.</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>Инициалы автора — подпись значка примечания на полях.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Initials { get; set; }

        /// <summary>Когда оставлено (UTC). Null — время неизвестно.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? Date { get; set; }

        /// <summary>Текст примечания: один или несколько абзацев.</summary>
        public List<ParagraphBlock> Paragraphs { get; set; } = new();

        /// <summary>Примечание, на которое это — ответ. Null — примечание само начинает обсуждение.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? ParentId { get; set; }

        /// <summary>Обсуждение закрыто: вопрос решён.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Done { get; set; }

        /// <summary>Вид обсуждения. Значим у примечания, начинающего обсуждение; ответы его не меняют.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public CommentKind Kind { get; set; }

        /// <summary>Как вид называется в Word-файле: приставка к тексту примечания.</summary>
        public static string? KindPrefix(CommentKind kind) => kind switch
        {
            CommentKind.Suggestion => "Предложение:",
            CommentKind.Requirement => "Требование:",
            _ => null
        };

        /// <summary>Текст примечания одной строкой: абзацы через перевод строки.</summary>
        [JsonIgnore]
        public string PlainText
        {
            get
            {
                var builder = new StringBuilder();
                for (int i = 0; i < Paragraphs.Count; i++)
                {
                    if (i > 0) builder.Append('\n');
                    builder.Append(Paragraphs[i].GetPlainText());
                }
                return builder.ToString();
            }
        }

        /// <summary>Инициалы для значка: свои или по имени автора.</summary>
        [JsonIgnore]
        public string DisplayInitials
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Initials)) return Initials!.Trim();

                var builder = new StringBuilder();
                foreach (var part in Author.Split(new[] { ' ', '\t', '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    builder.Append(char.ToUpperInvariant(part[0]));
                    if (builder.Length == 3) break;
                }
                return builder.Length > 0 ? builder.ToString() : "?";
            }
        }
    }
}
