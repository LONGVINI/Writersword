using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Infrastructure.Controls;
using Writersword.Infrastructure.Dock;

namespace Writersword.Infrastructure.Services.UI
{
    /// <summary>
    /// Пометки на вкладках модулей. Кладёт значение в прикреплённые свойства
    /// ModuleWarning на dockable модуля; рисует их шаблон заголовка вкладки в
    /// Styles/DockStyles.axaml.
    ///
    /// Ничего не хранит. Состояние живёт на самом dockable, и это правильно:
    /// вкладка исчезла — исчезло и предупреждение, чистить нечего.
    /// </summary>
    public class ModuleBadgeService : IModuleBadgeService
    {
        private readonly ILogger<ModuleBadgeService> _logger;

        public ModuleBadgeService(ILogger<ModuleBadgeService> logger) => _logger = logger;

        public void SetWarning(string moduleType, string? text)
        {
            if (string.IsNullOrEmpty(moduleType)) return;

            // Прикреплённые свойства — часть визуального дерева, менять их можно
            // только на потоке интерфейса. Зовут этот метод в том числе с фонового:
            // проверка шрифтов идёт после разбора документа.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => SetWarning(moduleType, text));
                return;
            }

            try
            {
                var factory = App.Services.GetService<DockFactory>();
                var document = factory?.FindModuleDocument(moduleType);

                if (document is null)
                {
                    _logger.LogDebug("Badge skipped, module not in layout: {ModuleType}", moduleType);
                    return;
                }

                ModuleWarning.Set(document, text);

                _logger.LogDebug("Badge {State} for {ModuleType}",
                    string.IsNullOrWhiteSpace(text) ? "cleared" : "set", moduleType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set module badge: {ModuleType}", moduleType);
            }
        }
    }
}
