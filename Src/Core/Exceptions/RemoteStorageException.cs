using System;

namespace Writersword.Core.Exceptions
{
    /// <summary>
    /// Ошибка удалённого хранилища, которую имеет смысл показать пользователю.
    ///
    /// Отделена от сетевых исключений намеренно: HttpRequestException означает
    /// «не дозвонились» и лечится повтором позже, а эта — «дозвонились, и нам
    /// отказали», и повтор ничего не изменит, пока пользователь не поправит
    /// настройки.
    /// </summary>
    public class RemoteStorageException : Exception
    {
        /// <summary>Код ответа HTTP, если ошибка пришла от сервера.</summary>
        public int? StatusCode { get; }

        public RemoteStorageException(string message, int? statusCode = null)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public RemoteStorageException(string message, Exception inner, int? statusCode = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>Сервер отверг учётные данные.</summary>
    public sealed class RemoteAuthenticationException : RemoteStorageException
    {
        public RemoteAuthenticationException(string message)
            : base(message, 401)
        {
        }
    }
}
