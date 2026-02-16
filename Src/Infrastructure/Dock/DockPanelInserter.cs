using Avalonia;
using Avalonia.Controls;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using Writersword.Core.Enums;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Отвечает за вставку панелей в Dock по заданным позициям
    /// Использует детерминированные пути вместо случайных GUID
    /// Вынесен из DockFactory для изоляции логики работы с PreferredDockPosition
    /// </summary>
    public class DockPanelInserter
    {
        private readonly ILogger<DockPanelInserter> _logger;
        private readonly DockFactory _factory;
        private readonly ContainerPathBuilder _pathBuilder;

        public DockPanelInserter(DockFactory factory, ILogger<DockPanelInserter> logger)
        {
            _factory = factory;
            _logger = logger;
            _pathBuilder = new ContainerPathBuilder();
        }

        /// <summary>
        /// Найти или создать Dock для позиции
        /// </summary>
        public IDock? FindOrCreateDockForPosition(IRootDock rootDock, PreferredDockPosition position, bool asTab)
        {
            var mainDock = rootDock.VisibleDockables?.FirstOrDefault() as ProportionalDock;
            if (mainDock == null)
            {
                _logger.LogWarning("No main ProportionalDock found");
                return null;
            }

            return position switch
            {
                PreferredDockPosition.Right => FindOrCreateRightDock(mainDock, PreferredDockPosition.Right, asTab),
                PreferredDockPosition.TopRight => FindOrCreateRightDock(mainDock, PreferredDockPosition.TopRight, asTab),
                PreferredDockPosition.BottomRight => FindOrCreateRightDock(mainDock, PreferredDockPosition.BottomRight, asTab),
                PreferredDockPosition.Left => FindOrCreateLeftDock(mainDock, PreferredDockPosition.Left, asTab),
                PreferredDockPosition.TopLeft => FindOrCreateLeftDock(mainDock, PreferredDockPosition.TopLeft, asTab),
                PreferredDockPosition.BottomLeft => FindOrCreateLeftDock(mainDock, PreferredDockPosition.BottomLeft, asTab),
                PreferredDockPosition.Bottom => FindOrCreateBottomDock(mainDock, asTab),
                PreferredDockPosition.Top => FindOrCreateTopDock(mainDock, asTab),
                _ => null
            };
        }

        /// <summary>
        /// Найти или создать правый Dock
        /// </summary>
        private IDock? FindOrCreateRightDock(ProportionalDock mainDock, PreferredDockPosition position, bool asTab)
        {
            _logger.LogDebug("FindOrCreateRightDock: {Position}, asTab={AsTab}", position, asTab);

            ProportionalDock searchDock = mainDock;

            if (mainDock.Orientation == Orientation.Horizontal)
            {
                var rightElement = mainDock.VisibleDockables?.LastOrDefault(d => d is not ProportionalDockSplitter);

                if (rightElement is ProportionalDock nestedLayout && nestedLayout.Orientation == Orientation.Vertical)
                {
                    _logger.LogDebug("Found nested vertical layout: {Id}", nestedLayout.Id);
                    searchDock = nestedLayout;
                }
            }

            List<IDock> panels;
            if (searchDock.Orientation == Orientation.Vertical)
            {
                panels = CollectAllDocumentDocks(searchDock);
            }
            else
            {
                panels = FindPanelsInDirection(searchDock, "Right");
            }

            if (asTab && panels.Count > 0)
            {
                var targetPanel = position switch
                {
                    PreferredDockPosition.BottomRight => panels.Last(),
                    PreferredDockPosition.TopRight => panels.First(),
                    _ => panels.First()
                };

                _logger.LogDebug("Using existing panel for tab: {Id}", targetPanel.Id);
                return targetPanel;
            }

            if (!asTab && panels.Count > 0)
            {
                _logger.LogDebug("Creating new right panel in existing vertical split");

                var parentPath = searchDock.Id ?? "Root";
                var newPanelPath = _pathBuilder.BuildPathByDirection(parentPath, "Right", panels.Count);

                var newPanel = new DocumentDock
                {
                    Id = newPanelPath,
                    Title = "Right",
                    Proportion = 0.5,
                    CanCreateDocument = false,
                    Factory = _factory
                };

                if (newPanel.VisibleDockables == null)
                    newPanel.VisibleDockables = new List<IDockable>();

                if (searchDock.Orientation == Orientation.Vertical)
                {
                    if (searchDock.VisibleDockables == null)
                        searchDock.VisibleDockables = new List<IDockable>();

                    if (searchDock.VisibleDockables.Count > 0)
                    {
                        var splitter = new ProportionalDockSplitter
                        {
                            Id = _pathBuilder.BuildPath(parentPath, $"Splitter{searchDock.VisibleDockables.Count}"),
                            Title = "Splitter"
                        };
                        searchDock.VisibleDockables.Add(splitter);
                    }

                    if (position == PreferredDockPosition.TopRight)
                    {
                        searchDock.VisibleDockables.Insert(0, newPanel);
                    }
                    else
                    {
                        searchDock.VisibleDockables.Add(newPanel);
                    }

                    _logger.LogDebug("Added new panel to existing vertical split: {Id}", newPanel.Id);
                    return newPanel;
                }
            }

            _logger.LogDebug("Creating new vertical split with first panel");

            var mainPath = mainDock.Id ?? "Root";
            var firstPanelPath = _pathBuilder.BuildPath(mainPath, "Right");

            var firstPanel = new DocumentDock
            {
                Id = firstPanelPath,
                Title = "Right",
                Proportion = 0.5,
                CanCreateDocument = false,
                Factory = _factory
            };

            if (firstPanel.VisibleDockables == null)
                firstPanel.VisibleDockables = new List<IDockable>();

            InsertPanelInDirection(mainDock, firstPanel, "Right", position);
            return firstPanel;
        }

        /// <summary>
        /// Собрать все DocumentDock из ProportionalDock
        /// </summary>
        private static List<IDock> CollectAllDocumentDocks(ProportionalDock dock)
        {
            var result = new List<IDock>();
            if (dock.VisibleDockables == null) return result;

            foreach (var child in dock.VisibleDockables)
            {
                if (child is DocumentDock dd)
                {
                    result.Add(dd);
                }
                else if (child is ProportionalDock pd)
                {
                    result.AddRange(CollectAllDocumentDocks(pd));
                }
            }

            return result;
        }

        /// <summary>
        /// Найти или создать левый Dock
        /// </summary>
        private IDock? FindOrCreateLeftDock(ProportionalDock mainDock, PreferredDockPosition position, bool asTab)
        {
            _logger.LogDebug("FindOrCreateLeftDock: {Position}, asTab={AsTab}", position, asTab);

            var leftPanels = FindPanelsInDirection(mainDock, "Left");

            if (asTab && leftPanels.Count > 0)
            {
                return position switch
                {
                    PreferredDockPosition.BottomLeft => leftPanels.Last(),
                    PreferredDockPosition.TopLeft => leftPanels.First(),
                    PreferredDockPosition.Left => leftPanels.Last(),
                    _ => leftPanels.Last()
                };
            }

            var mainPath = mainDock.Id ?? "Root";
            var newPanelPath = _pathBuilder.BuildPath(mainPath, "Left");

            var newPanel = new DocumentDock
            {
                Id = newPanelPath,
                Title = "Left",
                Proportion = double.NaN,
                CanCreateDocument = false
            };

            if (newPanel.VisibleDockables == null)
                newPanel.VisibleDockables = new List<IDockable>();

            InsertPanelInDirection(mainDock, newPanel, "Left", position);
            return newPanel;
        }

        /// <summary>
        /// Найти или создать нижний Dock
        /// </summary>
        private IDock? FindOrCreateBottomDock(ProportionalDock mainDock, bool asTab)
        {
            _logger.LogDebug("FindOrCreateBottomDock: asTab={AsTab}", asTab);

            var bottomPanels = FindPanelsInDirection(mainDock, "Bottom");

            if (asTab && bottomPanels.Count > 0)
            {
                return bottomPanels.First();
            }

            var mainPath = mainDock.Id ?? "Root";
            var newPanelPath = _pathBuilder.BuildPath(mainPath, "Bottom");

            var newPanel = new DocumentDock
            {
                Id = newPanelPath,
                Title = "Bottom",
                Proportion = 0.3,
                CanCreateDocument = false
            };

            if (newPanel.VisibleDockables == null)
                newPanel.VisibleDockables = new List<IDockable>();

            InsertPanelInDirection(mainDock, newPanel, "Bottom", PreferredDockPosition.Bottom);
            return newPanel;
        }

        /// <summary>
        /// Найти или создать верхний Dock
        /// </summary>
        private IDock? FindOrCreateTopDock(ProportionalDock mainDock, bool asTab)
        {
            _logger.LogDebug("FindOrCreateTopDock: asTab={AsTab}", asTab);

            var topPanels = FindPanelsInDirection(mainDock, "Top");

            if (asTab && topPanels.Count > 0)
            {
                return topPanels.First();
            }

            var mainPath = mainDock.Id ?? "Root";
            var newPanelPath = _pathBuilder.BuildPath(mainPath, "Top");

            var newPanel = new DocumentDock
            {
                Id = newPanelPath,
                Title = "Top",
                Proportion = 0.3,
                CanCreateDocument = false
            };

            if (newPanel.VisibleDockables == null)
                newPanel.VisibleDockables = new List<IDockable>();

            InsertPanelInDirection(mainDock, newPanel, "Top", PreferredDockPosition.Top);
            return newPanel;
        }

        /// <summary>
        /// Найти панели в заданном направлении
        /// </summary>
        private static List<IDock> FindPanelsInDirection(ProportionalDock mainDock, string direction)
        {
            var panels = new List<IDock>();
            if (mainDock.VisibleDockables == null) return panels;

            switch (direction)
            {
                case "Right":
                    if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables.Count > 1)
                    {
                        var rightElement = mainDock.VisibleDockables.Last();
                        CollectDocksRecursive(rightElement, panels);
                    }
                    break;

                case "Left":
                    if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables.Count > 0)
                    {
                        var leftElement = mainDock.VisibleDockables.First();
                        CollectDocksRecursive(leftElement, panels);
                    }
                    break;

                case "Bottom":
                    if (mainDock.Orientation == Orientation.Vertical && mainDock.VisibleDockables.Count > 1)
                    {
                        var bottomElement = mainDock.VisibleDockables.Last();
                        CollectDocksRecursive(bottomElement, panels);
                    }
                    break;

                case "Top":
                    if (mainDock.Orientation == Orientation.Vertical && mainDock.VisibleDockables.Count > 0)
                    {
                        var topElement = mainDock.VisibleDockables.First();
                        CollectDocksRecursive(topElement, panels);
                    }
                    break;
            }

            return panels;
        }

        /// <summary>
        /// Рекурсивно собрать Dock из элемента
        /// </summary>
        private static void CollectDocksRecursive(IDockable element, List<IDock> result)
        {
            if (element is ProportionalDock propDock && propDock.VisibleDockables != null)
            {
                foreach (var child in propDock.VisibleDockables)
                {
                    if (child is IDock dock && !(child is ProportionalDock))
                    {
                        result.Add(dock);
                    }
                    else
                    {
                        CollectDocksRecursive(child, result);
                    }
                }
            }
            else if (element is IDock dock)
            {
                result.Add(dock);
            }
        }

        /// <summary>
        /// Вставить панель в заданном направлении
        /// </summary>
        private void InsertPanelInDirection(ProportionalDock mainDock, IDock newPanel, string direction, PreferredDockPosition position)
        {
            _logger.LogDebug("InsertPanelInDirection: {Direction}, position={Position}", direction, position);

            if (mainDock.VisibleDockables == null)
                mainDock.VisibleDockables = new List<IDockable>();

            switch (direction)
            {
                case "Right":
                    InsertRightPanel(mainDock, newPanel, position);
                    break;

                case "Left":
                    InsertLeftPanel(mainDock, newPanel, position);
                    break;

                case "Bottom":
                    InsertBottomPanel(mainDock, newPanel);
                    break;

                case "Top":
                    InsertTopPanel(mainDock, newPanel);
                    break;
            }
        }

        /// <summary>
        /// Вставить панель справа
        /// </summary>
        private void InsertRightPanel(ProportionalDock mainDock, IDock newPanel, PreferredDockPosition position)
        {
            if (mainDock.VisibleDockables == null)
            {
                _logger.LogWarning("mainDock.VisibleDockables is null, initializing");
                mainDock.VisibleDockables = new List<IDockable>();
            }

            newPanel.Proportion = 0.5;
            newPanel.Factory = _factory;

            if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables.Count > 1)
            {
                var rightElement = mainDock.VisibleDockables
                    .Where(d => d is not ProportionalDockSplitter)
                    .LastOrDefault();

                if (rightElement == null)
                {
                    _logger.LogWarning("No right element found");
                    return;
                }

                if (rightElement is ProportionalDock rightDock && rightDock.Orientation == Orientation.Vertical)
                {
                    if (rightDock.VisibleDockables == null)
                        rightDock.VisibleDockables = new List<IDockable>();

                    if (rightDock.VisibleDockables.Count > 0)
                    {
                        var rightPath = rightDock.Id ?? "Root.Right";
                        var splitter = new ProportionalDockSplitter
                        {
                            Id = _pathBuilder.BuildPath(rightPath, $"Splitter{rightDock.VisibleDockables.Count}"),
                            Title = "Splitter"
                        };
                        rightDock.VisibleDockables.Add(splitter);
                    }

                    if (position == PreferredDockPosition.TopRight)
                    {
                        rightDock.VisibleDockables.Insert(0, newPanel);
                    }
                    else
                    {
                        rightDock.VisibleDockables.Add(newPanel);
                    }

                    _logger.LogDebug("Added to existing right vertical split with splitter");
                    return;
                }

                var mainPath = mainDock.Id ?? "Root";
                var verticalPath = _pathBuilder.BuildPath(mainPath, "RightVertical");

                var verticalSplit = new ProportionalDock
                {
                    Id = verticalPath,
                    Orientation = Orientation.Vertical,
                    Proportion = rightElement is IDock dock ? dock.Proportion : 0.3,
                    Factory = _factory
                };

                if (verticalSplit.VisibleDockables == null)
                    verticalSplit.VisibleDockables = new List<IDockable>();

                if (position == PreferredDockPosition.TopRight)
                {
                    verticalSplit.VisibleDockables.Add(newPanel);

                    var splitterBetween = new ProportionalDockSplitter
                    {
                        Id = _pathBuilder.BuildPath(verticalPath, "Splitter0"),
                        Title = "Splitter"
                    };
                    verticalSplit.VisibleDockables.Add(splitterBetween);

                    verticalSplit.VisibleDockables.Add(rightElement);
                }
                else
                {
                    verticalSplit.VisibleDockables.Add(rightElement);

                    var splitterBetween = new ProportionalDockSplitter
                    {
                        Id = _pathBuilder.BuildPath(verticalPath, "Splitter0"),
                        Title = "Splitter"
                    };
                    verticalSplit.VisibleDockables.Add(splitterBetween);

                    verticalSplit.VisibleDockables.Add(newPanel);
                }

                var index = mainDock.VisibleDockables.IndexOf(rightElement);
                if (index >= 0 && index < mainDock.VisibleDockables.Count)
                {
                    mainDock.VisibleDockables[index] = verticalSplit;
                    _logger.LogDebug("Created new right vertical split with splitter");
                }
                else
                {
                    _logger.LogError("Invalid index for rightElement: {Index}", index);
                }
            }
            else
            {
                if (mainDock.Orientation != Orientation.Horizontal)
                {
                    mainDock.Orientation = Orientation.Horizontal;
                }

                var mainPath = mainDock.Id ?? "Root";

                if (position == PreferredDockPosition.TopRight || position == PreferredDockPosition.BottomRight)
                {
                    var verticalPath = _pathBuilder.BuildPath(mainPath, "RightVertical");

                    var verticalSplit = new ProportionalDock
                    {
                        Id = verticalPath,
                        Orientation = Orientation.Vertical,
                        Proportion = 0.3,
                        Factory = _factory
                    };

                    if (verticalSplit.VisibleDockables == null)
                        verticalSplit.VisibleDockables = new List<IDockable>();

                    verticalSplit.VisibleDockables.Add(newPanel);

                    var splitter = new ProportionalDockSplitter
                    {
                        Id = _pathBuilder.BuildPath(mainPath, $"Splitter{mainDock.VisibleDockables.Count}"),
                        Title = $"Splitter{mainDock.VisibleDockables.Count}"
                    };

                    mainDock.VisibleDockables.Add(splitter);
                    mainDock.VisibleDockables.Add(verticalSplit);

                    _logger.LogDebug("Created first right panel inside RightVertical container");
                }
                else
                {
                    var splitter = new ProportionalDockSplitter
                    {
                        Id = _pathBuilder.BuildPath(mainPath, $"Splitter{mainDock.VisibleDockables.Count}"),
                        Title = $"Splitter{mainDock.VisibleDockables.Count}"
                    };

                    newPanel.Proportion = 0.3;

                    mainDock.VisibleDockables.Add(splitter);
                    mainDock.VisibleDockables.Add(newPanel);

                    _logger.LogDebug("Added first right panel with splitter");
                }
            }
        }

        /// <summary>
        /// Вставить панель слева
        /// </summary>
        private void InsertLeftPanel(ProportionalDock mainDock, IDock newPanel, PreferredDockPosition position)
        {
            if (mainDock.VisibleDockables == null)
            {
                _logger.LogWarning("mainDock.VisibleDockables is null, initializing");
                mainDock.VisibleDockables = new List<IDockable>();
            }

            if (mainDock.Orientation == Orientation.Horizontal && mainDock.VisibleDockables.Count > 0)
            {
                var leftElement = mainDock.VisibleDockables.First();

                if (leftElement is ProportionalDock leftDock && leftDock.Orientation == Orientation.Vertical)
                {
                    if (leftDock.VisibleDockables == null)
                        leftDock.VisibleDockables = new List<IDockable>();

                    if (position == PreferredDockPosition.TopLeft)
                    {
                        leftDock.VisibleDockables.Insert(0, newPanel);
                    }
                    else
                    {
                        leftDock.VisibleDockables.Add(newPanel);
                    }

                    _logger.LogDebug("Added to existing left vertical split");
                    return;
                }

                var mainPath = mainDock.Id ?? "Root";
                var verticalPath = _pathBuilder.BuildPath(mainPath, "LeftVertical");

                var verticalSplit = new ProportionalDock
                {
                    Id = verticalPath,
                    Orientation = Orientation.Vertical,
                    Proportion = leftElement is IDock dock ? dock.Proportion : 0.7,
                    Factory = _factory
                };

                if (verticalSplit.VisibleDockables == null)
                    verticalSplit.VisibleDockables = new List<IDockable>();

                verticalSplit.VisibleDockables.Add(leftElement);

                if (position == PreferredDockPosition.TopLeft)
                {
                    verticalSplit.VisibleDockables.Insert(0, newPanel);
                }
                else
                {
                    verticalSplit.VisibleDockables.Add(newPanel);
                }

                mainDock.VisibleDockables[0] = verticalSplit;

                _logger.LogDebug("Created new left vertical split");
            }
            else
            {
                if (mainDock.Orientation != Orientation.Horizontal)
                {
                    mainDock.Orientation = Orientation.Horizontal;
                }

                newPanel.Proportion = 0.3;
                newPanel.Factory = _factory;

                mainDock.VisibleDockables.Insert(0, newPanel);

                _logger.LogDebug("Added first left panel");
            }
        }

        /// <summary>
        /// Вставить панель снизу
        /// </summary>
        private void InsertBottomPanel(ProportionalDock mainDock, IDock newPanel)
        {
            if (mainDock.VisibleDockables == null)
                mainDock.VisibleDockables = new List<IDockable>();

            var currentContent = mainDock.VisibleDockables.ToList();
            mainDock.VisibleDockables.Clear();

            var mainPath = mainDock.Id ?? "Root";
            var contentPath = _pathBuilder.BuildPath(mainPath, "Content");

            var contentDock = new ProportionalDock
            {
                Id = contentPath,
                Orientation = mainDock.Orientation,
                Proportion = 0.7,
                Factory = _factory
            };

            if (contentDock.VisibleDockables == null)
                contentDock.VisibleDockables = new List<IDockable>();

            foreach (var item in currentContent)
            {
                contentDock.VisibleDockables.Add(item);
            }

            mainDock.Orientation = Orientation.Vertical;

            newPanel.Factory = _factory;

            mainDock.VisibleDockables.Add(contentDock);
            mainDock.VisibleDockables.Add(newPanel);

            _logger.LogDebug("Added bottom panel with vertical split");
        }

        /// <summary>
        /// Вставить панель сверху
        /// </summary>
        private void InsertTopPanel(ProportionalDock mainDock, IDock newPanel)
        {
            if (mainDock.VisibleDockables == null)
                mainDock.VisibleDockables = new List<IDockable>();

            var currentContent = mainDock.VisibleDockables.ToList();
            mainDock.VisibleDockables.Clear();

            var mainPath = mainDock.Id ?? "Root";
            var contentPath = _pathBuilder.BuildPath(mainPath, "Content");

            var contentDock = new ProportionalDock
            {
                Id = contentPath,
                Orientation = mainDock.Orientation,
                Proportion = 0.7,
                Factory = _factory
            };

            if (contentDock.VisibleDockables == null)
                contentDock.VisibleDockables = new List<IDockable>();

            foreach (var item in currentContent)
            {
                contentDock.VisibleDockables.Add(item);
            }

            mainDock.Orientation = Orientation.Vertical;

            newPanel.Factory = _factory;

            mainDock.VisibleDockables.Add(newPanel);
            mainDock.VisibleDockables.Add(contentDock);

            _logger.LogDebug("Added top panel with vertical split");
        }
    }
}