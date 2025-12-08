using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Writersword.Services.Interfaces
{
    /// <summary>
    /// Сервис для работы с настройками приложения
    /// </summary>
    public interface ISettingsService
    {
        /// <summary>Загрузить настройки из файла</summary>
        void Load();

        /// <summary>Сохранить настройки в файл</summary>
        void Save();

        /// <summary>Тема приложения (Dark, Light, Sepia)</summary>
        string Theme { get; set; }

        /// <summary>Язык интерфейса (ru, uk, en)</summary>
        string Language { get; set; }

        /// <summary>Последний открытый проект</summary>
        string? LastOpenedProject { get; set; }
    }
}
