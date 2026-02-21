using System;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Views;

namespace Writersword.Src.Core.Interfaces.Services.UI
{
    /// <summary>
    /// Сервис для диалоговых окон
    /// </summary>
    public interface IDialogService
    {
        /// <summary>Диалог открытия файла</summary>
        Task<string?> OpenFileAsync();

        /// <summary>Диалог сохранения файла</summary>
        Task<string?> SaveFileAsync(string? defaultFileName = null);

        /// <summary>Показать сообщение</summary>
        Task ShowMessageAsync(string title, string message);

        /// <summary>Показать сообщение с типом и кнопками</summary>
        Task<MessageBoxResult> ShowMessageAsync(string title, string message, MessageBoxType type, MessageBoxButtons buttons);

        /// <summary>
        /// Показать диалог восстановления проекта из автосохранения
        /// </summary>
        /// <param name="cacheDate">Дата автосохранения</param>
        /// <param name="saveDate">Дата последнего сохранения</param>
        /// <returns>Выбор пользователя (Restore/OpenSaved/Compare/Cancel)</returns>
        Task<RecoveryDialogResult> ShowRecoveryDialogAsync(DateTime cacheDate, DateTime saveDate);
    }
}