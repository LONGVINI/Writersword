using Avalonia.Media.Imaging;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.ViewModels.Avatars
{
    public class CharacterAvatarPickerItemViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerItemViewModel>();

        public string AvatarRef { get; }
        public string FileName { get; }
        public CharacterAvatarSource Source { get; }
        public bool IsProjectAvatar => Source == CharacterAvatarSource.Project;

        private Bitmap? _thumbnail;
        public Bitmap? Thumbnail
        {
            get => _thumbnail;
            private set => this.RaiseAndSetIfChanged(ref _thumbnail, value);
        }

        public ReactiveCommand<Unit, Unit> SelectCommand { get; }
        public ReactiveCommand<Unit, Unit> CopyToLibraryCommand { get; }

        public CharacterAvatarPickerItemViewModel(
            CharacterAvatarItem item,
            ICharacterAvatarService avatarService,
            Action<string> onSelect,
            Func<string, Task>? onCopyToLibrary = null)
        {
            AvatarRef = item.AvatarRef;
            FileName = item.FileName;
            Source = item.Source;

            SelectCommand = ReactiveCommand.Create(() => onSelect(AvatarRef));

            CopyToLibraryCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (onCopyToLibrary != null) await onCopyToLibrary(AvatarRef);
            });

            try { Thumbnail = avatarService.LoadBitmap(AvatarRef); }
            catch (Exception ex) { _logger.Error(ex, "Failed to load thumbnail {Ref}", AvatarRef); }
        }

        public void Dispose() => _thumbnail?.Dispose();
    }

    public class CharacterAvatarPickerViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerViewModel>();

        private readonly ICharacterAvatarService _avatarService;
        public string CharacterId { get; }

        public ObservableCollection<CharacterAvatarPickerItemViewModel> ProjectAvatars { get; } = new();
        public ObservableCollection<CharacterAvatarPickerItemViewModel> LibraryAvatars { get; } = new();
        public ObservableCollection<CharacterAvatarPickerItemViewModel> BuiltInAvatars { get; } = new();

        public bool HasProjectAvatars => ProjectAvatars.Count > 0;
        public bool HasNoProjectAvatars => ProjectAvatars.Count == 0;
        public bool HasLibraryAvatars => LibraryAvatars.Count > 0;
        public bool HasNoLibraryAvatars => LibraryAvatars.Count == 0;
        public bool HasBuiltInAvatars => BuiltInAvatars.Count > 0;
        public bool HasNoBuiltInAvatars => BuiltInAvatars.Count == 0;

        public event Action<string>? AvatarSelected;
        public event Action? CloseRequested;

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public ReactiveCommand<Unit, Unit> UploadCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        // Устанавливается из code-behind: открывает системный диалог выбора файла.
        public Func<Task<(byte[] data, string name)?>>? RequestFilePicker { get; set; }

        public CharacterAvatarPickerViewModel(ICharacterAvatarService avatarService, string characterId)
        {
            _avatarService = avatarService;
            CharacterId = characterId;

            UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync);
            CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());

            Refresh();
        }

        public void Refresh()
        {
            foreach (var item in ProjectAvatars) item.Dispose();
            ProjectAvatars.Clear();

            foreach (var item in LibraryAvatars) item.Dispose();
            LibraryAvatars.Clear();

            foreach (var item in _avatarService.GetProjectAvatars())
                ProjectAvatars.Add(CreateItem(item));

            foreach (var item in _avatarService.GetLibraryAvatars())
                LibraryAvatars.Add(CreateItem(item));

            foreach (var item in _avatarService.GetBuiltInAvatars())
                BuiltInAvatars.Add(new CharacterAvatarPickerItemViewModel(
                    item, _avatarService,
                    onSelect: ref_ => { AvatarSelected?.Invoke(ref_); CloseRequested?.Invoke(); }));

            this.RaisePropertyChanged(nameof(HasProjectAvatars));
            this.RaisePropertyChanged(nameof(HasNoProjectAvatars));
            this.RaisePropertyChanged(nameof(HasLibraryAvatars));
            this.RaisePropertyChanged(nameof(HasNoLibraryAvatars));
            this.RaisePropertyChanged(nameof(HasBuiltInAvatars));
            this.RaisePropertyChanged(nameof(HasNoBuiltInAvatars));
        }

        public async Task HandleImageBytesAsync(byte[] imageData, string fileName)
        {
            StatusMessage = "Сохранение…";
            var avatarRef = await _avatarService.SaveToProjectAsync(imageData, fileName);
            if (avatarRef == null)
            {
                StatusMessage = "Не удалось сохранить изображение.";
                return;
            }

            AvatarSelected?.Invoke(avatarRef);
            CloseRequested?.Invoke();
        }

        private async Task UploadAsync()
        {
            if (RequestFilePicker == null) return;
            var result = await RequestFilePicker();
            if (result != null) await HandleImageBytesAsync(result.Value.data, result.Value.name);
        }

        private async Task CopyToLibraryAsync(string projectRef)
        {
            var libRef = await _avatarService.CopyProjectAvatarToLibraryAsync(projectRef);
            if (libRef != null) { StatusMessage = "Сохранено в библиотеку."; Refresh(); }
        }

        private CharacterAvatarPickerItemViewModel CreateItem(CharacterAvatarItem item) =>
            new(item, _avatarService,
                onSelect: ref_ => { AvatarSelected?.Invoke(ref_); CloseRequested?.Invoke(); },
                onCopyToLibrary: item.Source == CharacterAvatarSource.Project
                    ? CopyToLibraryAsync : null);
    }
}