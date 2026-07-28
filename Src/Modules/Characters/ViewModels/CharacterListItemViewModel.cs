using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels
{
    public class CharacterListItemViewModel : ReactiveObject, Controls.IRowHeight
    {
        /// <summary>
        /// Высота строки в боковом списке редактора. Раскладка спрашивает её
        /// до создания контрола, шаблон берёт её же — расчёт скролла и
        /// нарисованная строка не расходятся по определению. В величину входят
        /// внешние отступы баннера: раскладка считает шаг списка, а не размер
        /// содержимого.
        /// </summary>
        public double RowHeight => 52;

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
            internal set
            {
                this.RaiseAndSetIfChanged(ref _name, value);
                // Символ-заглушка выводится из имени, когда иконка не задана —
                // переименование обновляет и его.
                this.RaisePropertyChanged(nameof(FallbackIcon));
            }
        }

        private string _shortDescription;
        public string ShortDescription
        {
            get => _shortDescription;
            internal set => this.RaiseAndSetIfChanged(ref _shortDescription, value);
        }

        // ── Совпадение при поиске ─────────────────────────────────────────
        // Имя, по которому карточка нашлась, если это не отображаемое имя.
        // Без этого поиск по списку имён работает вслепую: набрал «Вадим» —
        // в результатах «Диана», и непонятно, при чём тут она.
        //
        // Значение проставляется один раз при построении результата поиска,
        // а не вычисляется в строке списка: на трёх сотнях карточек пересчёт
        // на каждый символ запроса был бы заметен.
        private string _matchedName = string.Empty;
        public string MatchedName
        {
            get => _matchedName;
            internal set
            {
                this.RaiseAndSetIfChanged(ref _matchedName, value);
                this.RaisePropertyChanged(nameof(HasMatchedName));
            }
        }

        public bool HasMatchedName => !string.IsNullOrWhiteSpace(_matchedName);

        public string Color
        {
            get => _color;
            set
            {
                this.RaiseAndSetIfChanged(ref _color, value);
                OnColorChanged?.Invoke(Id, value);
            }
        }

        // Наружу отдаётся готовый символ для показа: заданная иконка или
        // первая буква имени (Models.CharacterGlyph). Сырое значение из
        // модели хранится в поле и наружу не выходит.
        private string _fallbackIcon;
        public string FallbackIcon
        {
            get => Models.CharacterGlyph.Resolve(_fallbackIcon, _name);
            internal set => this.RaiseAndSetIfChanged(ref _fallbackIcon, value);
        }

        // ── Метки ─────────────────────────────────────────────────────────
        // Снимок меток персонажа для отрисовки на карточке. Обновляется
        // синхронизацией из карточки персонажа (SetLabels).
        private System.Collections.Generic.List<Models.CharacterLabel> _labels = new();

        /// <summary>Есть встроенная метка «Мёртв» — карточка получает
        /// крестик-бейдж (объект-носитель смысла).</summary>
        public bool IsDead =>
            _labels.Any(l => l.Id == Models.CharacterBuiltinLabels.DeadId);

        /// <summary>Любая метка с эффектом Dim (включая «Мёртв») затемняет
        /// карточку. Затемнение — усилитель, не носитель смысла.</summary>
        public bool HasDimEffect =>
            _labels.Any(l => l.Effect == Models.CharacterLabelEffect.Dim);

        /// <summary>Приглушение строки бокового списка при эффекте Dim.</summary>
        public double DeadRowOpacity => HasDimEffect ? 0.55 : 1.0;

        // На карточке списка показываются первые метки (по порядку пользователя),
        // остальные сворачиваются в «+N». Встроенная «Мёртв» исключается — у неё
        // собственный крестик-бейдж, дубль не нужен.
        private const int MaxCardLabels = 3;

        public System.Collections.Generic.IReadOnlyList<Models.CharacterLabel> CardLabels =>
            _labels.Where(l => l.ShowOnCard && l.Id != Models.CharacterBuiltinLabels.DeadId)
                   .OrderBy(l => l.Order)
                   .Take(MaxCardLabels)
                   .ToList();

        public int CardLabelsOverflow => System.Math.Max(0,
            _labels.Count(l => l.ShowOnCard && l.Id != Models.CharacterBuiltinLabels.DeadId) - MaxCardLabels);

        public bool HasCardLabelsOverflow => CardLabelsOverflow > 0;
        public string CardLabelsOverflowText => $"+{CardLabelsOverflow}";

        internal void SetLabels(System.Collections.Generic.List<Models.CharacterLabel> labels)
        {
            _labels = labels ?? new();
            this.RaisePropertyChanged(nameof(IsDead));
            this.RaisePropertyChanged(nameof(HasDimEffect));
            this.RaisePropertyChanged(nameof(DeadRowOpacity));
            this.RaisePropertyChanged(nameof(CardLabels));
            this.RaisePropertyChanged(nameof(CardLabelsOverflow));
            this.RaisePropertyChanged(nameof(HasCardLabelsOverflow));
            this.RaisePropertyChanged(nameof(CardLabelsOverflowText));
        }

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
            _shortDescription = character.ShortDescription;
            _color = character.Color;
            _avatarRing = character.AvatarRing;
            _groupBookmark = character.GroupBookmark;
            _frameThickness = character.FrameThickness;
            _avatarStrip = character.AvatarStrip;
            _fallbackIcon = character.FallbackIcon;
            IsCollective = character.IsCollective;
            RelationshipsCount = relationshipsCount;
            IsNewlyCreated = isNewlyCreated;
            _avatarPath = character.AvatarPath;
            _avatarService = avatarService;
            _labels = character.Labels?.ToList() ?? new();

            _isBeingNamed = isNewlyCreated;
            _inlineName = isNewlyCreated ? string.Empty : character.Name;
        }
        /// <summary>
        /// Обновить аватар строки извне — например, при отмене смены аватара,
        /// когда карточка персонажа уже закрыта.
        /// </summary>
        public void ApplyAvatarRef(string? avatarRef) => SetAvatarRef(avatarRef);

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