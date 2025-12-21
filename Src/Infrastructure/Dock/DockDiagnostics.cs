using Dock.Model.Avalonia;
using Dock.Model.Controls;
using Dock.Model.Core;
using System;
using System.Reflection;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Диагностика Dock системы для отладки Float проблемы
    /// </summary>
    public static class DockDiagnostics
    {
        /// <summary>
        /// Вывести все методы Factory которые связаны с HostWindow
        /// </summary>
        public static void InspectFactoryMethods()
        {
            Console.WriteLine("=== DOCK FACTORY METHODS ===");

            Type factoryType = typeof(Factory);

            var methods = factoryType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                if (method.Name.Contains("Host") || method.Name.Contains("Window") || method.Name.Contains("Float"))
                {
                    Console.WriteLine($"Method: {method.Name}");
                    Console.WriteLine($"  Returns: {method.ReturnType.Name}");

                    var parameters = method.GetParameters();
                    if (parameters.Length > 0)
                    {
                        Console.WriteLine($"  Parameters:");
                        foreach (var param in parameters)
                        {
                            Console.WriteLine($"    - {param.ParameterType.Name} {param.Name}");
                        }
                    }
                    Console.WriteLine();
                }
            }

            Console.WriteLine("=== HOST WINDOW LOCATOR TYPE ===");
            var locatorProperty = factoryType.GetProperty("HostWindowLocator");
            if (locatorProperty != null)
            {
                Console.WriteLine($"Property Type: {locatorProperty.PropertyType.FullName}");
            }

            Console.WriteLine("=== END DIAGNOSTICS ===");
        }

        /// <summary>
        /// Проверить что Factory правильно установлена в RootDock
        /// </summary>
        public static void InspectRootDock(IRootDock? rootDock)
        {
            if (rootDock == null)
            {
                Console.WriteLine("[Diagnostics] RootDock is NULL!");
                return;
            }

            Console.WriteLine($"[Diagnostics] RootDock.Factory: {(rootDock.Factory != null ? "SET" : "NULL")}");
            Console.WriteLine($"[Diagnostics] RootDock.Id: {rootDock.Id}");
            Console.WriteLine($"[Diagnostics] RootDock.ActiveDockable: {rootDock.ActiveDockable?.Id ?? "NULL"}");

            if (rootDock.Factory != null)
            {
                var factoryType = rootDock.Factory.GetType();
                Console.WriteLine($"[Diagnostics] Factory Type: {factoryType.FullName}");

                // Проверяем HostWindowLocator
                var locatorProp = factoryType.GetProperty("HostWindowLocator", BindingFlags.Public | BindingFlags.Instance);
                if (locatorProp != null)
                {
                    var locatorValue = locatorProp.GetValue(rootDock.Factory);
                    Console.WriteLine($"[Diagnostics] HostWindowLocator: {(locatorValue != null ? "SET" : "NULL")}");

                    if (locatorValue != null)
                    {
                        Console.WriteLine($"[Diagnostics] HostWindowLocator Type: {locatorValue.GetType().FullName}");
                    }
                }
            }
        }

        /// <summary>
        /// Вывести информацию о Document
        /// </summary>
        public static void InspectDocument(IDockable? dockable)
        {
            if (dockable == null)
            {
                Console.WriteLine("[Diagnostics] Document is NULL!");
                return;
            }

            Console.WriteLine($"[Diagnostics] Document.Id: {dockable.Id}");
            Console.WriteLine($"[Diagnostics] Document.Title: {dockable.Title}");
            Console.WriteLine($"[Diagnostics] Document.CanFloat: {dockable.CanFloat}");
            Console.WriteLine($"[Diagnostics] Document.CanClose: {dockable.CanClose}");
            Console.WriteLine($"[Diagnostics] Document.Owner: {(dockable.Owner != null ? dockable.Owner.Id : "NULL")}");
            Console.WriteLine($"[Diagnostics] Document.Factory: {(dockable.Factory != null ? "SET" : "NULL")}");
        }
    }
}