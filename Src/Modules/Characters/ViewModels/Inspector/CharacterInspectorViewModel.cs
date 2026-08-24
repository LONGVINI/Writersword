using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Models;

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

            CloseCommand = ReactiveCommand.Create(() => { _owner.ClearSelection(); });

            CycleImportanceCommand = ReactiveCommand.Create(() =>
            {
                foreach (var card in Targets.ToList())
                    card.CycleImportance();
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
            this.RaisePropertyChanged(nameof(ImportanceMark));
            this.RaisePropertyChanged(nameof(ImportanceLevel));
            this.RaisePropertyChanged(nameof(CanChooseAvatar));
            this.RaisePropertyChanged(nameof(DropTarget));
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
                foreach (var card in Targets.ToList())
                    card.Color = value;
                this.RaisePropertyChanged();
            }
        }

        // Цветопикер показывает внутри себя превью карточки — ему нужны
        // картинка, имя и признак группы первой выбранной.
        public Avalonia.Media.Imaging.Bitmap? AvatarBitmap => Primary?.AvatarBitmap;
        public string FallbackIcon => Primary?.FallbackIcon ?? string.Empty;
        public bool IsCollective => Primary?.IsCollective ?? false;

        public ReactiveCommand<bool, Unit>? ApplyRingToAllCommand => Primary?.ApplyRingToAllCommand;

        // ── Кольцо, закладка, вид аватара ──────────────────────────────────

        public bool Ring
        {
            get => Primary?.AvatarRing ?? false;
            set
            {
                foreach (var card in Targets.ToList())
                    card.AvatarRing = value;
                this.RaisePropertyChanged();
            }
        }

        public bool Bookmark
        {
            get => Primary?.GroupBookmark ?? false;
            set
            {
                foreach (var card in Targets.ToList())
                    card.GroupBookmark = value;
                this.RaisePropertyChanged();
            }
        }

        public bool AvatarStrip
        {
            get => Primary?.AvatarStrip ?? false;
            set
            {
                foreach (var card in Targets.ToList())
                    card.AvatarStrip = value;

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

            foreach (var card in Targets.ToList())
                card.CommitFrameThickness();
        }

        // ── Метки ──────────────────────────────────────────────────────────
        //
        // Метки показываются от первой выбранной: это снимок для чтения, а не
        // редактор. Сам редактор остаётся отдельным окном — он про определения
        // меток целиком, а не про одного персонажа.

        public IReadOnlyList<CharacterLabel> Labels =>
            Primary?.CardLabels ?? Array.Empty<CharacterLabel>();

        public bool HasLabels => Labels.Count > 0;
    }
}
