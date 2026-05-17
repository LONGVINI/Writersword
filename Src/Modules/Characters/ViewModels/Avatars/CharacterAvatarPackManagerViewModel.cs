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
    public class CharacterAvatarPackManagerItemViewModel
    {
        public string AvatarRef { get; }
        public string FileName { get; }
        public string ToolTip => System.IO.Path.GetFileNameWithoutExtension(FileName);
        public Avalonia.Media.Imaging.Bitmap? Thumbnail { get; }

        public CharacterAvatarPackManagerItemViewModel(CharacterAvatarItem item, ICharacterAvatarService svc)
        {
            AvatarRef = item.AvatarRef;
            FileName = item.FileName;
            try { Thumbnail = svc.LoadBitmap(item.AvatarRef); } catch { }
        }
    }

    public class CharacterAvatarPackManagerPackViewModel : ReactiveObject
    {
        public string PackId { get; }
        public string DisplayName { get; }
        public bool IsBuiltIn { get; }
        public bool IsUserPack => !IsBuiltIn;
        public ObservableCollection<CharacterAvatarPackManagerItemViewModel> Items { get; } = new();

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

        public CharacterAvatarPackManagerPackViewModel(CharacterAvatarPackInfo pack, ICharacterAvatarService svc)
        {
            PackId = pack.Id;
            IsBuiltIn = pack.Source == CharacterAvatarPackSource.BuiltIn;

            if (IsBuiltIn)
            {
                DisplayName = CharactersStrings.ResourceManager
                    .GetString(pack.LocalizationKey, CharactersStrings.Culture) ?? pack.Id;
            }
            else if (pack.Id == "__library__")
            {
                DisplayName = CharactersStrings.ResourceManager
                    .GetString("AvatarPack_library") ?? "Мои аватарки";
            }
            else
            {
                DisplayName = pack.Name ?? pack.Id;
            }

            foreach (var item in pack.Items)
                Items.Add(new CharacterAvatarPackManagerItemViewModel(item, svc));
        }
    }

    public class CharacterAvatarPackManagerViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPackManagerViewModel>();
        private readonly ICharacterAvatarService _avatarService;

        public ObservableCollection<CharacterAvatarPackManagerPackViewModel> Packs { get; } = new();
        public event Action? CloseRequested;

        private CharacterAvatarPackManagerPackViewModel? _selectedPack;
        public CharacterAvatarPackManagerPackViewModel? SelectedPack
        {
            get => _selectedPack;
            set
            {
                if (_selectedPack != null) _selectedPack.IsSelected = false;
                this.RaiseAndSetIfChanged(ref _selectedPack, value);
                if (_selectedPack != null) _selectedPack.IsSelected = true;
                this.RaisePropertyChanged(nameof(HasSelectedPack));
                this.RaisePropertyChanged(nameof(CanEditPack));
            }
        }

        public bool HasSelectedPack => SelectedPack != null;
        public bool CanEditPack => SelectedPack?.IsUserPack == true;

        private string _newPackName = string.Empty;
        public string NewPackName
        {
            get => _newPackName;
            set => this.RaiseAndSetIfChanged(ref _newPackName, value);
        }

        public ReactiveCommand<Unit, Unit> CloseCommand { get; }
        public ReactiveCommand<Unit, Unit> CreatePackCommand { get; }
        public ReactiveCommand<Unit, Unit> DeletePackCommand { get; }
        public ReactiveCommand<string, Unit> SelectPackCommand { get; }
        public ReactiveCommand<Unit, Unit> ImportPackCommand { get; }
        public ReactiveCommand<Unit, Unit> ExportPackCommand { get; }

        public Func<Task<string?>>? RequestZipImportPicker { get; set; }
        public Func<string, Task<string?>>? RequestZipExportPicker { get; set; }

        public CharacterAvatarPackManagerViewModel(ICharacterAvatarService avatarService)
        {
            _avatarService = avatarService;

            CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());

            CreatePackCommand = ReactiveCommand.Create(() =>
            {
                if (string.IsNullOrWhiteSpace(NewPackName)) return;
                var pack = _avatarService.CreateUserPack(NewPackName);
                var vm = new CharacterAvatarPackManagerPackViewModel(pack, _avatarService);
                Packs.Add(vm);
                SelectedPack = vm;
                NewPackName = string.Empty;
            });

            DeletePackCommand = ReactiveCommand.Create(() =>
            {
                if (SelectedPack?.IsUserPack != true) return;
                _avatarService.DeleteUserPack(SelectedPack.PackId);
                Packs.Remove(SelectedPack);
                SelectedPack = Packs.FirstOrDefault();
            });

            SelectPackCommand = ReactiveCommand.Create<string>(id =>
                SelectedPack = Packs.FirstOrDefault(p => p.PackId == id));

            ImportPackCommand = ReactiveCommand.CreateFromTask(ImportAsync);
            ExportPackCommand = ReactiveCommand.CreateFromTask(ExportAsync);

            Refresh();
        }

        public void Refresh()
        {
            Packs.Clear();
            foreach (var pack in _avatarService.GetAllPacks())
                Packs.Add(new CharacterAvatarPackManagerPackViewModel(pack, _avatarService));
            SelectedPack = Packs.FirstOrDefault();
        }

        private async Task ImportAsync()
        {
            if (RequestZipImportPicker == null) return;
            var path = await RequestZipImportPicker();
            if (path == null) return;
            var pack = await _avatarService.ImportPackFromZipAsync(path);
            if (pack != null)
            {
                var vm = new CharacterAvatarPackManagerPackViewModel(pack, _avatarService);
                Packs.Add(vm);
                SelectedPack = vm;
            }
        }

        private async Task ExportAsync()
        {
            if (SelectedPack == null || RequestZipExportPicker == null) return;
            var path = await RequestZipExportPicker(SelectedPack.DisplayName);
            if (path != null)
                await _avatarService.ExportPackToZipAsync(SelectedPack.PackId, path);
        }
    }
}