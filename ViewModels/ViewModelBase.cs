using ReactiveUI;
using System.Reactive.Disposables;

namespace Writersword
{
    /// <summary>
    /// Базовый класс всех ViewModels.
    /// Предоставляет CompositeDisposable для освобождения Rx-подписок через DisposeWith().
    /// </summary>
    public class ViewModelBase : ReactiveObject, System.IDisposable
    {
        protected readonly CompositeDisposable _disposables = new CompositeDisposable();
        private bool _disposed;

        public virtual void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _disposables.Dispose();
        }
    }
}