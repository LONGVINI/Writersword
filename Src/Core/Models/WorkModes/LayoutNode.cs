using Newtonsoft.Json;
using System.Collections.Generic;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Узел иерархической структуры layout в Dock системе
    /// Представляет контейнер с детерминированным путём вместо случайного ID
    /// Используется для точного восстановления структуры между сессиями
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class LayoutNode
    {
        /// <summary>
        /// Иерархический путь контейнера в дереве layout
        /// Генерируется детерминированно из позиции в дереве
        /// Примеры: "Root", "Root.Center", "Root.Right", "Root.Right.Top", "Root.Right.Top.Left"
        /// Для float окон: "Float.0", "Float.1"
        /// </summary>
        [JsonProperty("Path")]
        public string Path { get; set; } = "Root";

        /// <summary>
        /// Тип Dock контейнера
        /// "ProportionalDock" - контейнер со split (имеет Children)
        /// "DocumentDock" - конечный контейнер для модулей (Children = null)
        /// </summary>
        [JsonProperty("Type")]
        public string Type { get; set; } = "DocumentDock";

        /// <summary>
        /// Пропорция контейнера относительно родителя
        /// Значение от 0.0 до 1.0 или NaN для автоматического размера
        /// Например 0.7 = занимает 70% пространства родительского контейнера
        /// </summary>
        [JsonProperty("Proportion")]
        public double Proportion { get; set; } = double.NaN;

        /// <summary>
        /// Ориентация split для ProportionalDock
        /// "Horizontal" - разделение лево/право
        /// "Vertical" - разделение верх/низ
        /// null - для DocumentDock (не имеет split)
        /// </summary>
        [JsonProperty("Orientation")]
        public string? Orientation { get; set; }

        /// <summary>
        /// Дочерние узлы для ProportionalDock
        /// null или пустой список для DocumentDock (конечный узел)
        /// Для ProportionalDock содержит минимум 2 элемента
        /// </summary>
        [JsonProperty("Children")]
        public List<LayoutNode>? Children { get; set; }

        /// <summary>
        /// Создать пустой узел с дефолтными значениями
        /// </summary>
        public LayoutNode()
        {
        }

        /// <summary>
        /// Создать узел с указанным путём и типом
        /// </summary>
        public LayoutNode(string path, string type)
        {
            Path = path;
            Type = type;
        }
    }
}