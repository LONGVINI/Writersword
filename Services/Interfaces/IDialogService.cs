using System.Threading.Tasks;
using Writersword.Views;

namespace Writersword.Services.Interfaces
{
    /// <summary>
    /// Сервис для диалоговых окон
    /// </summary>
    public interface IDialogService
    {
        /// <summary>Диалог открытия файла</summary>
        Task<string?> OpenFileAsync();

        /// <summary>Диалог сохранения файла</summary>
        Task<string?> SaveFileAsync();

        /// <summary>Показать сообщение</summary>
        Task ShowMessageAsync(string title, string message);
        /// <summary> Показать сообщение с типом и кнопками </summary>
        Task<MessageBoxResult> ShowMessageAsync(string title, string message, MessageBoxType type, MessageBoxButtons buttons);
    }
}
