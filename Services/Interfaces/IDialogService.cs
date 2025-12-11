using System.Threading.Tasks;

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
    }
}
