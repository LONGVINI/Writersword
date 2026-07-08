using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using System;

namespace Writersword.Infrastructure.Dock
{
    /// <summary>
    /// Автоскрытие содержимого панели, сжатой до бесполезного размера.
    /// Видимая, но сжатая до полоски панель полноценно исполняет свои команды
    /// рисования (включая тени и блюры) на каждом кадре окна. Когда высота или
    /// ширина панели меньше порога, содержимое переводится в IsVisible = false —
    /// рендерер пропускает его целиком, состояние вью и вьюмодели не трогаются.
    /// Порог отслеживается по Bounds родительского контейнера: сам контент
    /// с IsVisible = false не измеряется и его Bounds перестают обновляться.
    /// </summary>
    public static class PaneAutoHideBehavior
    {
        // Порог в пикселях: ниже него в панели всё равно не видно ничего полезного.
        private const double MinUsefulSizePx = 56.0;

        private static readonly AttachedProperty<bool> IsAttachedProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "PaneAutoHideAttached", typeof(PaneAutoHideBehavior));

        /// <summary>Подключает автоскрытие к вью модуля. Повторные вызовы безопасны.</summary>
        public static void Attach(Control view)
        {
            if (view.GetValue(IsAttachedProperty)) return;
            view.SetValue(IsAttachedProperty, true);

            AvaloniaObject? host = null;
            EventHandler<AvaloniaPropertyChangedEventArgs>? handler = null;

            void Unhook()
            {
                if (host is not null && handler is not null)
                    host.PropertyChanged -= handler;
                host = null;
                handler = null;
            }

            void ApplyThreshold(Rect hostBounds)
            {
                // Нулевые/несформированные Bounds означают «размер ещё неизвестен»
                // (первый measure, только что созданное флоат-окно, хост меряется по
                // контенту). В этом состоянии прятать контент нельзя: скрытый контент
                // даёт хосту размер 0 и панель никогда не развернётся обратно.
                // Прячем только при реально измеренном, но слишком малом размере.
                if (hostBounds.Width <= 0 || hostBounds.Height <= 0)
                {
                    if (!view.IsVisible) view.IsVisible = true;
                    return;
                }

                bool useful = hostBounds.Height >= MinUsefulSizePx
                              && hostBounds.Width >= MinUsefulSizePx;
                if (view.IsVisible != useful)
                    view.IsVisible = useful;
            }

            view.AttachedToVisualTree += (_, _) =>
            {
                Unhook();
                if (view.GetVisualParent() is not Control parent) return;

                handler = (_, args) =>
                {
                    if (args.Property == Visual.BoundsProperty)
                        ApplyThreshold(parent.Bounds);
                };
                host = parent;
                parent.PropertyChanged += handler;
                ApplyThreshold(parent.Bounds);
            };

            view.DetachedFromVisualTree += (_, _) =>
            {
                Unhook();
                // Возвращаем видимость: при переносе панели вью прицепится к новому
                // хосту и порог будет применён заново по его размерам.
                view.IsVisible = true;
            };
        }
    }
}
