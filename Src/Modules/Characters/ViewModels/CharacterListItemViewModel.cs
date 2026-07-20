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

        // Цвет папки, из которой взят персонаж, — для точки-индикатора справа
        // на баннере в результатах поиска Редактора. Заполняется при построении
        // FilteredCharacters; null (персонаж без папки) — точка не показывается.
        private string? _searchFolderColor;
        public string? SearchFolderColor
        {
            get => _searchFolderColor;
            set
            {
                this.RaiseAndSetIfChanged(ref _searchFolderColor, value);
                this.RaisePropertyChanged(nameof(ShowSearchFolderDot));
            }
        }

        public bool ShowSearchFolderDot => !string.IsNullOrEmpty(_searchFolderColor);

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

        // Толщина цветной рамки карточки. Хранится в модели персонажа; карточка
        // биндится к CardBorderThickness (готовый Thickness для Border).
        private double _frameThickness = 2;
        public double FrameThickness
        {
            get => _frameThickness;
            set
            {
                this.RaiseAndSetIfChanged(ref _frameThickness, value);
                this.RaisePropertyChanged(nameof(CardBorderThickness));
                OnFrameThicknessChanged?.Invoke(Id, value);
            }
        }

        public Avalonia.Thickness CardBorderThickness => new(_frameThickness);

        // Вид аватара: кружок (по умолчанию) или «полоска» — картинка/заливка
        // на всю верхнюю зону карточки. Видимость вариантов — через Show*-свойства,
        // чтобы разметка не собирала условия из нескольких биндингов.
        private bool _avatarStrip;
        public bool AvatarStrip
        {
            get => _avatarStrip;
            set
            {
                this.RaiseAndSetIfChanged(ref _avatarStrip, value);
                RaiseAvatarViewProps();
                OnAvatarStripChanged?.Invoke(Id, value);
            }
        }

        public bool ShowAvatarStrip => _avatarStrip;
        public bool ShowCircleNoPhoto => !_avatarStrip && string.IsNullOrEmpty(_avatarPath);
        public bool ShowCirclePhoto => !_avatarStrip && !string.IsNullOrEmpty(_avatarPath);

        private void RaiseAvatarViewProps()
        {
            this.RaisePropertyChanged(nameof(ShowAvatarStrip));
            this.RaisePropertyChanged(nameof(ShowCircleNoPhoto));
            this.RaisePropertyChanged(nameof(ShowCirclePhoto));
        }

        // Размер декодирования аватара для карточки списка. Кружок ~48px, полоска и
        // крупные плитки — до ~150px логических; с запасом на high-DPI хватает 256.
        // Полноразмерное 512 (AvatarMaxSide в сервисе) остаётся редактору/пикеру, где
        // картинка крупная. Меньший размер = вчетверо меньше памяти и текстуры GPU на
        // каждую карточку из сотен.
        private const int CardAvatarDecodeSize = 256;

        // Ленивая загрузка — битмап создаётся только при первом обращении.
        public Bitmap? AvatarBitmap
        {
            get
            {
                if (!_bitmapLoaded)
                {
                    _bitmapLoaded = true;
                    if (!string.IsNullOrEmpty(_avatarPath) && _avatarService != null)
                        try { _avatarBitmap = _avatarService.LoadBitmap(_avatarPath, CardAvatarDecodeSize); }
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
        public Action<string, double>? OnFrameThicknessChanged { get; set; } // (id, толщина рамки)
        public Action<string, bool>? OnAvatarStripChanged { get; set; } // (id, полоска вместо кружка)
        public Action<bool>? OnApplyRingToAll { get; set; }            // (ringEnabled — ко всем персонажам)

        // êîìàíäû  âûïîëíÿþòñÿ èç AXAML íàïðÿìóþ ÷åðåç {Binding}
        // Команды создаются ЛЕНИВО, при первом обращении из шаблона карточки.
        // Конструктор VM обязан быть дешёвым: раньше он создавал 9 ReactiveCommand
        // на каждого персонажа (Rx-машинерия), из-за чего создание сотен VM при
        // загрузке списка стоило секунды и дёргало прокрутку Редактора. Вкладка
        // «Редактор» эти команды не биндит, поэтому её строки теперь создаются
        // мгновенно, а карточки платят за команды только при реальной отрисовке.
        private ReactiveCommand<Unit, Unit>? _confirmNameCommand;
        public ReactiveCommand<Unit, Unit> ConfirmNameCommand => _confirmNameCommand ??= ReactiveCommand.Create(() =>
        {
            var resolved = string.IsNullOrWhiteSpace(InlineName)
                ? CharactersStrings.Character_DefaultName
                : InlineName.Trim();
            Name = resolved;
            IsBeingNamed = false;
            OnConfirmName?.Invoke(Id, resolved);
        });

        private ReactiveCommand<Unit, Unit>? _cancelNameCommand;
        public ReactiveCommand<Unit, Unit> CancelNameCommand => _cancelNameCommand ??= ReactiveCommand.Create(() =>
        {
            IsBeingNamed = false;
            OnCancelNewCharacter?.Invoke(Id);
        });

        private ReactiveCommand<Unit, Unit>? _startRenameCommand;
        public ReactiveCommand<Unit, Unit> StartRenameCommand => _startRenameCommand ??= ReactiveCommand.Create(() =>
        {
            PendingRename = Name;
            IsRenaming = true;
        });

        private ReactiveCommand<Unit, Unit>? _confirmRenameCommand;
        public ReactiveCommand<Unit, Unit> ConfirmRenameCommand => _confirmRenameCommand ??= ReactiveCommand.Create(() =>
        {
            var resolved = string.IsNullOrWhiteSpace(PendingRename) ? Name : PendingRename.Trim();
            Name = resolved;
            IsRenaming = false;
            OnConfirmName?.Invoke(Id, resolved);
        });

        private ReactiveCommand<Unit, Unit>? _cancelRenameCommand;
        public ReactiveCommand<Unit, Unit> CancelRenameCommand => _cancelRenameCommand ??= ReactiveCommand.Create(() =>
        {
            IsRenaming = false;
        });

        private ReactiveCommand<Unit, Unit>? _requestDeleteCommand;
        public ReactiveCommand<Unit, Unit> RequestDeleteCommand => _requestDeleteCommand ??= ReactiveCommand.Create(() =>
        {
            OnDeleteRequested?.Invoke(Id);
        });

        private ReactiveCommand<bool, Unit>? _applyRingToAllCommand;
        public ReactiveCommand<bool, Unit> ApplyRingToAllCommand => _applyRingToAllCommand ??= ReactiveCommand.Create<bool>(v =>
        {
            OnApplyRingToAll?.Invoke(v);
        });

        // Аватар — открытие пикера из списка персонажей.
        // RequestPickerOpen задаётся из code-behind CharactersListView.
        public Func<Task<string?>>? RequestPickerOpen { get; set; }

        private ReactiveCommand<Unit, Unit>? _openAvatarPickerCommand;
        public ReactiveCommand<Unit, Unit> OpenAvatarPickerCommand => _openAvatarPickerCommand ??= ReactiveCommand.CreateFromTask(async () =>
        {
            if (RequestPickerOpen == null) return;
            var result = await RequestPickerOpen();
            if (result != null) SetAvatarRef(result);
        });

        private ReactiveCommand<Unit, Unit>? _removeAvatarCommand;
        public ReactiveCommand<Unit, Unit> RemoveAvatarCommand => _removeAvatarCommand ??= ReactiveCommand.Create(() =>
        {
            _avatarService?.DeleteAvatar(_avatarPath);
            SetAvatarRef(null);
        });

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
            _frameThickness = character.FrameThickness;
            _avatarStrip = character.AvatarStrip;
            FallbackIcon = character.FallbackIcon;
            IsCollective = character.IsCollective;
            RelationshipsCount = relationshipsCount;
            IsNewlyCreated = isNewlyCreated;
            _avatarPath = character.AvatarPath;
            _avatarService = avatarService;

            _isBeingNamed = isNewlyCreated;
            _inlineName = isNewlyCreated ? string.Empty : character.Name;
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
            RaiseAvatarViewProps();   // видимость кружка с фото/без зависит от пути
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