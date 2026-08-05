using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Backup;
using Writersword.Modules.TextEditor;

namespace Writersword.ViewModels.Components.MenuBar
{
    public partial class MenuBarViewModel
    {
        // TODO: Statistics

        /// <summary>
        /// Убирает из проекта файлы картинок, на которые не осталось живых ссылок.
        /// Запускается только вручную: фоновая уборка удаляла файлы, которые
        /// возвращались по Ctrl+Z или из версии восстановления.
        /// Перед уборкой создаётся точка истории — ошибку можно откатить.
        /// </summary>
        private async Task CompactProject()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath))
            {
                _logger.LogDebug("CompactProject: no active project");
                return;
            }

            // В режиме сравнения на экране чужая версия документа, и её ссылки
            // не описывают того, что останется после выбора версии.
            if (activeTab.Context?.IsInCompareMode == true)
            {
                _notificationService.ShowWarning(
                    "Сжатие недоступно, пока открыты две версии проекта — сначала выберите версию");
                return;
            }

            var module = activeTab.ModuleContext.GetModule("TextEditor") as TextEditorModule;
            if (module == null)
            {
                _logger.LogDebug("CompactProject: TextEditor module is not loaded");
                _notificationService.ShowWarning("Откройте текстовый редактор — сжатие работает по его документу");
                return;
            }

            var confirm = await _dialogService.ShowMessageAsync(
                "Сжать проект?",
                "Из проекта будут удалены файлы картинок, на которые больше нет ссылок "
                + "ни в документе, ни в истории отмены. Перед уборкой создаётся точка истории. Продолжить?",
                MessageBoxType.Question, MessageBoxButtons.YesNo);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                // Точка истории до уборки копирует все файлы проекта, поэтому откат
                // возвращает и удалённые картинки.
                var backupService = App.Services.GetService<IBackupService>();
                if (backupService != null)
                    await backupService.CreateSnapshotAsync(activeTab.FilePath!, BackupTrigger.UserPoint);

                var (removed, freed) = module.CompactUnusedImages(activeTab.FilePath);

                if (removed == 0)
                {
                    _notificationService.ShowInfo("Лишних файлов не найдено — проект уже чистый");
                    return;
                }

                double freedMb = freed / 1024.0 / 1024.0;
                _notificationService.ShowSuccess(
                    $"Удалено файлов: {removed}, освобождено {freedMb:0.##} МБ");

                _logger.LogInformation(
                    "Project compacted: {Count} files, {Freed} bytes freed", removed, freed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CompactProject failed");
                _notificationService.ShowError("Не удалось сжать проект — подробности в журнале");
            }
        }
    }
}
