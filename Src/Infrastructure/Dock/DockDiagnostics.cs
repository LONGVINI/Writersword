using Dock.Model.Avalonia;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Reflection;

namespace Writersword.Src.Infrastructure.Dock
{
    /// <summary>
    /// Диагностика Dock системы для отладки Float проблемы
    /// </summary>
    public static class DockDiagnostics
    {
        private static ILogger? _logger;

        private static ILogger Logger
        {
            get
            {
                if (_logger == null)
                {
                    _logger = App.Services.GetService<ILogger<DockFactory>>()!;
                }
                return _logger;
            }
        }

        /// <summary>
        /// Вывести все методы Factory которые связаны с HostWindow
        /// </summary>
        public static void InspectFactoryMethods()
        {
            Logger.LogDebug("=== DOCK FACTORY METHODS ===");

            Type factoryType = typeof(Factory);

            var methods = factoryType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                if (method.Name.Contains("Host") || method.Name.Contains("Window") || method.Name.Contains("Float"))
                {
                    Logger.LogDebug("Method: {MethodName}", method.Name);
                    Logger.LogDebug("  Returns: {ReturnType}", method.ReturnType.Name);

                    var parameters = method.GetParameters();
                    if (parameters.Length > 0)
                    {
                        Logger.LogDebug("  Parameters:");
                        foreach (var param in parameters)
                        {
                            Logger.LogDebug("    - {ParameterType} {ParameterName}", param.ParameterType.Name, param.Name);
                        }
                    }
                }
            }

            Logger.LogDebug("=== HOST WINDOW LOCATOR TYPE ===");
            var locatorProperty = factoryType.GetProperty("HostWindowLocator");
            if (locatorProperty != null)
            {
                Logger.LogDebug("Property Type: {PropertyType}", locatorProperty.PropertyType.FullName);
            }

            Logger.LogDebug("=== END DIAGNOSTICS ===");
        }

        /// <summary>
        /// Проверить что Factory правильно установлена в RootDock
        /// </summary>
        public static void InspectRootDock(IRootDock? rootDock)
        {
            if (rootDock == null)
            {
                Logger.LogWarning("RootDock is NULL!");
                return;
            }

            Logger.LogDebug("RootDock.Factory: {Factory}", rootDock.Factory != null ? "SET" : "NULL");
            Logger.LogDebug("RootDock.Id: {Id}", rootDock.Id);
            Logger.LogDebug("RootDock.ActiveDockable: {ActiveDockable}", rootDock.ActiveDockable?.Id ?? "NULL");

            if (rootDock.Factory != null)
            {
                var factoryType = rootDock.Factory.GetType();
                Logger.LogDebug("Factory Type: {FactoryType}", factoryType.FullName);

                // Проверяем HostWindowLocator
                var locatorProp = factoryType.GetProperty("HostWindowLocator", BindingFlags.Public | BindingFlags.Instance);
                if (locatorProp != null)
                {
                    var locatorValue = locatorProp.GetValue(rootDock.Factory);
                    Logger.LogDebug("HostWindowLocator: {HostWindowLocator}", locatorValue != null ? "SET" : "NULL");

                    if (locatorValue != null)
                    {
                        Logger.LogDebug("HostWindowLocator Type: {LocatorType}", locatorValue.GetType().FullName);
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
                Logger.LogWarning("Document is NULL!");
                return;
            }

            Logger.LogDebug("Document.Id: {Id}", dockable.Id);
            Logger.LogDebug("Document.Title: {Title}", dockable.Title);
            Logger.LogDebug("Document.CanFloat: {CanFloat}", dockable.CanFloat);
            Logger.LogDebug("Document.CanClose: {CanClose}", dockable.CanClose);
            Logger.LogDebug("Document.Owner: {Owner}", dockable.Owner != null ? dockable.Owner.Id : "NULL");
            Logger.LogDebug("Document.Factory: {Factory}", dockable.Factory != null ? "SET" : "NULL");
        }
    }
}