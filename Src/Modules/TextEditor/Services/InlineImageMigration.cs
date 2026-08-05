using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Перевод картинок «в тексте» из старого представления в новое.
    ///
    /// Раньше режим <see cref="WrapMode.Inline"/> означал отдельный блок в потоке:
    /// картинка занимала собственную строку, поставить рядом с ней каретку и печатать
    /// слева или справа было нельзя. Теперь «в тексте» — обычный символ строки
    /// (объектный run со ссылкой на картинку в InlineObjects раздела).
    ///
    /// Старые документы приводятся к новому виду при загрузке: блок-картинка
    /// становится абзацем из одного объекта, а её выравнивание — выравниванием
    /// этого абзаца. Внешне документ не меняется: абзац из единственного
    /// объекта-символа даёт ту же строку с картинкой.
    /// </summary>
    public static class InlineImageMigration
    {
        /// <summary>
        /// Приводит документ к новому представлению. Возвращает true, если что-то
        /// было изменено — вызывающий может пометить документ требующим пересохранения.
        /// </summary>
        public static bool Migrate(DocumentModel? document)
        {
            if (document is null) return false;

            bool changed = false;

            foreach (var section in document.Sections)
            {
                for (int i = 0; i < section.Blocks.Count; i++)
                {
                    if (section.Blocks[i] is not ImageBlock image) continue;
                    if (image.WrapMode != WrapMode.Inline) continue;

                    var para = new ParagraphBlock();
                    para.Properties.Alignment = image.Alignment;
                    para.InsertInlineObject(0, image.Id);

                    section.Blocks[i] = para;
                    section.InlineObjects.Add(image);
                    changed = true;
                }
            }

            return changed;
        }
    }
}
