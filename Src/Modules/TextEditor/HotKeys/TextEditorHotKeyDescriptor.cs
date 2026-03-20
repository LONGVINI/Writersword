using Avalonia.Input;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Settings;

namespace Writersword.Modules.TextEditor.HotKeys
{
    /// <summary>
    /// Статический дескриптор горячих клавиш модуля TextEditor.
    /// Используется для регистрации клавиш в HotKeyService при старте приложения
    /// до создания живого экземпляра модуля.
    /// Пользователь может переназначить любую клавишу через HotKeySettings.
    /// </summary>
    public sealed class TextEditorHotKeyDescriptor : IHotKeyDescriptor
    {
        public IReadOnlyList<HotKey> GetHotKeys() => _hotKeys;

        private static readonly IReadOnlyList<HotKey> _hotKeys = Build();

        private static List<HotKey> Build()
        {
            var d = new List<HotKey>();

            // ── Navigation ────────────────────────────────────────────────
            Add(d, "TextEditor.Navigation.Left", "Move Left",
                Key.Left);
            Add(d, "TextEditor.Navigation.Right", "Move Right",
                Key.Right);
            Add(d, "TextEditor.Navigation.Up", "Move Up",
                Key.Up);
            Add(d, "TextEditor.Navigation.Down", "Move Down",
                Key.Down);
            Add(d, "TextEditor.Navigation.Home", "Line Start",
                Key.Home);
            Add(d, "TextEditor.Navigation.End", "Line End",
                Key.End);
            Add(d, "TextEditor.Navigation.DocumentStart", "Document Start",
                Key.Home, KeyModifiers.Control);
            Add(d, "TextEditor.Navigation.DocumentEnd", "Document End",
                Key.End, KeyModifiers.Control);
            Add(d, "TextEditor.Navigation.PageUp", "Page Up",
                Key.PageUp);
            Add(d, "TextEditor.Navigation.PageDown", "Page Down",
                Key.PageDown);
            Add(d, "TextEditor.Navigation.WordLeft", "Word Left",
                Key.Left, KeyModifiers.Control);
            Add(d, "TextEditor.Navigation.WordRight", "Word Right",
                Key.Right, KeyModifiers.Control);

            // ── Selection ─────────────────────────────────────────────────
            Add(d, "TextEditor.Selection.Left", "Select Left",
                Key.Left, KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.Right", "Select Right",
                Key.Right, KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.Up", "Select Up",
                Key.Up, KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.Down", "Select Down",
                Key.Down, KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.Home", "Select to Line Start",
                Key.Home, KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.End", "Select to Line End",
                Key.End, KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.DocumentStart", "Select to Document Start",
                Key.Home, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.DocumentEnd", "Select to Document End",
                Key.End, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.All", "Select All",
                Key.A, KeyModifiers.Control);
            Add(d, "TextEditor.Selection.WordLeft", "Select Word Left",
                Key.Left, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.Selection.WordRight", "Select Word Right",
                Key.Right, KeyModifiers.Control | KeyModifiers.Shift);

            // ── Editing ───────────────────────────────────────────────────
            Add(d, "TextEditor.Editing.DeleteBack", "Delete Back",
                Key.Back);
            Add(d, "TextEditor.Editing.DeleteForward", "Delete Forward",
                Key.Delete);
            Add(d, "TextEditor.Editing.NewParagraph", "New Paragraph",
                Key.Enter);
            Add(d, "TextEditor.Editing.InsertPageBreak", "Insert Page Break",
                Key.Enter, KeyModifiers.Control);

            // ── Clipboard ─────────────────────────────────────────────────
            Add(d, "TextEditor.Clipboard.Copy", "Copy",
                Key.C, KeyModifiers.Control);
            Add(d, "TextEditor.Clipboard.Cut", "Cut",
                Key.X, KeyModifiers.Control);
            Add(d, "TextEditor.Clipboard.Paste", "Paste",
                Key.V, KeyModifiers.Control);

            // ── Undo / Redo ───────────────────────────────────────────────
            Add(d, "TextEditor.UndoRedo.Undo", "Undo",
                Key.Z, KeyModifiers.Control);
            Add(d, "TextEditor.UndoRedo.Redo", "Redo",
                Key.Y, KeyModifiers.Control);

            // ── Format: Character ─────────────────────────────────────────
            Add(d, "TextEditor.Format.Bold", "Bold",
                Key.B, KeyModifiers.Control);
            Add(d, "TextEditor.Format.Italic", "Italic",
                Key.I, KeyModifiers.Control);
            Add(d, "TextEditor.Format.Underline", "Underline",
                Key.U, KeyModifiers.Control);
            Add(d, "TextEditor.Format.Strikethrough", "Strikethrough",
                Key.X, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.Format.Superscript", "Superscript",
                Key.OemPlus, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.Format.Subscript", "Subscript",
                Key.OemPlus, KeyModifiers.Control);
            Add(d, "TextEditor.Format.AllCaps", "All Caps",
                Key.A, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.Format.SmallCaps", "Small Caps",
                Key.K, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.Format.ClearFormatting", "Clear Formatting",
                Key.Space, KeyModifiers.Control);
            Add(d, "TextEditor.Format.IncreaseFontSize", "Increase Font Size",
                Key.OemCloseBrackets, KeyModifiers.Control);
            Add(d, "TextEditor.Format.DecreaseFontSize", "Decrease Font Size",
                Key.OemOpenBrackets, KeyModifiers.Control);

            // ── Format: Paragraph ─────────────────────────────────────────
            Add(d, "TextEditor.Format.AlignLeft", "Align Left",
                Key.L, KeyModifiers.Control);
            Add(d, "TextEditor.Format.AlignCenter", "Align Center",
                Key.E, KeyModifiers.Control);
            Add(d, "TextEditor.Format.AlignRight", "Align Right",
                Key.R, KeyModifiers.Control);
            Add(d, "TextEditor.Format.AlignJustify", "Justify",
                Key.J, KeyModifiers.Control);
            Add(d, "TextEditor.Format.IncreaseIndent", "Increase Indent",
                Key.M, KeyModifiers.Control);
            Add(d, "TextEditor.Format.DecreaseIndent", "Decrease Indent",
                Key.M, KeyModifiers.Control | KeyModifiers.Shift);

            // ── View ──────────────────────────────────────────────────────
            Add(d, "TextEditor.View.ZoomIn", "Zoom In",
                Key.OemPlus, KeyModifiers.Control);
            Add(d, "TextEditor.View.ZoomOut", "Zoom Out",
                Key.OemMinus, KeyModifiers.Control);
            Add(d, "TextEditor.View.ZoomReset", "Reset Zoom",
                Key.D0, KeyModifiers.Control);

            // ── Tools ─────────────────────────────────────────────────────
            Add(d, "TextEditor.Tools.Find", "Find",
                Key.F, KeyModifiers.Control);
            Add(d, "TextEditor.Tools.FindReplace", "Find and Replace",
                Key.H, KeyModifiers.Control);
            Add(d, "TextEditor.Tools.SpellCheck", "Spell Check",
                Key.F7);
            Add(d, "TextEditor.Tools.WordCount", "Word Count",
                Key.W, KeyModifiers.Control | KeyModifiers.Shift);

            // ── File / Export ─────────────────────────────────────────────
            Add(d, "TextEditor.File.Print", "Print",
                Key.P, KeyModifiers.Control);
            Add(d, "TextEditor.File.ExportPdf", "Export to PDF",
                Key.E, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.File.ExportDocx", "Export to DOCX",
                Key.D, KeyModifiers.Control | KeyModifiers.Shift);
            Add(d, "TextEditor.File.ExportTxt", "Export to TXT",
                Key.T, KeyModifiers.Control | KeyModifiers.Shift);

            return d;
        }

        private static void Add(
            List<HotKey> list,
            string id,
            string displayNameKey,
            Key key,
            KeyModifiers mods = KeyModifiers.None)
        {
            list.Add(new HotKey
            {
                Id = id,
                DisplayNameKey = displayNameKey,
                ModuleType = "TextEditor",
                Scope = HotKeyScope.Global,
                DefaultGesture = new HotKeyGesture(new KeyGesture(key, mods))
            });
        }
    }
}