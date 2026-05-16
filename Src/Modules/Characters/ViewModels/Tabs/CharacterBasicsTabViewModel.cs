using Avalonia.Media.Imaging;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.ViewModels.Tabs
{
    public class CharacterBasicsTabViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterBasicsTabViewModel>();

        private readonly ICharacterService _characterService;
        private readonly string _characterId;

        public ICharacterAvatarService? AvatarService { get; }
        public string CharacterId => _characterId;

        public ObservableCollection<string> Aliases { get; } = new();
        public ObservableCollection<CharacterStatus> ActiveStatuses { get; } = new();
        public ObservableCollection<string> Tags { get; } = new();

        public ReactiveCommand<Unit, Unit> OpenPickerCommand { get; }
        public ReactiveCommand<Unit, Unit> DeleteAvatarCommand { get; }
        public ReactiveCommand<Unit, Unit> AddStatusCommand { get; }
        public ReactiveCommand<string, Unit> RemoveStatusCommand { get; }
        public ReactiveCommand<string, Unit> AddAliasCommand { get; }
        public ReactiveCommand<string, Unit> RemoveAliasCommand { get; }
        public ReactiveCommand<string, Unit> RemoveTagCommand { get; }

        public Func<Task<string?>>? RequestPickerOpen { get; set; }

        private string _name = string.Empty;
        public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

        private string _shortDescription = string.Empty;
        public string ShortDescription { get => _shortDescription; set => this.RaiseAndSetIfChanged(ref _shortDescription, value); }

        private string _color = "#607D8B";
        public string Color { get => _color; set => this.RaiseAndSetIfChanged(ref _color, value); }

        private string _fallbackIcon = "?";
        public string FallbackIcon { get => _fallbackIcon; set => this.RaiseAndSetIfChanged(ref _fallbackIcon, value); }

        private string? _avatarPath;
        public string? AvatarPath
        {
            get => _avatarPath;
            set { this.RaiseAndSetIfChanged(ref _avatarPath, value); ReloadBitmap(); }
        }

        private Bitmap? _avatarBitmap;
        public Bitmap? AvatarBitmap
        {
            get => _avatarBitmap;
            private set { _avatarBitmap?.Dispose(); this.RaiseAndSetIfChanged(ref _avatarBitmap, value); }
        }

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

        public CharacterBasicsTabViewModel(
            ICharacterService characterService,
            Character character,
            ICharacterAvatarService? avatarService = null)
        {
            _characterService = characterService;
            _characterId = character.Id;
            AvatarService = avatarService;

            LoadFrom(character);

            OpenPickerCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (RequestPickerOpen == null) return;
                var selectedRef = await RequestPickerOpen();
                if (selectedRef != null) ApplyAvatarRef(selectedRef);
            });

            DeleteAvatarCommand = ReactiveCommand.Create(() =>
            {
                AvatarService?.DeleteAvatar(_avatarPath);
                AvatarPath = null;
                var c = _characterService.GetById(_characterId);
                if (c != null) { c.AvatarPath = null; _characterService.Update(c); }
            });

            AddStatusCommand = ReactiveCommand.Create(() => { });
            RemoveStatusCommand = ReactiveCommand.Create<string>(id =>
                ActiveStatuses.Remove(ActiveStatuses.FirstOrDefault(s => s.Id == id)!));
            AddAliasCommand = ReactiveCommand.Create<string>(a =>
            {
                if (!string.IsNullOrWhiteSpace(a) && !Aliases.Contains(a)) Aliases.Add(a);
            });
            RemoveAliasCommand = ReactiveCommand.Create<string>(a => Aliases.Remove(a));
            RemoveTagCommand = ReactiveCommand.Create<string>(t => Tags.Remove(t));
        }

        public async Task SetAvatarFromBytesAsync(byte[] imageData, string fileName)
        {
            if (AvatarService == null) return;
            var avatarRef = await AvatarService.SaveToProjectAsync(imageData, fileName);
            if (avatarRef != null) ApplyAvatarRef(avatarRef);
        }

        public void ApplyAvatarRef(string avatarRef)
        {
            AvatarPath = avatarRef;
            var c = _characterService.GetById(_characterId);
            if (c != null) { c.AvatarPath = avatarRef; _characterService.Update(c); }
        }

        private void ReloadBitmap()
        {
            if (AvatarService == null || string.IsNullOrEmpty(_avatarPath)) { AvatarBitmap = null; return; }
            try { AvatarBitmap = AvatarService.LoadBitmap(_avatarPath); }
            catch (Exception ex) { _logger.Error(ex, "Reload bitmap failed"); AvatarBitmap = null; }
        }

        private void LoadFrom(Character c)
        {
            _name = c.Name; _shortDescription = c.ShortDescription;
            _color = c.Color; _fallbackIcon = c.FallbackIcon;
            _avatarPath = c.AvatarPath; _importanceLevel = c.ImportanceLevel;
            _customImportanceLabel = c.CustomImportanceLabel;
            _narrativeStartPoint = c.NarrativeStartPoint; _narrativeEndPoint = c.NarrativeEndPoint;
            _isCollective = c.IsCollective; _populationNote = c.PopulationNote;

            Aliases.Clear(); foreach (var a in c.Aliases) Aliases.Add(a);
            Tags.Clear(); foreach (var t in c.Tags) Tags.Add(t);
            ActiveStatuses.Clear(); foreach (var s in c.ActiveStatuses) ActiveStatuses.Add(s);

            if (AvatarService != null && !string.IsNullOrEmpty(_avatarPath))
                try { _avatarBitmap = AvatarService.LoadBitmap(_avatarPath); }
                catch (Exception ex) { _logger.Error(ex, "Initial avatar load failed"); }
        }

        public void ApplyTo(Character character)
        {
            character.Name = Name; character.ShortDescription = ShortDescription;
            character.Color = Color; character.FallbackIcon = FallbackIcon;
            character.AvatarPath = AvatarPath; character.ImportanceLevel = ImportanceLevel;
            character.CustomImportanceLabel = CustomImportanceLabel;
            character.NarrativeStartPoint = NarrativeStartPoint;
            character.NarrativeEndPoint = NarrativeEndPoint;
            character.IsCollective = IsCollective; character.PopulationNote = PopulationNote;
            character.Aliases = Aliases.ToList();
            character.Tags = Tags.ToList();
            character.ActiveStatuses = ActiveStatuses.ToList();
        }
    }
}