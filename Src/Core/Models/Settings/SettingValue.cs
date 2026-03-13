using ReactiveUI;
using System.Collections.Generic;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Settings;

namespace Writersword.Core.Models.Settings
{
    /// <summary>
    /// Обёртка для одного значения настройки модуля.
    /// Хранит текущее значение, хардкод дефолт и глобальное значение.
    /// Автоматически вычисляет Origin при изменении Value.
    /// Используется в ViewModel настроек модуля вместо простого типа T.
    /// </summary>
    public class SettingValue<T> : ReactiveObject, ISettingValue
    {
        private static readonly EqualityComparer<T> _comparer = EqualityComparer<T>.Default;

        private T _value;
        private T _globalValue;

        /// <summary>
        /// Хардкод дефолт — задаётся один раз при создании, изменить невозможно.
        /// </summary>
        public T HardcodedDefault { get; }

        /// <summary>
        /// Глобальное значение — сохранённое пользователем для всех проектов.
        /// При изменении пересчитывается Origin.
        /// </summary>
        public T GlobalValue
        {
            get => _globalValue;
            set
            {
                this.RaiseAndSetIfChanged(ref _globalValue, value);
                this.RaisePropertyChanged(nameof(Origin));
                this.RaisePropertyChanged(nameof(IsOverriddenFromHardcoded));
                this.RaisePropertyChanged(nameof(IsOverriddenFromGlobal));
            }
        }

        /// <summary>
        /// Текущее значение поля.
        /// При изменении автоматически пересчитывается Origin.
        /// </summary>
        public T Value
        {
            get => _value;
            set
            {
                // Защита от null для reference types
                if (value is null) return;
                this.RaiseAndSetIfChanged(ref _value, value);
                this.RaisePropertyChanged(nameof(Origin));
                this.RaisePropertyChanged(nameof(IsOverriddenFromHardcoded));
                this.RaisePropertyChanged(nameof(IsOverriddenFromGlobal));
            }
        }

        /// <summary>
        /// Источник текущего значения.
        /// Hardcoded — совпадает с хардкодом.
        /// Global — совпадает с глобальным но отличается от хардкода.
        /// LocalOverride — отличается и от глобального и от хардкода.
        /// </summary>
        public SettingsOrigin Origin
        {
            get
            {
                if (_comparer.Equals(_value, HardcodedDefault))
                    return SettingsOrigin.Hardcoded;

                if (_comparer.Equals(_value, _globalValue))
                    return SettingsOrigin.Global;

                return SettingsOrigin.LocalOverride;
            }
        }

        /// <summary>
        /// True если текущее значение отличается от хардкод дефолта.
        /// Используется для отображения кнопки сброса до хардкода.
        /// </summary>
        public bool IsOverriddenFromHardcoded => !_comparer.Equals(_value, HardcodedDefault);

        /// <summary>
        /// True если текущее значение отличается от глобального.
        /// Используется для отображения кнопки сброса до глобального в локальной вкладке.
        /// </summary>
        public bool IsOverriddenFromGlobal => !_comparer.Equals(_value, _globalValue);

        /// <summary>
        /// Создать SettingValue с хардкод дефолтом.
        /// Глобальное значение по умолчанию равно хардкоду.
        /// </summary>
        /// <param name="hardcoded">Хардкод дефолт — неизменяемый.</param>
        public SettingValue(T hardcoded)
        {
            HardcodedDefault = hardcoded;
            _globalValue = hardcoded;
            _value = hardcoded;
        }

        /// <summary>
        /// Создать SettingValue с хардкод дефолтом и глобальным значением.
        /// </summary>
        /// <param name="hardcoded">Хардкод дефолт — неизменяемый.</param>
        /// <param name="global">Глобальное значение из ISettingsService.</param>
        public SettingValue(T hardcoded, T global)
        {
            HardcodedDefault = hardcoded;
            _globalValue = global;
            _value = global;
        }

        /// <summary>
        /// Создать SettingValue с явным текущим значением.
        /// Используется при загрузке локальных настроек проекта.
        /// </summary>
        /// <param name="hardcoded">Хардкод дефолт — неизменяемый.</param>
        /// <param name="global">Глобальное значение из ISettingsService.</param>
        /// <param name="current">Текущее локальное значение проекта.</param>
        public SettingValue(T hardcoded, T global, T current)
        {
            HardcodedDefault = hardcoded;
            _globalValue = global;
            _value = current;
        }

        /// <summary>Сбросить Value до хардкод дефолта.</summary>
        public void ResetToHardcoded()
        {
            Value = HardcodedDefault;
        }

        /// <summary>Сбросить Value до глобального значения.</summary>
        public void ResetToGlobal()
        {
            Value = GlobalValue;
        }

        /// <summary>
        /// Установить GlobalValue равным текущему Value.
        /// Вызывается когда пользователь сохраняет текущее значение как глобальное.
        /// </summary>
        public void PromoteToGlobal()
        {
            GlobalValue = Value;
        }
    }
}