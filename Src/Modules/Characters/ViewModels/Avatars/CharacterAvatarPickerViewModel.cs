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
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels.Avatars
{
    public class CharacterAvatarPickerItemViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerItemViewModel>();

        public string AvatarRef { get; }
        public string FileName { get; }
        public string ToolTip => System.IO.Path.GetFileNameWithoutExtension(FileName);
        public bool IsProjectAvatar { get; }

        private Bitmap? _thumbnail;
        public Bitmap? Thumbnail { get => _thumbnail; private set => this.RaiseAndSetIfChanged(ref _thumbnail, value); }

        public ReactiveCommand<Unit, Unit> SelectCommand { get; }
        public ReactiveCommand<Unit, Unit> CopyToLibraryCommand { get; }

        public CharacterAvatarPickerItemViewModel(
            CharacterAvatarItem item,
            ICharacterAvatarService svc,
            Action<string> onSelect,
            Func<string, Task>? onCopyToLibrary = null)
        {
            AvatarRef = item.AvatarRef;
            FileName = item.FileName;
            IsProjectAvatar = item.Source == CharacterAvatarSource.Project;
            SelectCommand = ReactiveCommand.Create(() => onSelect(AvatarRef));
            CopyToLibraryCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (onCopyToLibrary != null) await onCopyToLibrary(AvatarRef);
            });
            try { Thumbnail = svc.LoadBitmap(AvatarRef); }
            catch (Exception ex) { _logger.Error(ex, "Thumbnail failed {Ref}", AvatarRef); }
        }

        public void Dispose() => _thumbnail?.Dispose();
    }

    public class CharacterAvatarPackSectionViewModel : ReactiveObject
    {
        public string PackId { get; }
        public string DisplayName { get; }
        public bool IsBuiltIn { get; }
        public ObservableCollection<CharacterAvatarPickerItemViewModel> Items { get; } = new();

        private Bitmap? _iconBitmap;
        public Bitmap? IconBitmap { get => _iconBitmap; private set => this.RaiseAndSetIfChanged(ref _iconBitmap, value); }

        public bool HasItems => Items.Any();
        public bool HasNoItems => !Items.Any();

        public CharacterAvatarPackSectionViewModel(
            CharacterAvatarPackInfo pack,
            ICharacterAvatarService svc,
            Action<string> onSelect,
            Func<string, Task>? onCopyToLibrary = null)
        {
            PackId = pack.Id;
            IsBuiltIn = pack.Source == CharacterAvatarPackSource.BuiltIn;

            // Встроенные: локализация через CharactersStrings
            // Пользовательские: Name из pack.json
            DisplayName = ResolveDisplayName(pack);

            if (!string.IsNullOrEmpty(pack.IconRef))
                try { IconBitmap = svc.LoadBitmap(pack.IconRef); } catch { }

            foreach (var item in pack.Items)
                Items.Add(new CharacterAvatarPickerItemViewModel(item, svc, onSelect, onCopyToLibrary));
        }

        private static string ResolveDisplayName(CharacterAvatarPackInfo pack)
        {
            if (pack.Source == CharacterAvatarPackSource.BuiltIn)
            {
                // Ключ в CharactersStrings: AvatarPack_people_minimalism
                var localized = CharactersStrings.ResourceManager
                    .GetString(pack.LocalizationKey, CharactersStrings.Culture);
                return localized ?? pack.Id;
            }

            // Пользовательский: имя из pack.json
            if (pack.Id == "__library__")
            {
                return CharactersStrings.ResourceManager
                    .GetString("AvatarPack_library", CharactersStrings.Culture)
                    ?? "Мои аватарки";
            }

            return pack.Name ?? pack.Id;
        }

        public bool MatchesSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            return DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Items.Any(i => i.ToolTip.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            _iconBitmap?.Dispose();
            foreach (var i in Items) i.Dispose();
        }
    }

    public class CharacterAvatarPickerViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerViewModel>();

        private readonly ICharacterAvatarService _avatarService;
        public string CharacterId { get; }

        public ObservableCollection<CharacterAvatarPickerItemViewModel> ProjectAvatars { get; } = new();
        public ObservableCollection<CharacterAvatarPackSectionViewModel> Packs { get; } = new();
        public ObservableCollection<CharacterAvatarPackSectionViewModel> VisiblePacks { get; } = new();

        public bool HasProjectAvatars => ProjectAvatars.Any();
        public bool HasNoProjectAvatars => !ProjectAvatars.Any();
        public bool HasPacks => Packs.Any();

        public event Action<string>? AvatarSelected;
        public event Action? CloseRequested;
        public event Action? OpenManagerRequested;

        private string _searchQuery = string.Empty;
        public string SearchQuery
        {
            get => _searchQuery;
            set { this.RaiseAndSetIfChanged(ref _searchQuery, value); ApplySearch(); }
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public ReactiveCommand<Unit, Unit> UploadCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenManagerCommand { get; }

        public Func<Task<(byte[] data, string name)?>>? RequestFilePicker { get; set; }

        public CharacterAvatarPickerViewModel(ICharacterAvatarService avatarService, string characterId)
        {
            _avatarService = avatarService;
            CharacterId = characterId;

            UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync);
            CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
            OpenManagerCommand = ReactiveCommand.Create(() => OpenManagerRequested?.Invoke());

            Refresh();
        }

        public void Refresh()
        {
            foreach (var p in Packs) p.Dispose();
            foreach (var i in ProjectAvatars) i.Dispose();
            ProjectAvatars.Clear();
            Packs.Clear();
            VisiblePacks.Clear();

            foreach (var item in _avatarService.GetProjectAvatars())
                ProjectAvatars.Add(new CharacterAvatarPickerItemViewModel(
                    item, _avatarService,
                    onSelect: SelectAvatar,
                    onCopyToLibrary: CopyToLibraryAsync));

            foreach (var pack in _avatarService.GetAllPacks())
            {
                var section = new CharacterAvatarPackSectionViewModel(
                    pack, _avatarService,
                    onSelect: SelectAvatar);
                Packs.Add(section);
                VisiblePacks.Add(section);
            }

            this.RaisePropertyChanged(nameof(HasProjectAvatars));
            this.RaisePropertyChanged(nameof(HasNoProjectAvatars));
            this.RaisePropertyChanged(nameof(HasPacks));
        }

        private void SelectAvatar(string avatarRef)
        {
            AvatarSelected?.Invoke(avatarRef);
            CloseRequested?.Invoke();
        }

        private async Task CopyToLibraryAsync(string projectRef)
        {
            var libRef = await _avatarService.CopyProjectAvatarToLibraryAsync(projectRef);
            if (libRef != null)
            {
                StatusMessage = CharactersStrings.ResourceManager
                    .GetString("AvatarPicker_SavedToLibrary") ?? "Сохранено в библиотеку.";
                Refresh();
            }
        }

        public async Task HandleImageBytesAsync(byte[] imageData, string fileName)
        {
            StatusMessage = CharactersStrings.ResourceManager
                .GetString("AvatarPicker_Saving") ?? "Сохранение…";
            var avatarRef = await _avatarService.SaveToProjectAsync(imageData, fileName);
            if (avatarRef == null)
            {
                StatusMessage = CharactersStrings.ResourceManager
                    .GetString("AvatarPicker_SaveFailed") ?? "Не удалось сохранить.";
                return;
            }
            SelectAvatar(avatarRef);
        }

        private async Task UploadAsync()
        {
            if (RequestFilePicker == null) return;
            var result = await RequestFilePicker();
            if (result != null) await HandleImageBytesAsync(result.Value.data, result.Value.name);
        }

        private void ApplySearch()
        {
            VisiblePacks.Clear();
            foreach (var pack in Packs)
                if (pack.MatchesSearch(_searchQuery))
                    VisiblePacks.Add(pack);
        }
    }
}