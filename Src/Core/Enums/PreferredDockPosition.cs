namespace Writersword.Core.Enums
{
    /// <summary>
    /// Предпочитаемая позиция модуля при добавлении в Dock
    /// </summary>
    public enum PreferredDockPosition
    {
        // === СТОРОНЫ (отдельная панель) ===

        /// <summary>Справа, отдельная панель</summary>
        Right,

        /// <summary>Слева, отдельная панель</summary>
        Left,

        /// <summary>Сверху, отдельная панель</summary>
        Top,

        /// <summary>Снизу, отдельная панель</summary>
        Bottom,

        // === УГЛЫ (отдельная панель) ===

        /// <summary>Верхний правый угол, отдельная панель</summary>
        TopRight,

        /// <summary>Верхний левый угол, отдельная панель</summary>
        TopLeft,

        /// <summary>Нижний правый угол, отдельная панель</summary>
        BottomRight,

        /// <summary>Нижний левый угол, отдельная панель</summary>
        BottomLeft,

        // === ВКЛАДКИ (добавить вкладкой к существующей панели) ===

        /// <summary>Справа вкладкой к существующей панели</summary>
        RightAsTab,

        /// <summary>Слева вкладкой к существующей панели</summary>
        LeftAsTab,

        /// <summary>Сверху вкладкой к существующей панели</summary>
        TopAsTab,

        /// <summary>Снизу вкладкой к существующей панели</summary>
        BottomAsTab,

        /// <summary>Верхний правый угол вкладкой</summary>
        TopRightAsTab,

        /// <summary>Верхний левый угол вкладкой</summary>
        TopLeftAsTab,

        /// <summary>Нижний правый угол вкладкой</summary>
        BottomRightAsTab,

        /// <summary>Нижний левый угол вкладкой</summary>
        BottomLeftAsTab
    }
}