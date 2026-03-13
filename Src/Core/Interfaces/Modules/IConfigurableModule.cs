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
        /// <summary>Название раздела в Settings.</summary>
        string SettingsTitle { get; }

        /// <summary>Тип объекта глобальных настроек для десериализации.</summary>
        Type SettingsType { get; }

        /// <summary>Получить текущие глобальные настройки.</summary>
        object GetSettings();

        /// <summary>Применить глобальные настройки.</summary>
        void ApplySettings(object settings);

        /// <summary>Создать View для отображения глобальных настроек.</summary>
        Control CreateSettingsView();

        /// <summary>Получить текущие локальные настройки проекта.</summary>
        object GetLocalSettings();

        /// <summary>Применить локальные настройки проекта.</summary>
        void ApplyLocalSettings(object settings);

        /// <summary>Создать View для отображения локальных настроек проекта.</summary>
        Control CreateLocalSettingsView();

        /// <summary>
        /// Получить хардкод дефолты модуля.
        /// Возвращает объект того же типа что и GetSettings().
        /// Значения неизменяемы — задаются внутри модуля.
        /// </summary>
        object GetDefaultSettings();

        /// <summary>
        /// Сбросить UI глобальных настроек к хардкод дефолтам.
        /// Вызывается из toolbar кнопкой "Сбросить всё до дефолта" в глобальной вкладке.
        /// </summary>
        void ResetSettingsToDefaults();

        /// <summary>
        /// Сбросить UI локальных настроек к глобальным значениям.
        /// Вызывается из toolbar кнопкой "Сбросить всё до глобальных" в локальной вкладке.
        /// Берёт актуальные значения из текущего UI глобальной VM, а не из сервиса.
        /// </summary>
        void ResetLocalSettingsToGlobal();

        /// <summary>
        /// Сбросить UI локальных настроек к хардкод дефолтам.
        /// Вызывается из toolbar кнопкой "Сбросить всё до дефолта" в локальной вкладке.
        /// </summary>
        void ResetLocalSettingsToDefaults();

        /// <summary>
        /// Применить текущие глобальные UI-значения к локальной VM.
        /// Вызывается из toolbar кнопкой "Применить к этому проекту" в глобальной вкладке.
        /// Обновляет GlobalValue и Value в локальной VM без сохранения в файл.
        /// </summary>
        void ApplyGlobalToLocal();

        /// <summary>
        /// Сохранить текущие локальные UI-значения как глобальные.
        /// Вызывается из toolbar кнопкой "Сохранить как глобальные" в локальной вкладке.
        /// Сохраняет в ISettingsService и обновляет GlobalValue в локальной VM.
        /// </summary>
        void PromoteLocalToGlobal();
    }
}