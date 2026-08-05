using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Writersword.Modules.Characters.Models
{
    /// <summary>
    /// Базовая модель с уведомлением об изменении свойств. Нужна тем моделям,
    /// которые редактируются в одном месте интерфейса, а отображаются в другом:
    /// список слева и редактор справа, звёздочка ключевого события и её цвет.
    /// Событие PropertyChanged не является свойством, поэтому на сериализацию
    /// моделей в JSON переход на этот базовый класс не влияет.
    /// </summary>
    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class CharacterStatus
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Color { get; set; } = "#607D8B";
        public string Description { get; set; } = string.Empty;
    }

    public class CharacterContext : ObservableModel
    {
        private string _id = Guid.NewGuid().ToString();
        public string Id { get => _id; set => Set(ref _id, value); }

        private string _name = string.Empty;
        public string Name { get => _name; set => Set(ref _name, value); }

        private string _description = string.Empty;
        public string Description { get => _description; set => Set(ref _description, value); }

        private string _accentColor = "#607D8B";
        public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }

        private string _notes = string.Empty;
        public string Notes { get => _notes; set => Set(ref _notes, value); }
    }

    public class CharacterNote : ObservableModel
    {
        private string _id = Guid.NewGuid().ToString();
        public string Id { get => _id; set => Set(ref _id, value); }

        private string _title = "Заметка";
        public string Title { get => _title; set => Set(ref _title, value); }

        private string _content = string.Empty;
        public string Content { get => _content; set => Set(ref _content, value); }

        private string _accentColor = "#607D8B";
        public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }

        private DateTime _createdAt = DateTime.UtcNow;
        public DateTime CreatedAt { get => _createdAt; set => Set(ref _createdAt, value); }
    }

    public class CharacterPersonalEvent : ObservableModel
    {
        private string _id = Guid.NewGuid().ToString();
        public string Id { get => _id; set => Set(ref _id, value); }

        private string _title = string.Empty;
        public string Title { get => _title; set => Set(ref _title, value); }

        private string _timestamp = string.Empty;
        public string Timestamp { get => _timestamp; set => Set(ref _timestamp, value); }

        private string _description = string.Empty;
        public string Description { get => _description; set => Set(ref _description, value); }

        private bool _isKeyEvent;
        public bool IsKeyEvent { get => _isKeyEvent; set => Set(ref _isKeyEvent, value); }

        private string _accentColor = "#607D8B";
        public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }
    }

    public class CharacterItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class CharacterAnketa
    {
        // Встроенный набор полей коллективного персонажа. Идентификатор нужен
        // и сервису анкет, и признаку группы на карточке, и кнопке создания
        // группы в списке — литерал в трёх местах расходится молча.
        public const string CollectiveId = "builtin_collective";

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; } = false;
        public List<string> ProjectTypeTags { get; set; } = new();
        public List<CharacterAnketaField> Fields { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
