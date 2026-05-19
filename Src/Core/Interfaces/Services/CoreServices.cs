using Microsoft.Extensions.DependencyInjection;
using System;

namespace Writersword.Core.Services
{
    /// <summary>
    /// Статический провайдер сервисов для модулей.
    /// Устанавливается один раз при старте App.
    /// Позволяет BaseModule не зависеть напрямую от App.
    /// </summary>
    public static class CoreServices
    {
        private static IServiceProvider? _provider;

        public static void SetProvider(IServiceProvider provider)
        {
            _provider = provider;
        }

        public static T? GetService<T>() where T : class
        {
            return _provider?.GetService<T>();
        }

        public static T GetRequiredService<T>() where T : class
        {
            if (_provider == null)
                throw new InvalidOperationException("CoreServices not initialized.");
            return _provider.GetRequiredService<T>();
        }
    }
}