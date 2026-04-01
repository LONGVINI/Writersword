using ReactiveUI;

namespace Writersword.Modules.Characters.ViewModels
{
    public class CharacterListItemViewModel : ReactiveObject
    {
        private bool _isBeingNamed;
        private string _inlineName = string.Empty;
        private bool _isSelected;

        public string Id { get; }
        public string Name { get; }
        public string ShortDescription { get; }
        public string Color { get; }
        public string FallbackIcon { get; }
        public bool IsCollective { get; }
        public int RelationshipsCount { get; }

        /// <summary>Персонаж только что создан и ожидает ввода имени</summary>
        public bool IsBeingNamed
        {
            get => _isBeingNamed;
            set => this.RaiseAndSetIfChanged(ref _isBeingNamed, value);
        }

        /// <summary>Текущий вводимый текст при inline-создании</summary>
        public string InlineName
        {
            get => _inlineName;
            set => this.RaiseAndSetIfChanged(ref _inlineName, value);
        }

        /// <summary>Выделен ли персонаж в списке</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        public CharacterListItemViewModel(
            Models.Character character,
            int relationshipsCount = 0,
            bool isBeingNamed = false)
        {
            Id = character.Id;
            Name = character.Name;
            ShortDescription = character.ShortDescription;
            Color = character.Color;
            FallbackIcon = character.FallbackIcon;
            IsCollective = character.IsCollective;
            RelationshipsCount = relationshipsCount;
            _isBeingNamed = isBeingNamed;
            _inlineName = isBeingNamed ? string.Empty : character.Name;
        }
    }
}