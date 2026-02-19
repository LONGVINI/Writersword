using System.Collections.Generic;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Конфигурация размеров окон и раскладки
    /// Сохраняет размеры главного окна, позиции разделителей (splitters) и плавающие окна
    /// АВТОМАТИЧЕСКИ сохраняется при изменении
    /// Используется как в глобальных (Settings.json), так и в локальных (workspace.json) конфигурациях
    /// </summary>
    public class WindowLayoutConfig
    {
        /// <summary>Ширина главного окна в пикселях</summary>
        public int Width { get; set; }

        /// <summary>Высота главного окна в пикселях</summary>
        public int Height { get; set; }

        /// <summary>Позиции разделителей (splitters) в процентах или пикселях</summary>
        public List<double> SplitterPositions { get; set; } = new();

        /// <summary>Список открытых плавающих окон с их позициями и размерами</summary>
        public List<FloatWindowConfig> FloatWindows { get; set; } = new();
    }

    /// <summary>
    /// Конфигурация плавающего окна модуля (Float window)
    /// Сохраняет позицию, размер и какой модуль в нём открыт
    /// Используется как в глобальных (Settings.json), так и в локальных (workspace.json) конфигурациях
    /// </summary>
    public class FloatWindowConfig
    {
        /// <summary>ID модуля который открыт в плавающем окне</summary>
        public string moduleType { get; set; } = "";

        /// <summary>Позиция окна X (пиксели от левого края экрана)</summary>
        public int X { get; set; }

        /// <summary>Позиция окна Y (пиксели от верхнего края экрана)</summary>
        public int Y { get; set; }

        /// <summary>Ширина окна в пикселях</summary>
        public int Width { get; set; } = 800;

        /// <summary>Высота окна в пикселях</summary>
        public int Height { get; set; } = 600;

        /// <summary>Развёрнуто ли окно на весь экран</summary>
        public bool IsMaximized { get; set; }
    }
}