using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Writersword.Modules.TextEditor.Models
{
    /// <summary>
    /// Абзац текста - набор фрагментов с разным форматированием
    /// </summary>
    public class Paragraph
    {
        /// <summary>Список фрагментов текста в абзаце</summary>
        public List<TextFragment> Fragments { get; set; } = new();

        /// <summary>Выравнивание: left, center, right, justify</summary>
        public string Alignment { get; set; } = "left";

        /// <summary>Отступ первой строки (в пикселях или условных единицах)</summary>
        public double Indent { get; set; } = 0;

        /// <summary>Междустрочный интервал (1.0 = одинарный, 1.5 = полуторный)</summary>
        public double LineSpacing { get; set; } = 1.0;
    }
}
