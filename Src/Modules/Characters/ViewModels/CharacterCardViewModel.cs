using ReactiveUI;
using Serilog;
using System.Reactive;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.ViewModels
{
    public class CharacterCardViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterCardViewModel>();

        public const int TabCount = 7;

        public string CharacterId { get; }

        public CharacterBasicsTabViewModel BasicsTab { get; }
        public CharacterParametersTabViewModel ParametersTab { get; }
        public CharacterRelationshipsTabViewModel RelationshipsTab { get; }
        public CharacterContextsTabViewModel ContextsTab { get; }
        public CharacterNotesTabViewModel NotesTab { get; }
        public CharacterPersonalTimelineTabViewModel PersonalTimelineTab { get; }
        public CharacterHistoryTabViewModel HistoryTab { get; }

        private int _selectedTabIndex = 0;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
        }

        public string DisplayName => BasicsTab.Name;
        public string Color => BasicsTab.Color;
        public bool IsCollective => BasicsTab.IsCollective;

        public ReactiveCommand<Unit, Unit> SaveCommand { get; }

        private readonly ICharacterService _characterService;

        public CharacterCardViewModel(
            ICharacterService characterService,
            IRelationshipService relationshipService,
            ICharacterAnketaService anketaService,
            Character character,
            ICharacterAvatarService? avatarService = null)
        {
            _characterService = characterService;
            CharacterId = character.Id;

            BasicsTab = new CharacterBasicsTabViewModel(characterService, character, avatarService);
            ParametersTab = new CharacterParametersTabViewModel(characterService, anketaService, character);
            RelationshipsTab = new CharacterRelationshipsTabViewModel(relationshipService, characterService, character.Id);
            ContextsTab = new CharacterContextsTabViewModel(character);
            NotesTab = new CharacterNotesTabViewModel(character);
            PersonalTimelineTab = new CharacterPersonalTimelineTabViewModel(character);
            HistoryTab = new CharacterHistoryTabViewModel(character);

            SaveCommand = ReactiveCommand.Create(Save);
        }

        private void Save()
        {
            var character = _characterService.GetById(CharacterId);
            if (character == null) return;
            BasicsTab.ApplyTo(character);
            _characterService.Update(character);
            _logger.Debug("Character {Id} saved", CharacterId);
        }
    }
}