using Avalonia.Controls;
using System;

namespace Writersword.Core.Interfaces.Modules
{
    /// <summary>
    /// Опциональный интерфейс для модулей у которых есть настройки.
    /// Реализуй только если модулю нужна своя вкладка в Settings.
    /// </summary>
    public interface IConfigurableModule
    {
        /// <summary>Название раздела в Settings</summary>
        string SettingsTitle { get; }

        /// <summary>Тип объекта глобальных настроек для десериализации</summary>
        Type SettingsType { get; }

        /// <summary>Получить текущие глобальные настройки</summary>
        object GetSettings();

        /// <summary>Применить глобальные настройки</summary>
        void ApplySettings(object settings);

        /// <summary>Создать View для отображения глобальных настроек</summary>
        Control CreateSettingsView();

        /// <summary>Получить текущие локальные настройки проекта</summary>
        object GetLocalSettings();

        /// <summary>Применить локальные настройки проекта</summary>
        void ApplyLocalSettings(object settings);

        /// <summary>Создать View для отображения локальных настроек проекта</summary>
        Control CreateLocalSettingsView();
    }
}