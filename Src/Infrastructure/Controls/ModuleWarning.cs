using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Dock.Model.Core;
using System;
using System.Reactive.Linq;

namespace Writersword.Infrastructure.Controls
{
    /// <summary>
    /// Предупреждение на вкладке модуля: красный значок рядом с названием и
    /// пояснение по наведению.
    ///
    /// Свойства прикреплённые, а не собственные. Вкладку модуля представляет
    /// стоковый Dock.Model.Avalonia.Controls.Document, и завести вместо него
    /// своего наследника нельзя: раскладка сохраняется через Newtonsoft, в уже
    /// лежащих у людей файлах записан тип стокового Document, и после
    /// восстановления вернулся бы он же. Значок молча пропал бы у всех, кто
    /// открывает старую раскладку.
    ///
    /// Прикреплённое свойство этой беды лишено: Document наследует
    /// StyledElement, то есть AvaloniaObject, — значение вешается на живой
    /// объект, участвует в привязках и не сериализуется. Последнее здесь плюс:
    /// предупреждение описывает нынешнее состояние машины, а не документ.
    /// </summary>
    public static class ModuleWarning
    {
        /// <summary>Показывать ли значок на вкладке модуля.</summary>
        public static readonly AttachedProperty<bool> HasWarningProperty =
            AvaloniaProperty.RegisterAttached<AvaloniaObject, bool>(
                "HasWarning", typeof(ModuleWarning));

        /// <summary>Текст подсказки по наведению на значок.</summary>
        public static readonly AttachedProperty<string?> TextProperty =
            AvaloniaProperty.RegisterAttached<AvaloniaObject, string?>(
                "Text", typeof(ModuleWarning));

        public static bool GetHasWarning(AvaloniaObject target)
            => target.GetValue(HasWarningProperty);

        public static void SetHasWarning(AvaloniaObject target, bool value)
            => target.SetValue(HasWarningProperty, value);

        public static string? GetText(AvaloniaObject target)
            => target.GetValue(TextProperty);

        public static void SetText(AvaloniaObject target, string? value)
            => target.SetValue(TextProperty, value);

        /// <summary>
        /// Выставить или снять предупреждение одним вызовом. Пустой текст
        /// означает «предупреждения нет»: держать значок без пояснения
        /// бессмысленно — по нему нечего понять.
        /// </summary>
        public static void Set(AvaloniaObject? target, string? text)
        {
            if (target is null) return;

            bool has = !string.IsNullOrWhiteSpace(text);
            SetText(target, has ? text : null);
            SetHasWarning(target, has);
        }

        /// <summary>
        /// Шаблон значка для слота иконки вкладки модуля.
        ///
        /// Собран кодом, а не разметкой, по двум причинам, и обе выяснились
        /// дорого.
        ///
        /// Первая — supportsRecycling: false. DataTemplate, объявленный внутри
        /// Setter в файле стилей, отдаёт презентерам один и тот же экземпляр
        /// контрола. Вкладок несколько, презентеров несколько, и второй же
        /// пытается прицепить к себе контрол, который уже висит в чужом дереве:
        /// «AttachedToLogicalTreeCore called for Panel but control has no
        /// logical parent» и падение всего приложения на проходе разметки.
        /// Здесь каждый вызов строит новое дерево, и делить нечего.
        ///
        /// Вторая — привязки. FuncDataTemplate строит контрол один раз на
        /// вкладку и заново при смене предупреждения не вызывается. Поэтому
        /// видимость и текст подсказки подписаны на сам dockable через
        /// GetObservable: выставили свойство — значок появился, сняли — пропал,
        /// без пересборки шаблона.
        ///
        /// Занимается слот иконки, а не заголовка: заголовок рисует штатный
        /// шаблон Dock, и трогать его нельзя — при ошибке в нём у модуля
        /// пропадает название. Слот иконки в дереве и так есть у каждой
        /// вкладки, по умолчанию там пустой Panel; мы просто кладём в него
        /// видимый.
        /// </summary>
        public static readonly IDataTemplate TabIconTemplate =
            new FuncDataTemplate<IDockable>(
                (dockable, _) => BuildBadge(dockable),
                supportsRecycling: false);

        private static Control BuildBadge(IDockable? dockable)
        {
            var badge = new Panel
            {
                Width = 13,
                Height = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };

            // Треугольник с восклицательным знаком собран из фигур, а не взят
            // шрифтовым символом: гарнитуры со значками есть не везде, а значок
            // про нехватку шрифтов не должен сам оказаться квадратом.
            var triangle = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 8,1 L 15,14 L 1,14 Z"),
                Stretch = Stretch.Uniform
            };
            // Кисть берётся наблюдаемым ресурсом, а не разовым поиском: цвет
            // задан в каждой теме отдельно, и при смене темы значок должен
            // перекраситься вместе со всем остальным.
            triangle.Bind(
                Avalonia.Controls.Shapes.Shape.FillProperty,
                triangle.GetResourceObservable("ModuleWarningBrush"));

            var mark = new TextBlock
            {
                Text = "!",
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -1)
            };

            badge.Children.Add(triangle);
            badge.Children.Add(mark);

            // Вкладка без предупреждения не должна занимать место под значок:
            // пустой слот иконки сдвигал бы заголовок вправо на ровном месте.
            if (dockable is AvaloniaObject source)
            {
                badge.Bind(Visual.IsVisibleProperty, source.GetObservable(HasWarningProperty));
                badge.Bind(ToolTip.TipProperty,
                    source.GetObservable(TextProperty).Select(text => (object?)text));
            }
            else
            {
                badge.IsVisible = false;
            }

            return badge;
        }
    }
}
