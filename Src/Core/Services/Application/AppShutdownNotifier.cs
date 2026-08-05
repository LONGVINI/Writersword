using System;
using Writersword.Core.Interfaces.Services;

namespace Writersword.Core.Services
{
    /// <summary>
    /// Источник оповещения о штатном закрытии приложения.
    ///
    /// Ничего не знает ни о модулях, ни о том, что они станут делать: его работа —
    /// объявить факт и не дать одному упавшему подписчику сорвать остальных. Порядок
    /// вызова подписчиков не определён, и полагаться на него нельзя.
    /// </summary>
    public sealed class AppShutdownNotifier : IAppShutdownNotifier
    {
        private readonly object _lock = new();
        private bool _notified;

        public event Action? GracefulShutdown;

        public void NotifyGracefulShutdown()
        {
            // Закрытие объявляется один раз: обработчик закрытия окна может сработать
            // повторно (отмена закрытия и повторная попытка), а уборка у подписчиков
            // рассчитана на однократный запуск.
            lock (_lock)
            {
                if (_notified) return;
                _notified = true;
            }

            var handlers = GracefulShutdown;
            if (handlers is null) return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action)handler)(); }
                catch { /* один подписчик не должен мешать остальным закрыться */ }
            }
        }
    }
}
