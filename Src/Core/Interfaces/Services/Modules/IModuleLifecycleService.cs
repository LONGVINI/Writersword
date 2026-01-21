using System.Threading.Tasks;
using Writersword.Core.Interfaces.Modules;

namespace Writersword.Core.Interfaces.Services
{
    /// <summary>
    /// Сервис управления жизненным циклом модулей
    /// Отвечает за открытие, закрытие и восстановление состояния модулей
    /// </summary>
    public interface IModuleLifecycleService
    {
        /// <summary>
        /// Закрыть модуль с сохранением его состояния в кеш
        /// Используется при:
        /// - Закрытии модуля пользователем (крестик)
        /// - Переключении WorkMode
        /// - Закрытии вкладки
        /// - Закрытии приложения
        /// </summary>
        /// <param name="module"ё>Модуль для закрытия</param>
        /// <param name="projectPath">Путь к проекту</param>
        /// <param name="projectId">GUID проекта</param>
        Task CloseModuleAsync(IModule module, string projectPath, string projectId);

        /// <summary>
        /// Открыть модуль с восстановлением его состояния из кеша
        /// Используется при:
        /// - Открытии модуля пользователем
        /// - Переключении WorkMode
        /// - Открытии вкладки
        /// </summary>
        /// <param name="module">Модуль для открытия</param>
        /// <param name="projectPath">Путь к проекту (для загрузки из кеша)</param>
        void RestoreModule(IModule module, string projectPath);
    }
}