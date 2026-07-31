using System;

namespace Writersword.Core.Interfaces.Services.Storage
{
    /// <summary>
    /// Таймер истории версий: периодически предлагает снять точку с активного
    /// проекта. Частоту точек определяют настройки истории, а не шаг таймера.
    /// </summary>
    public interface IBackupTimerService : IDisposable
    {
        /// <summary>Запустить проверки.</summary>
        void Start();

        /// <summary>Остановить проверки.</summary>
        void Stop();
    }
}
