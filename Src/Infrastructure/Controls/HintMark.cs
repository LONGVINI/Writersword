using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Writersword.Infrastructure.Controls
{
    /// <summary>
    /// Значок пояснения: кружок с восклицательным знаком, за которым висит
    /// подсказка.
    ///
    /// Текст подсказки навешивается через TooltipBehavior:
    ///     &lt;infra:HintMark behaviours:TooltipBehavior.Tip="Заголовок"
    ///                     behaviours:TooltipBehavior.Description="Пояснение"/&gt;
    ///
    /// Шаблон и цвета заданы прямо здесь, а не файлом стилей: селектор стиля
    /// сверяет тип вместе с его сборкой, и стиль из приложения не применился бы
    /// к контролу из общей сборки. Заданный в коде шаблон от сборки не зависит,
    /// поэтому контрол собирается ровно один раз — в Writersword.UI.Shared,
    /// а приложение и модули берут его оттуда.
    /// </summary>
    public class HintMark : TemplatedControl
    {
        /// <summary>
        /// Контур круга и восклицательный знак одной фигурой, 24×24.
        /// </summary>
        private const string IconGeometry =
            "M11 15h2v2h-2zm0-8h2v6h-2zm.99-5C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8z";

        public HintMark()
        {
            Width = 16;
            Height = 16;
            Cursor = new Cursor(StandardCursorType.Help);

            // Цвет берётся из темы и следует за её переключением.
            this[!ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush");

            Template = new FuncControlTemplate<HintMark>((control, scope) =>
            {
                var path = new Path
                {
                    Data = Geometry.Parse(IconGeometry),
                    Stretch = Stretch.Uniform
                };

                path[!Shape.FillProperty] = control[!ForegroundProperty];

                // Прозрачная подложка на всю площадь: без неё указатель
                // попадает только по нарисованным пикселям знака, и подсказка
                // ловится лишь при точном наведении на обводку круга.
                return new Panel
                {
                    Background = Brushes.Transparent,
                    Children =
                    {
                        new Viewbox
                        {
                            Width = 16,
                            Height = 16,
                            Stretch = Stretch.Uniform,
                            Child = path
                        }
                    }
                };
            });

            // Подсветка на наведении: отдельным стилем её пришлось бы дублировать
            // в каждой сборке, поэтому переключение цвета сделано здесь.
            PointerEntered += (_, _) =>
                this[!ForegroundProperty] = new DynamicResourceExtension("AccentDefaultBrush");

            PointerExited += (_, _) =>
                this[!ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush");
        }
    }
}
