using Writersword.Core.Enums;

namespace Writersword.Core.Interfaces.Settings
{
    /// <summary>
    /// Не-дженерик интерфейс для SettingValue.
    /// Используется в SettingRow для биндинга в XAML без знания конкретного типа T.
    /// </summary>
    public interface ISettingValue
    {
        /// <summary>Источник текущего значения.</summary>
        SettingsOrigin Origin { get; }

        /// <summary>True если значение отличается от хардкод дефолта.</summary>
        bool IsOverriddenFromHardcoded { get; }

        /// <summary>True если значение отличается от глобального.</summary>
        bool IsOverriddenFromGlobal { get; }

        /// <summary>Сбросить до хардкод дефолта.</summary>
        void ResetToHardcoded();

        /// <summary>Сбросить до глобального значения.</summary>
        void ResetToGlobal();
    }
}