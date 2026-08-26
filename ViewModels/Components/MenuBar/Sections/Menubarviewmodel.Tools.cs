using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Backup;
using Writersword.Core.Models.Project;

namespace Writersword.ViewModels.Components.MenuBar
{
    public partial class MenuBarViewModel
    {
        // TODO: Statistics

        /// <summary>
        /// Убирает из проекта файлы, на которые не осталось живых ссылок.
        ///
        /// Запускается только вручную: фоновая уборка удаляла файлы, которые
        /// возвращались по Ctrl+Z или из версии восстановления. Перед уборкой
        /// создаётся точка истории — ошибку можно откатить.
        ///
        /// Спрашиваются все модули проекта, а не один текстовый редактор: файлы
        /// держит не только он, и уборка «по документу» оставляла бы остальное
        /// нетронутым, называясь при этом сжатием проекта.
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

            var modules = ProjectModules(activeTab);
            if (modules.Count == 0)
            {
                _notificationService.ShowWarning(
                    "Ни один модуль с файлами не открыт — сжимать нечего");
                return;
            }

            var confirm = await _dialogService.ShowMessageAsync(
                "Сжать проект?",
                "Из проекта будут удалены файлы, на которые больше нет ссылок "
                + "ни в документе, ни в истории отмены. Картинки, лежащие в папках проекта, "
                + "остаются: это набор, собранный вами, а не мусор. "
                + "Перед уборкой создаётся точка истории. Продолжить?",
                MessageBoxType.Question, MessageBoxButtons.YesNo);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                // Точка истории до уборки копирует все файлы проекта, поэтому откат
                // возвращает и удалённые картинки.
                var backupService = App.Services.GetService<IBackupService>();
                if (backupService != null)
                    await backupService.CreateSnapshotAsync(activeTab.FilePath!, BackupTrigger.UserPoint);

                var assets = App.Services.GetService<IProjectAssetService>();
                if (assets == null)
                {
                    _notificationService.ShowError("Служба файлов проекта недоступна");
                    return;
                }

                var result = assets.CompactAll(modules);

                if (result.Removed == 0)
                {
                    _notificationService.ShowInfo("Лишних файлов не найдено — проект уже чистый");
                    return;
                }

                double freedMb = result.FreedBytes / 1024.0 / 1024.0;
                _notificationService.ShowSuccess(
                    $"Удалено файлов: {result.Removed}, освобождено {freedMb:0.##} МБ");

                _logger.LogInformation(
                    "Project compacted: {Count} files, {Freed} bytes freed",
                    result.Removed, result.FreedBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CompactProject failed");
                _notificationService.ShowError("Не удалось сжать проект — подробности в журнале");
            }
        }

