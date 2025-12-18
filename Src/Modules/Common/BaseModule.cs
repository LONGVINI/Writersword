using System;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Modules;
using Writersword.Core.Models.Modules;

namespace Writersword.Modules.Common
{
    /// <summary>
    /// Базовый класс для всех UI модулей
    /// Все модули из Src/Modules/ наследуются от него
    /// </summary>
    public abstract class BaseModule : IModule
    {
        public string InstanceId { get; } = Guid.NewGuid().ToString();
        public abstract ModuleType ModuleType { get; }
        public virtual string Title { get; set; } = "Module";
        public virtual bool IsMovable { get; } = true;
        public virtual bool IsCloseable { get; } = true;
        public virtual bool CanDetach { get; } = true;
        public abstract object? ViewModel { get; }

        public event Action<IModule>? RequestClose;
        public event Action<IModule>? RequestDetach;

        public virtual void Initialize()
        {
            // Переопределите если нужна инициализация
        }

        public virtual ModuleState SaveState()
        {
            return new ModuleState
            {
                ScrollPosition = 0,
                CustomData = null
            };
        }

        public virtual void RestoreState(ModuleState state)
        {
            // Переопределите для восстановления состояния
        }

        public virtual void Dispose()
        {
            // Переопределите для освобождения ресурсов
        }

        protected void OnRequestClose()
        {
            RequestClose?.Invoke(this);
        }

        protected void OnRequestDetach()
        {
            RequestDetach?.Invoke(this);
        }
    }
}