// src/Core/Enums/HotKeyContext.cs

namespace Writersword.Core.Enums
{
    /// <summary>
    /// Контекст выполнения горячих клавиш
    /// </summary>
    public enum HotKeyContext
    {
        Global,          // Работает всегда (File > New, Exit)
        TextEditor,      // Только в текстовом редакторе
        Screenplay,      // Только в редакторе сценариев
        CharacterEditor, // Только в редакторе персонажей
        Timeline,        // Только в таймлайне
    }
}