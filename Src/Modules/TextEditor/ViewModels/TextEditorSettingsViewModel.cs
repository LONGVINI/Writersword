using ReactiveUI;
using System.Collections.Generic;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// ViewModel для настроек текстового редактора
    /// </summary>
    public class TextEditorSettingsViewModel : ReactiveObject
    {
        private double _fontSize;
        private string _fontFamily = "";

        public double FontSize
        {
            get => _fontSize;
            set => this.RaiseAndSetIfChanged(ref _fontSize, value);
        }

        public string FontFamily
        {
            get => _fontFamily;
            set => this.RaiseAndSetIfChanged(ref _fontFamily, value);
        }

        public List<string> AvailableFonts { get; } = new()
        {
            "Times New Roman",
            "Arial",
            "Georgia",
            "Verdana",
            "Courier New",
            "Palatino Linotype",
            "Garamond",
            "Book Antiqua"
        };
    }
}