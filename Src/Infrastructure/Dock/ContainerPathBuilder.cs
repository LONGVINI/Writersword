using Dock.Model.Avalonia.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Генератор детерминированных путей для контейнеров Dock
    /// Создаёт иерархические пути вида Root.Right.Top.Left вместо случайных GUID
    /// Обеспечивает стабильность ID между сессиями
    /// </summary>
    public class ContainerPathBuilder
    {
        private readonly ILogger<ContainerPathBuilder> _logger;
        private const int MaxDepth = 7;

        public ContainerPathBuilder()
        {
            _logger = App.Services.GetService<ILogger<ContainerPathBuilder>>()!;
        }

        /// <summary>
        /// Построить путь для дочернего контейнера
        /// Добавляет сегмент к родительскому пути
        /// </summary>
        /// <param name="parentPath">Путь родительского контейнера</param>
        /// <param name="segmentName">Имя нового сегмента (Center, Right, Top, Left, Bottom)</param>
        /// <returns>Полный путь дочернего контейнера</returns>
        public string BuildPath(string parentPath, string segmentName)
        {
            if (string.IsNullOrEmpty(parentPath))
            {
                _logger.LogWarning("Parent path is empty, using segment as root");
                return segmentName;
            }

            var newPath = $"{parentPath}.{segmentName}";

            var depth = CalculateDepth(newPath);
            if (depth > MaxDepth)
            {
                _logger.LogWarning("Path depth {Depth} exceeds maximum {MaxDepth}: {Path}",
                    depth, MaxDepth, newPath);
            }

            return newPath;
        }

        /// <summary>
        /// Построить путь для контейнера по направлению
        /// Используется при динамическом создании панелей
        /// </summary>
        /// <param name="parentPath">Путь родительского контейнера</param>
        /// <param name="direction">Направление (Right, Left, Top, Bottom)</param>
        /// <param name="index">Индекс если несколько контейнеров в одном направлении</param>
        /// <returns>Полный путь контейнера</returns>
        public string BuildPathByDirection(string parentPath, string direction, int index = 0)
        {
            var segmentName = index == 0 ? direction : $"{direction}{index}";
            return BuildPath(parentPath, segmentName);
        }

        /// <summary>
        /// Вычислить путь для существующего контейнера в дереве
        /// Рекурсивно проходит от корня до целевого контейнера
        /// </summary>
        /// <param name="target">Целевой контейнер</param>
        /// <param name="root">Корневой контейнер</param>
        /// <returns>Полный путь или null если контейнер не найден</returns>
        public string? CalculatePathFromRoot(IDockable target, IDock root)
        {
            if (target == root)
                return "Root";

            var path = FindPathRecursive(target, root, "Root");

            if (path == null)
            {
                _logger.LogWarning("Could not calculate path for container: {Id}", target.Id);
            }

            return path;
        }

        /// <summary>
        /// Рекурсивный поиск пути к контейнеру
        /// </summary>
        private string? FindPathRecursive(IDockable target, IDockable current, string currentPath)
        {
            if (current == target)
                return currentPath;

            if (current is IDock dock && dock.VisibleDockables != null)
            {
                for (int i = 0; i < dock.VisibleDockables.Count; i++)
                {
                    var child = dock.VisibleDockables[i];

                    if (child is ProportionalDockSplitter)
                        continue;

                    var childSegment = GenerateSegmentName(child, i);
                    var childPath = BuildPath(currentPath, childSegment);

                    var result = FindPathRecursive(target, child, childPath);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Сгенерировать имя сегмента для дочернего контейнера
        /// Использует ID контейнера или позицию в списке
        /// </summary>
        private string GenerateSegmentName(IDockable dockable, int index)
        {
            if (!string.IsNullOrEmpty(dockable.Id))
            {
                var id = dockable.Id;

                if (id.StartsWith("Root."))
                    return id.Substring(5);

                if (id.Contains("Center"))
                    return "Center";
                if (id.Contains("Right"))
                    return "Right";
                if (id.Contains("Left"))
                    return "Left";
                if (id.Contains("Top"))
                    return "Top";
                if (id.Contains("Bottom"))
                    return "Bottom";
                if (id.Contains("Vertical"))
                    return "Vertical";
                if (id.Contains("Horizontal"))
                    return "Horizontal";
            }

            return $"Panel{index}";
        }

        /// <summary>
        /// Найти контейнер по пути в дереве
        /// </summary>
        /// <param name="root">Корневой контейнер</param>
        /// <param name="path">Путь для поиска</param>
        /// <returns>Найденный контейнер или null</returns>
        public IDock? FindContainerByPath(IDock root, string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            if (path == "Root")
                return root;

            var segments = path.Split('.');
            if (segments.Length == 0 || segments[0] != "Root")
            {
                _logger.LogWarning("Invalid path format: {Path}", path);
                return null;
            }

            IDockable current = root;

            for (int i = 1; i < segments.Length; i++)
            {
                var segment = segments[i];
                var found = false;

                if (current is IDock dock && dock.VisibleDockables != null)
                {
                    foreach (var child in dock.VisibleDockables)
                    {
                        if (child is ProportionalDockSplitter)
                            continue;

                        if (child.Id != null && (child.Id.EndsWith(segment) || child.Id.Contains(segment)))
                        {
                            current = child;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    _logger.LogDebug("Path segment not found: {Segment} in {Path}", segment, path);
                    return null;
                }
            }

            return current as IDock;
        }

        /// <summary>
        /// Вычислить глубину вложенности пути
        /// </summary>
        /// <param name="path">Путь для анализа</param>
        /// <returns>Глубина (количество уровней)</returns>
        public int CalculateDepth(string path)
        {
            if (string.IsNullOrEmpty(path))
                return 0;

            return path.Split('.').Length;
        }

        /// <summary>
        /// Получить родительский путь
        /// </summary>
        /// <param name="path">Путь дочернего контейнера</param>
        /// <returns>Путь родителя или null если это корень</returns>
        public string? GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "Root")
                return null;

            var lastDotIndex = path.LastIndexOf('.');
            if (lastDotIndex < 0)
                return "Root";

            return path.Substring(0, lastDotIndex);
        }

        /// <summary>
        /// Получить имя последнего сегмента пути
        /// </summary>
        /// <param name="path">Полный путь</param>
        /// <returns>Имя последнего сегмента</returns>
        public string GetSegmentName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";

            var lastDotIndex = path.LastIndexOf('.');
            if (lastDotIndex < 0)
                return path;

            return path.Substring(lastDotIndex + 1);
        }

        /// <summary>
        /// Проверить валидность пути
        /// </summary>
        /// <param name="path">Путь для проверки</param>
        /// <returns>true если путь валиден</returns>
        public bool IsValidPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            if (!path.StartsWith("Root") && !path.StartsWith("Float"))
                return false;

            var depth = CalculateDepth(path);
            if (depth > MaxDepth)
                return false;

            return true;
        }
    }
}