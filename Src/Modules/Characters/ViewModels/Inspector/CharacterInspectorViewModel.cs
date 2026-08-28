using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Models;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels.Inspector
{
    /// <summary>
    /// Боковая панель оформления карточки. Читает состояние у первой выбранной
    /// карточки, а пишет во все выбранные сразу — потому в ней и нет
    /// переключателей «применить ко всем», которые были нужны окну настроек
    /// одной карточки.
    ///
    /// Панель ничего не подтверждает: каждая правка уходит персонажу немедленно
    /// и откатывается через Ctrl+Z, для чего в CharactersActions заведены
    /// команды на кольцо, закладку, толщину рамки и вид аватара.
    ///
    /// Отдельно живёт толщина рамки. Её тянут ползунком, и запись на каждое
    /// движение означала бы сотню обращений к проекту и сотню шагов истории за
    /// один жест. Поэтому у неё два пути: ThicknessPreview рисует, не записывая,
    /// а CommitThickness записывает один раз, когда ползунок отпустили.
    /// </summary>
    public class CharacterInspectorViewModel : ReactiveObject
    {
        private readonly CharactersViewModel _owner;

        public CharacterInspectorViewModel(CharactersViewModel owner)
        {
            _owner = owner;

            QuickAvatars = new CharacterQuickAvatarsViewModel(PickQuickAvatar);

            CloseCommand = ReactiveCommand.Create(() => { _owner.ClearSelection(); });

            CycleImportanceCommand = ReactiveCommand.Create(() =>
            {
                using (_owner.BeginUndoBatch("важность выбранных карточек"))
                {
                    foreach (var card in Targets.ToList())
                        card.CycleImportance();
                }

                this.RaisePropertyChanged(nameof(ImportanceMark));
                this.RaisePropertyChanged(nameof(ImportanceLevel));
            });

            ChooseAvatarCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                var target = Primary;
                if (target is null) return;
                await target.OpenAvatarPickerCommand.Execute();
            });
            SetAvatarCircleCommand = ReactiveCommand.Create(() => { AvatarStrip = false; });
            SetAvatarStripCommand = ReactiveCommand.Create(() => { AvatarStrip = true; });

            // Кольцо разом по всему проекту. Подтверждение не модальным окном, а
            // раскрытием самой строки в две кнопки — тот же приём, что у
            // удаления в менеджере наборов аватарок: действие задевает все
            // карточки, и промах здесь стоит дорого.
            RequestRingAllCommand = ReactiveCommand.Create(() => { IsConfirmingRingAll = true; });
            CancelRingAllCommand = ReactiveCommand.Create(() => { IsConfirmingRingAll = false; });
            ConfirmRingAllCommand = ReactiveCommand.Create(() => ApplyRingToEveryone(Ring));

            // Быстрое добавление метки по имени — тот же ход, что и в
            // автодополнении на вкладке «Основное»: совпадение с уже
            // известной проекту меткой подхватывает её целиком (значок,
            // цвет, эффект), иначе заводится новая с настройками по
            // умолчанию через тот же UpsertLabel, что и у полного редактора.
            AddLabelCommand = ReactiveCommand.Create<string>(name =>
            {
                if (!CanEditLabels) return;
                var trimmed = name?.Trim();
                if (string.IsNullOrEmpty(trimmed)) return;
                if (Labels.Any(l => string.Equals(l.Name, trimmed, StringComparison.CurrentCultureIgnoreCase))) return;

                var known = _owner.CharacterService.GetAllLabels()
                    .FirstOrDefault(l => string.Equals(l.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));

                var label = known != null
                    ? new CharacterLabel
                    {
                        Id = known.Id,
                        Name = known.Name,
                        Icon = known.Icon,
                        IconImage = known.IconImage,
                        Color = known.Color,
                        IconColor = known.IconColor,
                        ShowBackdrop = known.ShowBackdrop,
                        Effect = known.Effect,
                        ShowOnCard = known.ShowOnCard,
                        Description = known.Description,
                        Order = Labels.Count
                    }
                    : new CharacterLabel { Name = trimmed, Order = Labels.Count };

                UpsertLabel(label, applyToAll: false);
                ReloadKnownLabels();
            });
        }

        // ── Выбранные карточки ─────────────────────────────────────────────

        private IReadOnlyList<CharacterListItemViewModel> Targets => _owner.SelectedCards;

        private CharacterListItemViewModel? Primary =>
            _owner.SelectedCards.Count > 0 ? _owner.SelectedCards[0] : null;

        public bool HasSelection => _owner.SelectedCards.Count > 0;
        public bool IsSingle => _owner.SelectedCards.Count == 1;
        public bool IsMultiple => _owner.SelectedCards.Count > 1;

        /// <summary>Подпись под именем: одна карточка или сколько выбрано.</summary>
        public string SelectionCaption
        {
            get
            {
                var count = _owner.SelectedCards.Count;
                if (count == 0) return string.Empty;
                if (count == 1) return "один персонаж";
                return $"выбрано карточек: {count}";
            }
        }

        /// <summary>
        /// Закладка есть только у групп, и показывать её строку одиночному
        /// персонажу незачем. При множественном выборе строка показывается,
        /// если группа есть хотя бы одна.
        /// </summary>
        public bool ShowBookmarkRow => _owner.SelectedCards.Any(x => x.IsCollective);

        /// <summary>
        /// Кольцо рисуется вокруг кружка, у полоски его нет. Строка гаснет,
        /// когда все выбранные показаны полоской, — иначе галочка меняла бы
        /// то, чего на карточке не видно.
        /// </summary>
        public bool ShowRingRow => _owner.SelectedCards.Any(x => !x.AvatarStrip);

        /// <summary>
        /// Список сменился снаружи: перевыбрали карточки, или их пересобрали
        /// фильтры. Панель обязана перечитать всё, что показывает.
        /// </summary>
        public void OnSelectionChanged()
        {
            _thicknessDrag = false;
            IsConfirmingRingAll = false;

            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(IsSingle));
            this.RaisePropertyChanged(nameof(IsMultiple));
            this.RaisePropertyChanged(nameof(SelectionCaption));
            this.RaisePropertyChanged(nameof(ShowBookmarkRow));
            this.RaisePropertyChanged(nameof(ShowRingRow));

            RaiseValueProperties();
        }

        private void RaiseValueProperties()
        {
            this.RaisePropertyChanged(nameof(Name));
            this.RaisePropertyChanged(nameof(Color));
            this.RaisePropertyChanged(nameof(Ring));
            this.RaisePropertyChanged(nameof(RingAllQuestion));
            this.RaisePropertyChanged(nameof(Bookmark));
            this.RaisePropertyChanged(nameof(AvatarStrip));
            this.RaisePropertyChanged(nameof(AvatarCircle));
            this.RaisePropertyChanged(nameof(Thickness));
            this.RaisePropertyChanged(nameof(ThicknessText));
            this.RaisePropertyChanged(nameof(AvatarBitmap));
            this.RaisePropertyChanged(nameof(FallbackIcon));
            this.RaisePropertyChanged(nameof(IsCollective));
            this.RaisePropertyChanged(nameof(ApplyRingToAllCommand));
            this.RaisePropertyChanged(nameof(Labels));
            this.RaisePropertyChanged(nameof(HasLabels));
            this.RaisePropertyChanged(nameof(CanEditLabels));
            this.RaisePropertyChanged(nameof(IsDead));
            this.RaisePropertyChanged(nameof(CanMarkDead));
            this.RaisePropertyChanged(nameof(ImportanceMark));
            this.RaisePropertyChanged(nameof(ImportanceLevel));
            this.RaisePropertyChanged(nameof(CanChooseAvatar));
            this.RaisePropertyChanged(nameof(DropTarget));
            this.RaisePropertyChanged(nameof(QuickAvatars));

            // Ленту перечитываем на каждой смене выделения: недавние меняются
            // от каждой поставленной аватарки, в том числе поставленной не
            // отсюда — броском на карточку или из окна выбора.
            QuickAvatars.Reload(_owner.AvatarService);

            // Подсказки меток — из общего реестра проекта плюс метки самой
            // карточки: удалил по ошибке — вернёшь вводом имени.
            ReloadKnownLabels();
        }

        // ── Имя ────────────────────────────────────────────────────────────
        //
        // Имя правится только у одной карточки: общего имени у нескольких нет.
        // Пока печатают, меняется подпись на карточке; в проект имя уходит по
        // CommitName — когда поле теряет фокус или нажат Enter. Так на каждую
        // правку имени приходится один шаг истории, а не один на букву.

        public string Name
        {
            get => Primary?.Name ?? string.Empty;
            set
            {
                var target = Primary;
                if (target is null || _owner.SelectedCards.Count != 1) return;
                if (target.Name == value) return;
                target.Name = value;
                this.RaisePropertyChanged();
            }
        }

        public void CommitName()
        {
            var target = Primary;
            if (target is null || _owner.SelectedCards.Count != 1) return;

            var resolved = string.IsNullOrWhiteSpace(target.Name) ? target.Name : target.Name.Trim();
            target.OnConfirmName?.Invoke(target.Id, resolved);
        }

        // ── Цвет ───────────────────────────────────────────────────────────
        //
        // Цветопикер пишет сюда через двустороннюю привязку. Значение уходит
        // всем выбранным карточкам, и каждая сама кладёт свой шаг в историю:
        // отменять правку десяти карточек по одной честнее, чем одним махом,
        // потому что выделение к моменту отмены может быть уже другим.

        public string Color
        {
            get => Primary?.Color ?? "#455A64";
            set
            {
                if (string.IsNullOrEmpty(value)) return;

                // Правка нескольких карточек — один шаг истории, а не по шагу
                // на карточку: человек сделал одно действие и отменять обязан
                // тоже одно.
                using (_owner.BeginUndoBatch("цвет выбранных карточек"))
                {
                    foreach (var card in Targets.ToList())
                        card.Color = value;
                }

                this.RaisePropertyChanged();
            }
        }

        // Цветопикер показывает внутри себя превью карточки — ему нужны
        // картинка, имя и признак группы первой выбранной.
        public Avalonia.Media.Imaging.Bitmap? AvatarBitmap => Primary?.AvatarBitmap;
        public string FallbackIcon => Primary?.FallbackIcon ?? string.Empty;
        public bool IsCollective => Primary?.IsCollective ?? false;

        public ReactiveCommand<bool, Unit>? ApplyRingToAllCommand => Primary?.ApplyRingToAllCommand;

        // ── Кольцо у всех персонажей проекта ───────────────────────────────

        private bool _isConfirmingRingAll;

        /// <summary>Строка кольца раскрыта в вопрос «включить / убрать у всех».</summary>
        public bool IsConfirmingRingAll
        {
            get => _isConfirmingRingAll;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isConfirmingRingAll, value);
                this.RaisePropertyChanged(nameof(IsNotConfirmingRingAll));
                this.RaisePropertyChanged(nameof(RingAllQuestion));
            }
        }

        public bool IsNotConfirmingRingAll => !_isConfirmingRingAll;

        public ReactiveCommand<Unit, Unit> RequestRingAllCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelRingAllCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmRingAllCommand { get; }

        /// <summary>Вопрос перед раздачей: называет, что именно произойдёт.</summary>
        public string RingAllQuestion => Ring
            ? "Включить кольцо у всех персонажей проекта?"
            : "Убрать кольцо у всех персонажей проекта?";

        /// <summary>
        /// Поставить или снять кольцо у всех карточек проекта. Обход и запись
        /// лежат на списке (ApplyRingToAllCharacters): там же снимок прежних
        /// значений и один шаг в истории отмены на всё действие.
        /// </summary>
        private void ApplyRingToEveryone(bool on)
        {
            IsConfirmingRingAll = false;

            var target = Primary;
            if (target is null) return;

            target.ApplyRingToAllCommand.Execute(on).Subscribe();
            this.RaisePropertyChanged(nameof(Ring));
            this.RaisePropertyChanged(nameof(RingAllQuestion));
        }

        // ── Кольцо, закладка, вид аватара ──────────────────────────────────

        public bool Ring
        {
            get => Primary?.AvatarRing ?? false;
            set
            {
                using (_owner.BeginUndoBatch("кольцо у выбранных карточек"))
                {
                    foreach (var card in Targets.ToList())
                        card.AvatarRing = value;
                }

                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(RingAllQuestion));
            }
        }

        public bool Bookmark
        {
            get => Primary?.GroupBookmark ?? false;
            set
            {
                using (_owner.BeginUndoBatch("закладка у выбранных карточек"))
                {
                    foreach (var card in Targets.ToList())
                        card.GroupBookmark = value;
                }

                this.RaisePropertyChanged();
            }
        }

        public bool AvatarStrip
        {
            get => Primary?.AvatarStrip ?? false;
            set
            {
                using (_owner.BeginUndoBatch("вид аватара у выбранных карточек"))
                {
                    foreach (var card in Targets.ToList())
                        card.AvatarStrip = value;
                }

                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(AvatarCircle));
                this.RaisePropertyChanged(nameof(ShowRingRow));
            }
        }

        /// <summary>Обратное к AvatarStrip — для подсветки кнопки «Кружок».</summary>
        public bool AvatarCircle => !AvatarStrip;

        public ReactiveCommand<Unit, Unit> SetAvatarCircleCommand { get; }
        public ReactiveCommand<Unit, Unit> SetAvatarStripCommand { get; }
        public ReactiveCommand<Unit, Unit> CloseCommand { get; }
        public ReactiveCommand<Unit, Unit> CycleImportanceCommand { get; }
        public ReactiveCommand<Unit, Unit> ChooseAvatarCommand { get; }

        // ── Аватарка ───────────────────────────────────────────────────────
        //
        // Кнопка на карточке больше аватарку не меняет: щелчок по карточке
        // теперь выбирает её, а не открывает окно. Меняют аватарку отсюда, и
        // открывается то же самое окно выбора со всеми недавними, папками,
        // загрузкой файла и обрезкой — второго такого окна заводить незачем.

        public bool CanChooseAvatar => IsSingle;

        /// <summary>
        /// Быстрая лента: папки снизу, картинки сверху, щелчок ставит аватарку
        /// немедленно. Полное окно выбора остаётся для того, что ленте не по
        /// силам, — загрузки файла, обрезки, правки папок.
        /// </summary>
        public CharacterQuickAvatarsViewModel QuickAvatars { get; }

        /// <summary>
        /// Поставить аватарку всем выбранным карточкам. Кадр берётся из самой
        /// ссылки: у картинки из папки его нет, и она встаёт целиком, а у
        /// недавней он уже выбран прошлым разом.
        /// </summary>
        private void PickQuickAvatar(string avatarRef)
        {
            if (string.IsNullOrEmpty(avatarRef)) return;

            using (_owner.BeginUndoBatch("аватарка выбранных карточек"))
            {
                foreach (var card in Targets.ToList())
                    card.ApplyAvatarRef(avatarRef);
            }

            this.RaisePropertyChanged(nameof(AvatarBitmap));

            // Список недавних только что пополнился — лента обязана это
            // показать, иначе только что поставленной картинки в ней не будет.
            QuickAvatars.Reload(_owner.AvatarService);
        }

        // ── Ступень важности ───────────────────────────────────────────────

        /// <summary>Римская цифра ступени первой выбранной карточки.</summary>
        public string ImportanceMark => Primary?.ImportanceMark ?? "II";

        /// <summary>
        /// Сама ступень — её читает конвертер прозрачности, тот же, что и у
        /// значка важности папки: третья ступень бледнее первой.
        /// </summary>
        public Models.Enums.CharacterImportanceLevel ImportanceLevel =>
            Primary?.ImportanceLevel ?? Models.Enums.CharacterImportanceLevel.Secondary;

        /// <summary>
        /// Карточка, на которую уйдёт брошенная в панель картинка. Панель
        /// принимает файл так же, как сама карточка: тянуть его к маленькому
        /// кружку аватарки неудобно, а мимо панели промахнуться трудно.
        /// </summary>
        public CharacterListItemViewModel? DropTarget => Primary;

        // ── Толщина рамки ──────────────────────────────────────────────────

        // Признак того, что идёт жест: значение уже показано карточками, но в
        // проект ещё не записано.
        private bool _thicknessDrag;

        public double Thickness
        {
            get => Primary?.FrameThickness ?? 2.0;
            set
            {
                foreach (var card in Targets.ToList())
                    card.SetFrameThicknessPreview(value);

                _thicknessDrag = true;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ThicknessText));
            }
        }

        public string ThicknessText => Thickness.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Записать показанную толщину. Зовётся, когда ползунок отпустили:
        /// один вызов на жест — одна запись в проект и один шаг в истории.
        /// </summary>
        public void CommitThickness()
        {
            if (!_thicknessDrag) return;
            _thicknessDrag = false;

            using (_owner.BeginUndoBatch("толщина рамки выбранных карточек"))
            {
                foreach (var card in Targets.ToList())
                    card.CommitFrameThickness();
            }
        }

        // ── Метки ──────────────────────────────────────────────────────────
        //
        // Показываются от первой выбранной карточки и правятся только у неё:
        // общего набора меток у нескольких персонажей нет, как и у имени.
        //
        // Список — все метки персонажа (AllLabels), а не только те, что видны
        // на карточке (CardLabels): здесь их правят, а не просто показывают,
        // и скрытая от карточки метка не должна из-за этого стать недоступна.
        //
        // Правка идёт тем же редактором, что и на вкладке «Основное» —
        // LabelEditorOverlay, хостится в CharactersModuleView. Чип зовёт его
        // на правку существующей метки, кнопка «+» — на создание новой; сама
        // панель только знает, куда деть результат (UpsertLabel/RemoveLabel).
        // Запись немедленная, с шагом в историю — как и всё остальное здесь,
        // через OnLabelsChanged персонажа и ChangeLabelsCommand.

        public IReadOnlyList<CharacterLabel> Labels =>
            Primary?.AllLabels ?? Array.Empty<CharacterLabel>();

        public bool HasLabels => Labels.Count > 0;

        /// <summary>Метки правятся только у одной карточки — как и имя.</summary>
        public bool CanEditLabels => IsSingle;

        /// <summary>
        /// Встроенная метка «Мёртв»: кнопка-ярлык добавляет/убирает её тем же
        /// путём, что и обычную метку (UpsertLabel/RemoveLabel), — отдельного
        /// органа состояния нет, чип «Мёртв» и есть источник истины.
        /// </summary>
        public bool IsDead
        {
            get => Labels.Any(l => l.Id == CharacterBuiltinLabels.DeadId);
            set
            {
                if (!CanEditLabels) return;
                var existing = Labels.FirstOrDefault(l => l.Id == CharacterBuiltinLabels.DeadId);
                if (value && existing == null)
                    UpsertLabel(CharacterBuiltinLabels.CreateDead(CharactersStrings.Label_Dead), applyToAll: false);
                else if (!value && existing != null)
                    RemoveLabel(existing.Id);
            }
        }

        /// <summary>Кнопка-ярлык «Мёртв» показывается только там, где вообще
        /// можно править метки (одна выбранная карточка) и метка ещё не
        /// стоит.</summary>
        public bool CanMarkDead => CanEditLabels && !IsDead;

        /// <summary>
        /// Подсказки для быстрого добавления метки по имени: все метки
        /// проекта плюс метки самой карточки (удалил по ошибке — вернёшь
        /// вводом имени, а не заведением похожей).
        /// </summary>
        public ObservableCollection<string> KnownLabelNames { get; } = new();

        public void ReloadKnownLabels()
        {
            var known = _owner.CharacterService.GetAllLabels()
                .Select(l => l.Name)
                .Concat(Labels.Select(l => l.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            KnownLabelNames.Clear();
            foreach (var name in known) KnownLabelNames.Add(name);
        }

        /// <summary>Добавить метку по введённому имени — используется полем
        /// быстрого ввода со списком подсказок (см. AddLabelCommand в
        /// конструкторе).</summary>
        public ReactiveCommand<string, Unit> AddLabelCommand { get; }

        /// <summary>
        /// Применить метку из редактора: правка существующей (Id сохраняется,
        /// порядок остаётся её собственным) или создание новой (добавляется
        /// в конец). applyToAll — сделать вид общим для всех персонажей с
        /// этой же меткой, как в полном редакторе персонажа.
        /// </summary>
        public void UpsertLabel(CharacterLabel label, bool applyToAll)
        {
            var target = Primary;
            if (target is null || !IsSingle || label is null) return;

            var current = target.AllLabels.ToList();
            var index = current.FindIndex(l => l.Id == label.Id);
            if (index >= 0) current[index] = label;
            else current.Add(label);

            var renumbered = Renumbered(current);

            // Тот же реестр меток проекта, что и у полного редактора: метка,
            // которой там ещё нет, вносится в любом случае — иначе её не
            // предложат другому персонажу. Личная правка уже known-метки в
            // реестр не уходит без явного applyToAll.
            var isKnown = _owner.CharacterService.GetAllLabels().Any(l => l.Id == label.Id);
            if (!isKnown)
            {
                _owner.CharacterService.SaveGlobalLabel(label);
            }
            else if (applyToAll)
            {
                _owner.CharacterService.SaveGlobalLabel(label);
                _owner.CharacterService.ApplyLabelToAll(label);

                // Applied ко всем персонажам проекта, не только к текущему —
                // карточки в списке обязаны это подхватить, иначе показывали
                // бы прежний вид метки до перезагрузки проекта.
                _owner.RefreshLabelsFromModel();
            }

            target.OnLabelsChanged?.Invoke(target.Id, renumbered);
            this.RaisePropertyChanged(nameof(Labels));
            this.RaisePropertyChanged(nameof(HasLabels));
            this.RaisePropertyChanged(nameof(IsDead));
            this.RaisePropertyChanged(nameof(CanMarkDead));
        }

        /// <summary>Убрать метку у текущего персонажа. Из реестра проекта
        /// метка не удаляется — она остаётся доступна для других.</summary>
        public void RemoveLabel(string labelId)
        {
            var target = Primary;
            if (target is null || !IsSingle || string.IsNullOrEmpty(labelId)) return;

            var current = target.AllLabels.Where(l => l.Id != labelId).ToList();
            var renumbered = Renumbered(current);

            target.OnLabelsChanged?.Invoke(target.Id, renumbered);
            this.RaisePropertyChanged(nameof(Labels));
            this.RaisePropertyChanged(nameof(HasLabels));
            this.RaisePropertyChanged(nameof(IsDead));
            this.RaisePropertyChanged(nameof(CanMarkDead));
        }

        /// <summary>
        /// Пересчитать Order по позиции в списке — копиями, а не правкой
        /// объектов на месте. Те же экземпляры могут лежать в снимке более
        /// раннего шага истории (ChangeLabelsCommand хранит списки меток
        /// целиком), и правка Order на месте задним числом попортила бы
        /// прежний снимок — Ctrl+Z вернул бы не тот порядок, что был.
        /// </summary>
        private static List<CharacterLabel> Renumbered(List<CharacterLabel> labels)
        {
            var result = new List<CharacterLabel>(labels.Count);
            for (var i = 0; i < labels.Count; i++)
                result.Add(CloneWithOrder(labels[i], i));
            return result;
        }

        private static CharacterLabel CloneWithOrder(CharacterLabel source, int order) => new()
        {
            Id = source.Id,
            Name = source.Name,
            Icon = source.Icon,
            IconImage = source.IconImage,
            Color = source.Color,
            IconColor = source.IconColor,
            ShowBackdrop = source.ShowBackdrop,
            Effect = source.Effect,
            ShowOnCard = source.ShowOnCard,
            Order = order,
            Description = source.Description
        };
    }
}
