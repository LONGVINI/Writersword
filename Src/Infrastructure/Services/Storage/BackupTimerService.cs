using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Reactive.Linq;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Backup;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Таймер истории версий.
    ///
    /// Раньше точки во время работы снимались на автосохранении, и частота
    /// истории зависела от чужой настройки: поставив автосохранение на десять
    /// секунд, пользователь получал историю с той же частотой, а выключив его
    /// совсем — не получал точек вообще. Задачи у этих механизмов разные:
    /// автосохранение защищает от падения и должно быть частым, история
    /// отвечает на вопрос «к чему вернуться» и должна быть редкой.
    ///
    /// Сервис тикает раз в минуту и предлагает снять точку. Решение принимает
    /// BackupService: он знает и настройки, и время последней точки, и то,
    /// менялось ли содержимое. Дублировать эту логику здесь незачем.
    /// </summary>
    public class BackupTimerService : IBackupTimerService
    {
        private readonly ILogger<BackupTimerService> _logger;
        private readonly IBackupService _backupService;
        private readonly ITabCollection _tabCollection;

        private IDisposable? _timer;

        /// <summary>
        /// Заслонка от наложения тиков. Снимок большого проекта может занять
        /// дольше шага таймера, и без неё вторая проверка стартовала бы поверх
        /// первой, упираясь в шлюз хранилища и множа очередь.
        /// </summary>
        private int _running;

        /// <summary>Шаг проверки. Реальную частоту точек задаёт интервал в настройках.</summary>
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

        public BackupTimerService(IBackupService backupService, ITabCollection tabCollection)
        {
            _logger = App.Services.GetService<ILogger<BackupTimerService>>()!;
            _backupService = backupService;
            _tabCollection = tabCollection;
        }

        public void Start()
        {
            Stop();

            _timer = Observable.Interval(TickInterval)
                .Subscribe(async _ => await TickAsync());

            _logger.LogDebug("Backup timer started, tick {Minutes} min", TickInterval.TotalMinutes);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private async System.Threading.Tasks.Task TickAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                _logger.LogDebug("Backup timer tick skipped: previous one is still running");
                return;
            }

            try
            {
                var tab = _tabCollection.ActiveTab;

                if (tab == null || string.IsNullOrEmpty(tab.FilePath))
                    return;

                // В режиме сравнения на экране может быть чужая версия —
                // снимать с неё точку нельзя.
                if (tab.Context?.IsInCompareMode == true)
                    return;

                await _backupService.CreateSnapshotAsync(tab.FilePath, BackupTrigger.AutoSave);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup timer tick failed");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _running, 0);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
