namespace Writersword.Modules.TextEditor.Views.Dialogs
{
    /// <summary>
    /// Строка списка гарнитур.
    ///
    /// Заголовок раздела хранится у первой строки раздела, а не отдельным пунктом.
    /// Отдельным пунктом он был бы выбираемым: стрелки останавливались бы на нём,
    /// а Enter применял бы к рукописи слово «Недавние». Строк одного и того же
    /// шрифта в списке может быть две — в закреплённых и среди всех, — и это
    /// намеренно: наверху лежит то, чем пользуются, внизу полный перечень.
    ///
    /// Свойства только для чтения: закрепление меняет и порядок разделов, поэтому
    /// список пересобирается целиком, а не правится на месте.
    /// </summary>
    public sealed class FontEntry
    {
        public FontEntry(string name, bool isPinned, string? sectionTitle)
        {
            Name = name;
            IsPinned = isPinned;
            SectionTitle = sectionTitle;
        }

        public string Name { get; }

        public bool IsPinned { get; }

        public string? SectionTitle { get; }

        public bool HasSection => SectionTitle is not null;
    }
}
