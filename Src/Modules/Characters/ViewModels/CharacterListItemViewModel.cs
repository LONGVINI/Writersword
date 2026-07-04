using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels
{
    public class CharacterListItemViewModel : ReactiveObject
    {
        private bool _isBeingNamed;
        private string _inlineName = string.Empty;
        private bool _isRenaming;
        private string _pendingRename = string.Empty;
        private bool _isSelected;
        private bool _isDragging;
        private string _name;
        private string _color;
        private string? _avatarPath;
        private readonly ICharacterAvatarService? _avatarService;
        private Bitmap? _avatarBitmap;
        private bool _bitmapLoaded;


        public string Id { get; }

        public string Name
        {
            get => _name;
            internal set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        public string ShortDescription { get; }

        public string Color
        {
            get => _color;
            set
            {
                this.RaiseAndSetIfChanged(ref _color, value);
                OnColorChanged?.Invoke(Id, value);
            }
        }

        public string FallbackIcon { get; }
        public string? AvatarPath => _avatarPath;
        public bool IsCollective { get; }

        // Доп. функция: цветное кольцо вокруг аватара (цветом персонажа).
        private bool _avatarRing;
        public bool AvatarRing
        {
            get => _avatarRing;
            set
            {
                this.RaiseAndSetIfChanged(ref _avatarRing, value);
                OnAvatarRingChanged?.Invoke(Id, value);
            }
        }

        // Закладка-ленточка на карточке группы. Хранится в модели персонажа,
        // переключается из редактора цвета. Показ на карточке — ShowGroupBookmark:
        // закладка рисуется только у групп и только когда включена.
        private bool _groupBookmark;
        public bool GroupBookmark
        {
            get => _groupBookmark;
            set
            {
                this.RaiseAndSetIfChanged(ref _groupBookmark, value);
                this.RaisePropertyChanged(nameof(ShowGroupBookmark));
                OnGroupBookmarkChanged?.Invoke(Id, value);
            }
        }

        public bool ShowGroupBookmark => IsCollective && _groupBookmark;

        // Ленивая загрузка — битмап создаётся только при первом обращении.
        public Bitmap? AvatarBitmap
        {
            get
            {
                if (!_bitmapLoaded)
                {
                    _bitmapLoaded = true;
                    if (!string.IsNullOrEmpty(_avatarPath) && _avatarService != null)
                        try { _avatarBitmap = _avatarService.LoadBitmap(_avatarPath); }
                        catch { }
                }
                return _avatarBitmap;
            }
        }
        public int RelationshipsCount { get; }
        public bool IsNewlyCreated { get; }

        public bool IsBeingNamed
        {
            get => _isBeingNamed;
            set
            {
                this.RaiseAndSetIfChanged(ref _isBeingNamed, value);
                this.RaisePropertyChanged(nameof(IsShowingNameDisplay));
            }
        }

        public string InlineName
        {
            get => _inlineName;
            set => this.RaiseAndSetIfChanged(ref _inlineName, value);
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                this.RaiseAndSetIfChanged(ref _isRenaming, value);
                this.RaisePropertyChanged(nameof(IsShowingNameDisplay));
            }
        }

        public string PendingRename
        {
            get => _pendingRename;
            set => this.RaiseAndSetIfChanged(ref _pendingRename, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        // true ïîêà êàðòî÷êà ôèçè÷åñêè ïåðåòàñêèâàåòñÿ  äåëàåò å¸ ïîëóïðîçðà÷íîé
        // è îòêëþ÷àåò hit-òåñò ÷òîáû íå ìåøàòü îïðåäåëåíèþ öåëè âñòàâêè
        public bool IsDragging
        {
            get => _isDragging;
            set
            {
                this.RaiseAndSetIfChanged(ref _isDragging, value);
                this.RaisePropertyChanged(nameof(DragOpacity));
            }
        }

        // 0.25 âî âðåìÿ drag, 1.0 â îáû÷íîì ñîñòîÿíèè  áèíäèòñÿ ê Opacity êàðòî÷êè
        // true êîãäà ýòî ïóñòîé placeholder âî âðåìÿ drag
        private bool _isPlaceholder;
        public bool IsPlaceholder
        {
            get => _isPlaceholder;
            set
            {
                this.RaiseAndSetIfChanged(ref _isPlaceholder, value);
                this.RaisePropertyChanged(nameof(DragOpacity));
                this.RaisePropertyChanged(nameof(IsShowingNameDisplay));
            }
        }

        public double DragOpacity => _isPlaceholder ? 0.35 : 1.0;

        // true êîãäà íå â ðåæèìå ââîäà/ïåðåèìåíîâàíèÿ  ïîêàçûâàåò íîðìàëüíîå îòîáðàæåíèå
        public bool IsShowingNameDisplay => !_isBeingNamed && !_isRenaming;

        // êîëáýêè, óñòàíàâëèâàþòñÿ ðîäèòåëüñêèì ViewModel
        public Action<string, string>? OnConfirmName { get; set; }    // (id, newName)
        public Action<string>? OnCancelNewCharacter { get; set; }     // (id)
        public Action<string>? OnDeleteRequested { get; set; }        // (id)
        public Action<string, string>? OnColorChanged { get; set; }   // (id, newColor)
        public Action<string, bool>? OnAvatarRingChanged { get; set; } // (id, ringEnabled)
        public Action<string, bool>? OnGroupBookmarkChanged { get; set; } // (id, bookmarkEnabled)
        public Action<bool>? OnApplyRingToAll { get; set; }            // (ringEnabled — ко всем персонажам)

        // êîìàíäû  âûïîëíÿþòñÿ èç AXAML íàïðÿìóþ ÷åðåç {Binding}
        public ReactiveCommand<Unit, Unit> ConfirmNameCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelNameCommand { get; }
        public ReactiveCommand<Unit, Unit> StartRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> RequestDeleteCommand { get; }
        public ReactiveCommand<bool, Unit> ApplyRingToAllCommand { get; }

        // Аватар — открытие пикера из списка персонажей.
        // RequestPickerOpen задаётся из code-behind CharactersListView.
        public Func<Task<string?>>? RequestPickerOpen { get; set; }
        public ReactiveCommand<Unit, Unit> OpenAvatarPickerCommand { get; }
        public ReactiveCommand<Unit, Unit> RemoveAvatarCommand { get; }

        public CharacterListItemViewModel(
            Models.Character character,
            int relationshipsCount = 0,
            bool isNewlyCreated = false,
            ICharacterAvatarService? avatarService = null)
        {
            Id = character.Id;
            _name = character.Name;
            ShortDescription = character.ShortDescription;
            _color = character.Color;
            _avatarRing = character.AvatarRing;
            _groupBookmark = character.GroupBookmark;
            FallbackIcon = character.FallbackIcon;
            IsCollective = character.IsCollective;
            RelationshipsCount = relationshipsCount;
            IsNewlyCreated = isNewlyCreated;
            _avatarPath = character.AvatarPath;
            _avatarService = avatarService;

            OpenAvatarPickerCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (RequestPickerOpen == null) return;
                var result = await RequestPickerOpen();
                if (result != null) SetAvatarRef(result);
            });

            RemoveAvatarCommand = ReactiveCommand.Create(() =>
            {
                _avatarService?.DeleteAvatar(_avatarPath);
                SetAvatarRef(null);
            });

            _isBeingNamed = isNewlyCreated;
            _inlineName = isNewlyCreated ? string.Empty : character.Name;

            ConfirmNameCommand = ReactiveCommand.Create(() =>
            {
                var resolved = string.IsNullOrWhiteSpace(InlineName)
                    ? CharactersStrings.Character_DefaultName
                    : InlineName.Trim();
                Name = resolved;
                IsBeingNamed = false;
                OnConfirmName?.Invoke(Id, resolved);
            });

            CancelNameCommand = ReactiveCommand.Create(() =>
            {
                IsBeingNamed = false;
                OnCancelNewCharacter?.Invoke(Id);
            });

            StartRenameCommand = ReactiveCommand.Create(() =>
            {
                PendingRename = Name;
                IsRenaming = true;
            });

            ConfirmRenameCommand = ReactiveCommand.Create(() =>
            {
                var resolved = string.IsNullOrWhiteSpace(PendingRename) ? Name : PendingRename.Trim();
                Name = resolved;
                IsRenaming = false;
                OnConfirmName?.Invoke(Id, resolved);
            });

            CancelRenameCommand = ReactiveCommand.Create(() =>
            {
                IsRenaming = false;
            });

            RequestDeleteCommand = ReactiveCommand.Create(() =>
            {
                OnDeleteRequested?.Invoke(Id);
            });

            ApplyRingToAllCommand = ReactiveCommand.Create<bool>(v =>
            {
                OnApplyRingToAll?.Invoke(v);
            });
        }
        private void SetAvatarRef(string? avatarRef)
        {
            _bitmapLoaded = false;
            _avatarBitmap?.Dispose();
            _avatarBitmap = null;
            // Через field чтобы не трогать readonly
            _avatarPath = avatarRef;
            this.RaisePropertyChanged(nameof(AvatarPath));
            this.RaisePropertyChanged(nameof(AvatarBitmap));
            OnAvatarChanged?.Invoke(Id, avatarRef);
        }

        // Сбросить кэш аватарки (вызывается снаружи при обновлении).
        public void RefreshAvatar()
        {
            _bitmapLoaded = false;
            _avatarBitmap?.Dispose();
            _avatarBitmap = null;
            this.RaisePropertyChanged(nameof(AvatarBitmap));
        }

        // Колбэк уведомления родительского VM о смене аватарки.
        public Action<string, string?>? OnAvatarChanged { get; set; }
    }
}