        /// <summary>
        /// Готовит проект к передаче другому человеку: находит всё, что лежит
        /// снаружи файла проекта, и укладывает копии внутрь.
        ///
        /// Проверка нужна потому, что снаружи файлы оказываются незаметно.
        /// Картинка из вашей библиотеки, бумага вида чтения, выбранная на диске,
        /// аватарка из общей папки — у вас всё это открывается, а у того, кому
        /// вы отдали проект, на их месте пустое место, и объяснить его нечем.
        ///
        /// Исходники не трогаются: библиотека и общие папки остаются вашими и
        /// доступны в других проектах. Укладывается копия.
        /// </summary>
        private async Task PrepareProjectForSharing()
        {
            var activeTab = _getActiveTab?.Invoke();
            if (activeTab == null || string.IsNullOrEmpty(activeTab.FilePath))
            {
                _logger.LogDebug("PrepareProjectForSharing: no active project");
                return;
            }

            if (activeTab.Context?.IsInCompareMode == true)
            {
                _notificationService.ShowWarning(
                    "Подготовка недоступна, пока открыты две версии проекта — сначала выберите версию");
                return;
            }

            var assets = App.Services.GetService<IProjectAssetService>();
            if (assets == null)
            {
                _notificationService.ShowError("Служба файлов проекта недоступна");
                return;
            }

            var modules = ProjectModules(activeTab);
            if (modules.Count == 0)
            {
                _notificationService.ShowWarning(
                    "Ни один модуль с файлами не открыт — проверять нечего");
                return;
            }

            try
            {
                var report = assets.Inspect(modules);

                // О недостающих файлах говорится всегда, даже когда укладывать
                // нечего: это единственное место, где о них вообще можно узнать.
                // Молчание здесь читалось бы как «всё на месте».
                if (report.IsSelfContained)
                {
                    if (report.Missing.Count > 0)
                    {
                        await _dialogService.ShowMessageAsync(
                            "Проект готов к передаче",
                            "Всё, что видно в проекте, лежит внутри него.\n\n"
                            + "Но по нескольким ссылкам файлов уже нет — их не покажет и ваша "
                            + "программа:\n\n" + Enumerate(report.Missing),
                            MessageBoxType.Warning, MessageBoxButtons.OK);
                        return;
                    }

                    _notificationService.ShowSuccess(
                        "Проект самодостаточен — всё, что в нём видно, лежит внутри");
                    return;
                }

                var text = new StringBuilder();
                text.Append("Эти файлы лежат вне проекта и у того, кому вы его передадите, "
                    + "не покажутся:\n\n");
                text.Append(Enumerate(report.Outside));

                double mb = report.OutsideBytes / 1024.0 / 1024.0;
                text.Append($"\nКопии лягут в файл проекта, он вырастет примерно на {mb:0.##} МБ. ");
                text.Append("Исходники останутся на местах и будут доступны в других проектах.");

                // О шрифтах говорится отдельно, и это не формальность. Файл
                // шрифта внутри проекта уезжает вместе с ним, то есть
                // распространяется, а лицензии на шрифты такое разрешают далеко
                // не всегда. Решать это за человека программа не вправе —
                // сказать обязана.
                if (report.Outside.Any(r => r.Kind == ProjectAssetKind.Font))
                    text.Append("\n\nСреди них есть шрифты. Файл шрифта внутри проекта уедет "
                        + "вместе с ним, а это уже распространение шрифта: лицензии разрешают "
                        + "его не всегда. В систему получателя ничего не установится — шрифт "
                        + "будет читаться из проекта и работать только в Writersword.");

                if (report.Missing.Count > 0)
                    text.Append("\n\nКроме того, по этим ссылкам файлов уже нет — уложить их "
                        + "нечем:\n\n" + Enumerate(report.Missing));

                text.Append("\nУложить копии в проект?");

                var confirm = await _dialogService.ShowMessageAsync(
                    "Подготовить проект к передаче?", text.ToString(),
                    MessageBoxType.Question, MessageBoxButtons.YesNo);

                if (confirm != MessageBoxResult.Yes) return;

                var embedded = await assets.EmbedAllAsync(modules);

                if (embedded == 0)
                {
                    _notificationService.ShowWarning(
                        "Уложить не удалось ни одного файла — подробности в журнале");
                    return;
                }

                activeTab.MarkAsModified();

                _notificationService.ShowSuccess(
                    $"В проект уложено файлов: {embedded}. Сохраните проект, чтобы они попали в файл");

                _logger.LogInformation("Project prepared for sharing: {Count} files embedded", embedded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PrepareProjectForSharing failed");
                _notificationService.ShowError(
                    "Не удалось подготовить проект — подробности в журнале");
            }
        }

        /// <summary>
        /// Список файлов для окна: по строке на файл, с именем того, кто его
        /// держит. Длинный список подрезается — читать сотню строк в окне
        /// сообщения всё равно никто не станет, а число остатка называется.
        /// </summary>
        private static string Enumerate(IReadOnlyList<ProjectAssetRef> items)
        {
            const int Shown = 12;

            var text = new StringBuilder();
            foreach (var item in items.Take(Shown))
            {
                text.Append("  • ");
                text.Append(item.DisplayName);

                if (!string.IsNullOrEmpty(item.OwnerName))
                {
                    text.Append(" — ");
                    text.Append(item.OwnerName);
                }

                text.Append('\n');
            }

            if (items.Count > Shown)
                text.Append($"  …и ещё {items.Count - Shown}\n");

            return text.ToString();
        }

        /// <summary>
        /// Модули открытого проекта, которые держат файлы. Пустой список значит,
        /// что спрашивать некого: модули создаются по мере открытия, и проект,
        /// у которого не открыт ни один из них, о своих файлах ничего не скажет.
        /// </summary>
        private static List<IModule> ProjectModules(DocumentTabViewModel tab)
        {
            var all = tab.ModuleContext?.GetAllModules();
            if (all == null) return new List<IModule>();

            return all.Where(m => m is IProjectAssetHolder).ToList();
        }
    }
}
