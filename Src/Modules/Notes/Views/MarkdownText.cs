using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Writersword.Modules.Notes.Models;
using Writersword.Modules.Notes.Services;

namespace Writersword.Modules.Notes.Views
{
    /// <summary>
    /// Надпись, которая рисует разметку внутри строки набело.
    ///
    /// Нужна затем, что поле ввода не умеет показывать часть своего текста
    /// жирным. Надпись лежит поверх поля и показывает строку так, как она
    /// должна выглядеть; поле под ней в этот момент прозрачное. Как только в
    /// строку встаёт каретка, надпись прячется и видно то, что записано, —
    /// иначе знаки разметки нельзя было бы поправить.
    /// </summary>
    public class MarkdownText : TextBlock
    {
        /// <summary>Шрифт кусков, оформленных как код.</summary>
        private static readonly FontFamily CodeFont = new("Consolas, Courier New, monospace");

        /// <summary>Исходный текст строки со знаками разметки.</summary>
        public static readonly StyledProperty<string?> MarkdownProperty =
            AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

        /// <summary>Цвет кусков, оформленных как код.</summary>
        public static readonly StyledProperty<IBrush?> CodeBrushProperty =
            AvaloniaProperty.Register<MarkdownText, IBrush?>(nameof(CodeBrush));

        /// <summary>Цвет выделенных кусков — «==текст==».</summary>
        public static readonly StyledProperty<IBrush?> MarkBrushProperty =
            AvaloniaProperty.Register<MarkdownText, IBrush?>(nameof(MarkBrush));

        /// <summary>Вся строка зачёркнута — знак приходит от вида строки, а не от разметки.</summary>
        public static readonly StyledProperty<bool> IsStruckProperty =
            AvaloniaProperty.Register<MarkdownText, bool>(nameof(IsStruck));

        public string? Markdown
        {
            get => GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        public IBrush? CodeBrush
        {
            get => GetValue(CodeBrushProperty);
            set => SetValue(CodeBrushProperty, value);
        }

        public IBrush? MarkBrush
        {
            get => GetValue(MarkBrushProperty);
            set => SetValue(MarkBrushProperty, value);
        }

        public bool IsStruck
        {
            get => GetValue(IsStruckProperty);
            set => SetValue(IsStruckProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == MarkdownProperty ||
                change.Property == CodeBrushProperty ||
                change.Property == MarkBrushProperty ||
                change.Property == IsStruckProperty)
            {
                Rebuild();
            }
        }

        /// <summary>Пересобрать куски надписи по разметке строки.</summary>
        private void Rebuild()
        {
            var inlines = Inlines;
            if (inlines == null)
            {
                inlines = new InlineCollection();
                Inlines = inlines;
            }

            inlines.Clear();

            var source = Markdown ?? string.Empty;
            if (source.Length == 0)
                return;

            foreach (var span in NotesService.ParseInline(source))
            {
                if (span.Text.Length == 0)
                    continue;
                inlines.Add(CreateRun(span));
            }
        }

        /// <summary>Собрать кусок надписи по его оформлению.</summary>
        private Run CreateRun(NoteSpan span)
        {
            var run = new Run { Text = span.Text };

            if ((span.Style & NoteSpanStyle.Bold) != 0)
                run.FontWeight = FontWeight.Bold;

            if ((span.Style & NoteSpanStyle.Italic) != 0)
                run.FontStyle = FontStyle.Italic;

            if ((span.Style & NoteSpanStyle.Code) != 0)
            {
                run.FontFamily = CodeFont;
                if (CodeBrush != null)
                    run.Foreground = CodeBrush;
            }

            // Выделенный кусок красится, а не заливается: заливка у куска
            // надписи держится не во всех темах одинаково, а цвет — везде.
            // Заливка всей строки осталась отдельной командой ленты.
            if ((span.Style & NoteSpanStyle.Mark) != 0 && MarkBrush != null)
                run.Foreground = MarkBrush;

            // Зачёркивание строки целиком и зачёркивание куска дают один и тот
            // же знак, поэтому проверяются вместе.
            // Имя типа пишется полностью: внутри наследника надписи простое
            // «TextDecorations» разбирается как унаследованное свойство, а не
            // как класс со знаками подчёркивания и зачёркивания.
            if (IsStruck || (span.Style & NoteSpanStyle.Strikethrough) != 0)
                run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;

            return run;
        }
    }
}
