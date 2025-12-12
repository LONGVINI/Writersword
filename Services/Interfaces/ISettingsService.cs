using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Writersword.Core.Models.Project;

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

        /// <summary>Папка для проектов по умолчанию</summary>
        string DefaultProjectsFolder { get; set; }

        /// <summary>Последний использованный путь (для диалога Save/Open)</summary>
        string? LastUsedPath { get; set; }

        /// <summary> Получает или задает список недавно использованных проектов </summary>
        List<RecentProject> RecentProjects { get; }

        /// <summary> Добавляет проект в список недавно использованных </summary>
        void AddRecentProject(string filePath);

        List<string> OpenProjectPaths { get; set; }
        void SaveOpenProjects(List<string> projectPaths);
    }
}
