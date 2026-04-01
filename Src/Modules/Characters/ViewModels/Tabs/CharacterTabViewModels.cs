using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.ViewModels.Tabs
{
    // ── Вкладка Основное ─────────────────────────────────────────────────

    public class CharacterBasicsTabViewModel : ReactiveObject
    {
        private readonly ICharacterService _characterService;
        private readonly string _characterId;

        public ObservableCollection<string> Aliases { get; } = new();
        public ObservableCollection<CharacterStatus> ActiveStatuses { get; } = new();

        public ReactiveCommand<Unit, Unit> UploadAvatarCommand { get; }
        public ReactiveCommand<Unit, Unit> DeleteAvatarCommand { get; }
        public ReactiveCommand<Unit, Unit> AddStatusCommand { get; }
        public ReactiveCommand<string, Unit> RemoveStatusCommand { get; }
        public ReactiveCommand<string, Unit> AddAliasCommand { get; }
        public ReactiveCommand<string, Unit> RemoveAliasCommand { get; }
        public ReactiveCommand<string, Unit> RemoveTagCommand { get; }

        private string _name = string.Empty;
        public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

        private string _shortDescription = string.Empty;
        public string ShortDescription { get => _shortDescription; set => this.RaiseAndSetIfChanged(ref _shortDescription, value); }

        private string _color = "#607D8B";
        public string Color { get => _color; set => this.RaiseAndSetIfChanged(ref _color, value); }

        private string _fallbackIcon = "?";
        public string FallbackIcon { get => _fallbackIcon; set => this.RaiseAndSetIfChanged(ref _fallbackIcon, value); }

        private string? _avatarPath;
        public string? AvatarPath { get => _avatarPath; set => this.RaiseAndSetIfChanged(ref _avatarPath, value); }

        private CharacterImportanceLevel _importanceLevel = CharacterImportanceLevel.Secondary;
        public CharacterImportanceLevel ImportanceLevel { get => _importanceLevel; set => this.RaiseAndSetIfChanged(ref _importanceLevel, value); }

        private string _customImportanceLabel = string.Empty;
        public string CustomImportanceLabel { get => _customImportanceLabel; set => this.RaiseAndSetIfChanged(ref _customImportanceLabel, value); }

        private string _narrativeStartPoint = string.Empty;
        public string NarrativeStartPoint { get => _narrativeStartPoint; set => this.RaiseAndSetIfChanged(ref _narrativeStartPoint, value); }

        private string _narrativeEndPoint = string.Empty;
        public string NarrativeEndPoint { get => _narrativeEndPoint; set => this.RaiseAndSetIfChanged(ref _narrativeEndPoint, value); }

        private bool _isCollective;
        public bool IsCollective { get => _isCollective; set => this.RaiseAndSetIfChanged(ref _isCollective, value); }

        private string _populationNote = string.Empty;
        public string PopulationNote { get => _populationNote; set => this.RaiseAndSetIfChanged(ref _populationNote, value); }

        public ObservableCollection<string> Tags { get; } = new();

        public CharacterBasicsTabViewModel(ICharacterService characterService, Character character)
        {
            _characterService = characterService;
            _characterId = character.Id;

            LoadFrom(character);

            UploadAvatarCommand = ReactiveCommand.Create(() => { });
            DeleteAvatarCommand = ReactiveCommand.Create(() => { _characterService.DeleteAvatar(_characterId); AvatarPath = null; });
            AddStatusCommand = ReactiveCommand.Create(() => { });
            RemoveStatusCommand = ReactiveCommand.Create<string>(id => { ActiveStatuses.Remove(ActiveStatuses.FirstOrDefault(s => s.Id == id)!); });
            AddAliasCommand = ReactiveCommand.Create<string>(a => { if (!string.IsNullOrWhiteSpace(a) && !Aliases.Contains(a)) Aliases.Add(a); });
            RemoveAliasCommand = ReactiveCommand.Create<string>(a => Aliases.Remove(a));
            RemoveTagCommand = ReactiveCommand.Create<string>(t => Tags.Remove(t));
        }

        private void LoadFrom(Character c)
        {
            _name = c.Name;
            _shortDescription = c.ShortDescription;
            _color = c.Color;
            _fallbackIcon = c.FallbackIcon;
            _avatarPath = c.AvatarPath;
            _importanceLevel = c.ImportanceLevel;
            _customImportanceLabel = c.CustomImportanceLabel;
            _narrativeStartPoint = c.NarrativeStartPoint;
            _narrativeEndPoint = c.NarrativeEndPoint;
            _isCollective = c.IsCollective;
            _populationNote = c.PopulationNote;

            Aliases.Clear(); foreach (var a in c.Aliases) Aliases.Add(a);
            Tags.Clear(); foreach (var t in c.Tags) Tags.Add(t);
            ActiveStatuses.Clear(); foreach (var s in c.ActiveStatuses) ActiveStatuses.Add(s);
        }

        public void ApplyTo(Character character)
        {
            character.Name = Name;
            character.ShortDescription = ShortDescription;
            character.Color = Color;
            character.FallbackIcon = FallbackIcon;
            character.ImportanceLevel = ImportanceLevel;
            character.CustomImportanceLabel = CustomImportanceLabel;
            character.NarrativeStartPoint = NarrativeStartPoint;
            character.NarrativeEndPoint = NarrativeEndPoint;
            character.IsCollective = IsCollective;
            character.PopulationNote = PopulationNote;
            character.Aliases = Aliases.ToList();
            character.Tags = Tags.ToList();
            character.ActiveStatuses = ActiveStatuses.ToList();
        }
    }

    // ── Вкладка Параметры ────────────────────────────────────────────────

    public class CharacterParametersTabViewModel : ReactiveObject
    {
        public ObservableCollection<CharacterParameter> Parameters { get; } = new();
        public ObservableCollection<CharacterAnketa> AvailableAnketas { get; } = new();

        public ReactiveCommand<Unit, Unit> AddNumericParameterCommand { get; }
        public ReactiveCommand<Unit, Unit> AddTextParameterCommand { get; }
        public ReactiveCommand<Unit, Unit> AddStateListParameterCommand { get; }
        public ReactiveCommand<Unit, Unit> AddBooleanParameterCommand { get; }
        public ReactiveCommand<string, Unit> RemoveParameterCommand { get; }
        public ReactiveCommand<string, Unit> MoveParameterUpCommand { get; }
        public ReactiveCommand<Unit, Unit> RandomizeAllCommand { get; }
        public ReactiveCommand<string, Unit> ApplyAnketaCommand { get; }

        private readonly ICharacterService _characterService;
        private readonly ICharacterAnketaService _anketaService;
        private readonly string _characterId;

        public CharacterParametersTabViewModel(ICharacterService cs, ICharacterAnketaService as_, Character character)
        {
            _characterService = cs;
            _anketaService = as_;
            _characterId = character.Id;

            foreach (var p in character.Parameters) Parameters.Add(p);
            foreach (var a in as_.GetAll()) AvailableAnketas.Add(a);

            AddNumericParameterCommand = ReactiveCommand.Create(() => AddParameter(CharacterParameterType.Numeric));
            AddTextParameterCommand = ReactiveCommand.Create(() => AddParameter(CharacterParameterType.Text));
            AddStateListParameterCommand = ReactiveCommand.Create(() => AddParameter(CharacterParameterType.StateList));
            AddBooleanParameterCommand = ReactiveCommand.Create(() => AddParameter(CharacterParameterType.Boolean));
            RemoveParameterCommand = ReactiveCommand.Create<string>(id => { var p = Parameters.FirstOrDefault(x => x.Id == id); if (p != null) Parameters.Remove(p); });
            MoveParameterUpCommand = ReactiveCommand.Create<string>(id => { var idx = Parameters.IndexOf(Parameters.FirstOrDefault(x => x.Id == id)!); if (idx > 0) Parameters.Move(idx, idx - 1); });
            RandomizeAllCommand = ReactiveCommand.Create(RandomizeAll);
            ApplyAnketaCommand = ReactiveCommand.Create<string>(ApplyAnketa);
        }

        private void AddParameter(CharacterParameterType type)
        {
            Parameters.Add(new CharacterParameter
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Новый параметр",
                Type = type,
                Order = Parameters.Count
            });
        }

        private void RandomizeAll()
        {
            var list = Parameters.ToList();
            _anketaService.RandomizeParameters(list);
            for (int i = 0; i < list.Count; i++) Parameters[i] = list[i];
        }

        private void ApplyAnketa(string anketaId)
        {
            var anketa = _anketaService.GetById(anketaId);
            if (anketa == null) return;
            _characterService.ApplyAnketa(_characterId, anketa, false);
            var updated = _characterService.GetById(_characterId);
            if (updated == null) return;
            Parameters.Clear();
            foreach (var p in updated.Parameters) Parameters.Add(p);
        }

        public List<CharacterParameter> GetParameters() => Parameters.ToList();
    }

    // ── Вкладка Связи ────────────────────────────────────────────────────

    public class CharacterRelationshipItemViewModel : ReactiveObject
    {
        public string RelationshipId { get; }
        public string TargetCharacterId { get; }
        public string TargetName { get; set; } = string.Empty;
        public string TargetColor { get; set; } = "#607D8B";
        public string TargetIcon { get; set; } = "?";

        private string _relationshipType = string.Empty;
        public string RelationshipType { get => _relationshipType; set => this.RaiseAndSetIfChanged(ref _relationshipType, value); }

        private CharacterRelationshipContext _context;
        public CharacterRelationshipContext Context { get => _context; set => this.RaiseAndSetIfChanged(ref _context, value); }

        private CharacterRelationshipEmotion _emotion;
        public CharacterRelationshipEmotion Emotion { get => _emotion; set => this.RaiseAndSetIfChanged(ref _emotion, value); }

        private double _strength = 0.5;
        public double Strength { get => _strength; set => this.RaiseAndSetIfChanged(ref _strength, value); }

        private bool _isBidirectional = true;
        public bool IsBidirectional { get => _isBidirectional; set => this.RaiseAndSetIfChanged(ref _isBidirectional, value); }

        private string _note = string.Empty;
        public string Note { get => _note; set => this.RaiseAndSetIfChanged(ref _note, value); }

        public ObservableCollection<string> SourceCallsTargetAs { get; } = new();

        public CharacterRelationshipItemViewModel(CharacterRelationship rel, Character? target)
        {
            RelationshipId = rel.Id;
            TargetCharacterId = rel.TargetCharacterId;
            _relationshipType = rel.RelationshipType;
            _context = rel.Context;
            _emotion = rel.Emotion;
            _strength = rel.Strength;
            _isBidirectional = rel.IsBidirectional;
            _note = rel.Note;

            if (target != null)
            {
                TargetName = target.Name;
                TargetColor = target.Color;
                TargetIcon = target.FallbackIcon;
            }

            foreach (var a in rel.SourceCallsTargetAs) SourceCallsTargetAs.Add(a);
        }

        public CharacterRelationship ToModel() => new()
        {
            Id = RelationshipId,
            TargetCharacterId = TargetCharacterId,
            RelationshipType = RelationshipType,
            Context = Context,
            Emotion = Emotion,
            Strength = Strength,
            IsBidirectional = IsBidirectional,
            Note = Note,
            SourceCallsTargetAs = SourceCallsTargetAs.ToList()
        };
    }

    public class CharacterRelationshipsTabViewModel : ReactiveObject
    {
        private readonly IRelationshipService _relService;
        private readonly ICharacterService _charService;
        private readonly string _characterId;

        public ObservableCollection<CharacterRelationshipItemViewModel> Relationships { get; } = new();
        public ObservableCollection<Character> AvailableCharacters { get; } = new();

        public ReactiveCommand<Unit, Unit> AddRelationshipCommand { get; }
        public ReactiveCommand<string, Unit> RemoveRelationshipCommand { get; }

        public CharacterRelationshipsTabViewModel(IRelationshipService rs, ICharacterService cs, string characterId)
        {
            _relService = rs;
            _charService = cs;
            _characterId = characterId;

            Refresh();

            AddRelationshipCommand = ReactiveCommand.Create(AddRelationship);
            RemoveRelationshipCommand = ReactiveCommand.Create<string>(id => { _relService.Delete(id); Refresh(); });
        }

        public void Refresh()
        {
            Relationships.Clear();
            foreach (var rel in _relService.GetOutgoing(_characterId))
            {
                var target = _charService.GetById(rel.TargetCharacterId);
                Relationships.Add(new CharacterRelationshipItemViewModel(rel, target));
            }

            AvailableCharacters.Clear();
            foreach (var c in _charService.GetAll())
                if (c.Id != _characterId) AvailableCharacters.Add(c);
        }

        private void AddRelationship()
        {
            var first = AvailableCharacters.FirstOrDefault();
            if (first == null) return;
            var rel = _relService.Create(_characterId, first.Id);
            Relationships.Add(new CharacterRelationshipItemViewModel(rel, first));
        }
    }

    // ── Вкладка Контексты ────────────────────────────────────────────────

    public class CharacterContextsTabViewModel : ReactiveObject
    {
        public ObservableCollection<CharacterContext> Contexts { get; } = new();

        private CharacterContext? _selectedContext;
        public CharacterContext? SelectedContext { get => _selectedContext; set => this.RaiseAndSetIfChanged(ref _selectedContext, value); }

        public ReactiveCommand<Unit, Unit> AddContextCommand { get; }
        public ReactiveCommand<string, Unit> RemoveContextCommand { get; }
        public ReactiveCommand<CharacterContext, Unit> SelectContextCommand { get; }

        public CharacterContextsTabViewModel(Character character)
        {
            foreach (var c in character.Contexts) Contexts.Add(c);
            SelectedContext = Contexts.FirstOrDefault();

            AddContextCommand = ReactiveCommand.Create(() =>
            {
                var ctx = new CharacterContext { Id = Guid.NewGuid().ToString(), Name = "Новый контекст" };
                Contexts.Add(ctx);
                SelectedContext = ctx;
            });
            RemoveContextCommand = ReactiveCommand.Create<string>(id =>
            {
                var ctx = Contexts.FirstOrDefault(c => c.Id == id);
                if (ctx != null) { Contexts.Remove(ctx); SelectedContext = Contexts.FirstOrDefault(); }
            });
            SelectContextCommand = ReactiveCommand.Create<CharacterContext>(c => SelectedContext = c);
        }

        public List<CharacterContext> GetContexts() => Contexts.ToList();
    }

    // ── Вкладка Заметки ──────────────────────────────────────────────────

    public class CharacterNotesTabViewModel : ReactiveObject
    {
        public ObservableCollection<CharacterNote> Notes { get; } = new();

        private CharacterNote? _selectedNote;
        public CharacterNote? SelectedNote { get => _selectedNote; set => this.RaiseAndSetIfChanged(ref _selectedNote, value); }

        public ReactiveCommand<Unit, Unit> AddNoteCommand { get; }
        public ReactiveCommand<string, Unit> RemoveNoteCommand { get; }
        public ReactiveCommand<CharacterNote, Unit> SelectNoteCommand { get; }

        public CharacterNotesTabViewModel(Character character)
        {
            foreach (var n in character.Notes) Notes.Add(n);
            SelectedNote = Notes.FirstOrDefault();

            AddNoteCommand = ReactiveCommand.Create(() =>
            {
                var note = new CharacterNote { Id = Guid.NewGuid().ToString(), Title = "Новая заметка" };
                Notes.Add(note);
                SelectedNote = note;
            });
            RemoveNoteCommand = ReactiveCommand.Create<string>(id =>
            {
                var n = Notes.FirstOrDefault(x => x.Id == id);
                if (n != null) { Notes.Remove(n); SelectedNote = Notes.FirstOrDefault(); }
            });
            SelectNoteCommand = ReactiveCommand.Create<CharacterNote>(n => SelectedNote = n);
        }

        public List<CharacterNote> GetNotes() => Notes.ToList();
    }

    // ── Вкладка Таймлайн ─────────────────────────────────────────────────

    public class CharacterPersonalTimelineTabViewModel : ReactiveObject
    {
        public ObservableCollection<CharacterPersonalEvent> Events { get; } = new();

        public ReactiveCommand<Unit, Unit> AddEventCommand { get; }
        public ReactiveCommand<string, Unit> RemoveEventCommand { get; }
        public ReactiveCommand<string, Unit> ToggleKeyEventCommand { get; }

        public CharacterPersonalTimelineTabViewModel(Character character)
        {
            foreach (var e in character.PersonalTimeline) Events.Add(e);

            AddEventCommand = ReactiveCommand.Create(() =>
                Events.Add(new CharacterPersonalEvent { Id = Guid.NewGuid().ToString(), Title = "Новое событие" }));
            RemoveEventCommand = ReactiveCommand.Create<string>(id =>
            {
                var e = Events.FirstOrDefault(x => x.Id == id);
                if (e != null) Events.Remove(e);
            });
            ToggleKeyEventCommand = ReactiveCommand.Create<string>(id =>
            {
                var e = Events.FirstOrDefault(x => x.Id == id);
                if (e != null) e.IsKeyEvent = !e.IsKeyEvent;
            });
        }

        public List<CharacterPersonalEvent> GetEvents() => Events.ToList();
    }

    // ── Вкладка История ──────────────────────────────────────────────────

    public class CharacterHistoryTabViewModel : ReactiveObject
    {
        public ObservableCollection<string> LinkedProjectEventIds { get; } = new();
        public bool HasNoHistory => !LinkedProjectEventIds.Any();

        public CharacterHistoryTabViewModel(Character character)
        {
            foreach (var id in character.LinkedProjectEventIds) LinkedProjectEventIds.Add(id);
        }
    }
}