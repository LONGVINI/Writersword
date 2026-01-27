using System.Collections.Generic;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Контейнер для split панелей в Dock системе
    /// Описывает иерархическую структуру разделения пространства
    /// Может содержать вложенные контейнеры для сложных layouts
    /// </summary>
    public class SplitContainer
    {
        /// <summary>
        /// Уникальный идентификатор контейнера
        /// Используется модулями для привязки к конкретному контейнеру
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// Пропорция контейнера относительно родителя (0.0 - 1.0)
        /// Например 0.7 = занимает 70% пространства родительского контейнера
        /// </summary>
        public double Proportion { get; set; }

        /// <summary>
        /// Ориентация split если контейнер делится на части
        /// "Horizontal" - разделение лево/право
        /// "Vertical" - разделение верх/низ
        /// null - если контейнер не делится (конечный узел)
        /// </summary>
        public string? Orientation { get; set; }

        /// <summary>
        /// Дочерние контейнеры если это split
        /// null или пустой список если это конечный узел без разделения
        /// </summary>
        public List<SplitContainer>? Children { get; set; }
    }
}