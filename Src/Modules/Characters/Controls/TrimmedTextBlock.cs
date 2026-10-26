using Avalonia;
using Avalonia.Controls;
using System;
using Writersword.Infrastructure.Behaviours;

namespace Writersword.Modules.Characters.Controls
{
    /// <summary>
    /// Текст, который сам знает, обрезан ли он многоточием, и только тогда
    /// даёт нашу подсказку (TooltipBehavior) с полным текстом. Влезший
    /// целиком текст подсказки не даёт — повторять его незачем.
    ///
    /// Подсказка встаёт на сам текст, а если он не ловит указатель (показ
    /// поверх поля ввода) — на его родителя: наводят тогда на родителя.
    ///
    /// Стили у него те же, что у TextBlock: селекторы TextBlock.* его видят.
    /// </summary>
    public class TrimmedTextBlock : TextBlock
    {
        protected override Type StyleKeyOverride => typeof(TextBlock);

        private Control? _host;

        protected override Size ArrangeOverride(Size finalSize)
        {
            var result = base.ArrangeOverride(finalSize);
            UpdateTip();
            return result;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsHitTestVisibleProperty)
                UpdateTip();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_host != null)
            {
                TooltipBehavior.SetDescription(_host, null);
                _host = null;
            }
            base.OnDetachedFromVisualTree(e);
        }

        private void UpdateTip()
        {
            var host = IsHitTestVisible ? this : Parent as Control ?? this;

            // Подсказка переехала (текст стал ловить указатель или наоборот) —
            // со старого места она уходит.
            if (_host != null && !ReferenceEquals(_host, host))
                TooltipBehavior.SetDescription(_host, null);
            _host = host;

            var text = Text;
            var tip = IsTrimmed(text) ? text : null;
            if (!string.Equals(TooltipBehavior.GetDescription(host), tip, StringComparison.Ordinal))
                TooltipBehavior.SetDescription(host, tip);
        }

        /// <summary>
        /// Обрезан: строка свёрнута многоточием или показанные строки вместили
        /// не весь текст (упёрлись в MaxLines).
        /// </summary>
        private bool IsTrimmed(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var shown = 0;
            foreach (var line in TextLayout.TextLines)
            {
                if (line.HasCollapsed) return true;
                shown += line.Length;
            }

            return shown < text.Length;
        }
    }
}
