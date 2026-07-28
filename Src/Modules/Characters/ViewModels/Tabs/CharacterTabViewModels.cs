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
    // ── Вкладка Параметры ────────────────────────────────────────────────

    public class CharacterParametersTabViewModel : ReactiveObject
    {
        /// <summary>
        /// Параметры в обёртках для интерфейса. Модели достаются через
        /// GetParameters при сохранении.
        /// </summary>
        public ObservableCollection<CharacterParameterItemViewModel> Parameters { get; } = new();

        // Списка наборов здесь больше нет: состав карточки задаётся в «Общем»,
        // здесь живут значения полей. Держать второй вход к тому же действию
        // значит позволить составу разъехаться между двумя экранами.

        /// <summary>
        /// Любое изменение параметра или их набора. Карточка подписывается и
        /// сохраняет персонажа — до этого правки параметров не сохранялись
        /// вообще, потому что Save применял только вкладку Basics.
        /// Имя не «Changed»: такой член уже есть у ReactiveObject.
        /// </summary>
        public event Action? Edited;

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

            foreach (var p in character.Parameters) Parameters.Add(Wrap(p));

            Parameters.CollectionChanged += (_, _) => Edited?.Invoke();

            AddNumericParameterCommand = ReactiveCommand.Create(() => AddParameter(CharacterParameterType.Numeric));
            AddTextParameterCommand = ReactiveCommand.Create(() => AddParameter(CharacterParameterType.Text));
            AddStateListParameterCommand = ReactiveCommand.Create(() => AddParameter(CharacterParameterType.StateList));
            AddBooleanParameterCommand = ReactiveCommand.Create(() => AddParameter(CharacterParameterType.Boolean));
            RemoveParameterCommand = ReactiveCommand.Create<string>(id =>
            {
                var p = Parameters.FirstOrDefault(x => x.Id == id);
                if (p != null) Parameters.Remove(p);
            });
            MoveParameterUpCommand = ReactiveCommand.Create<string>(id =>
            {
                var item = Parameters.FirstOrDefault(x => x.Id == id);
                if (item == null) return;
                var idx = Parameters.IndexOf(item);
                if (idx > 0) Parameters.Move(idx, idx - 1);
            });
            RandomizeAllCommand = ReactiveCommand.Create(RandomizeAll);
            ApplyAnketaCommand = ReactiveCommand.Create<string>(ApplyAnketa);
        }

        private CharacterParameterItemViewModel Wrap(CharacterParameter p)
        {
            var item = new CharacterParameterItemViewModel(p);
            item.Edited += () => Edited?.Invoke();
            return item;
        }

        private void AddParameter(CharacterParameterType type)
        {
            var parameter = new CharacterParameter
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Новый параметр",
                Type = type,
                Order = Parameters.Count
            };

            // Короткая шкала по умолчанию: «3 из 5» читается сразу, тогда как
            // значение на диапазоне 0..100 не говорит ничего без объяснений.
            if (type == CharacterParameterType.Numeric)
            {
                parameter.MinValue = 0;
                parameter.MaxValue = 5;
                parameter.Step = 1;
            }

            Parameters.Add(Wrap(parameter));
        }

        private void RandomizeAll()
        {
            var list = Parameters.Select(i => i.Model).ToList();
            _anketaService.RandomizeParameters(list);
            Reload(list);
        }

        /// <summary>
        /// Подключить набор к карточке. Состав задаётся в «Общем», но команда
        /// остаётся точкой входа для горячих клавиш и внешних вызовов —
        /// действие одно и то же, и вести его должен один метод.
        /// </summary>
        private void ApplyAnketa(string anketaId)
        {
            var anketa = _anketaService.GetById(anketaId);
            if (anketa == null) return;
            _characterService.ApplyAnketa(_characterId, anketa, false);
            var updated = _characterService.GetById(_characterId);
            if (updated == null) return;
            Reload(updated.Parameters);
        }

        private void Reload(IEnumerable<CharacterParameter> source)
        {
            Parameters.Clear();
            foreach (var p in source) Parameters.Add(Wrap(p));
        }

        /// <summary>
        /// Пересобрать вкладку из модели. Вызывается карточкой, когда состав
        /// наборов изменили в «Общем»: поля живут здесь, а состав задаётся там.
        /// </summary>
        public void ReloadFromModel(Character character)
        {
            Reload(character.Parameters);
        }

        public List<CharacterParameter> GetParameters() => Parameters.Select(i => i.Model).ToList();
    }

    // ── Вкладка Связи ────────────────────────────────────────────────────

    /// <summary>
    /// Связь как узел эго-графа: и данные отношения, и его положение на
    /// полотне. Форма с выпадающими списками заменена полотном — связи
    /// воспринимаются глазом, а не читаются построчно.
    /// </summary>
    public class CharacterRelationshipItemViewModel : ReactiveObject
    {
        /// <summary>Диаметр узла на полотне.</summary>
        public const double NodeSize = 46.0;

        /// <summary>
        /// Вызывается при изменении, которое нужно сохранить. Имя не «Changed»:
        /// такой член уже есть у ReactiveObject.
        /// </summary>
        public event Action? Edited;

        public string RelationshipId { get; }

        /// <summary>
        /// Источник связи. Раньше в ToModel не переносился, и сохранение
        /// отношения обнулило бы его владельца.
        /// </summary>
        public string SourceCharacterId { get; }

        public string TargetCharacterId { get; }
        public string TargetName { get; set; } = string.Empty;
        public string TargetColor { get; set; } = "#607D8B";
        public string TargetIcon { get; set; } = "?";

        private string _relationshipType = string.Empty;
        public string RelationshipType
        {
            get => _relationshipType;
            set { this.RaiseAndSetIfChanged(ref _relationshipType, value); this.RaisePropertyChanged(nameof(EdgeCaption)); Edited?.Invoke(); }
        }

        private CharacterRelationshipContext _context;
        public CharacterRelationshipContext Context
        {
            get => _context;
            set { this.RaiseAndSetIfChanged(ref _context, value); Edited?.Invoke(); }
        }

        private CharacterRelationshipEmotion _emotion;
        public CharacterRelationshipEmotion Emotion
        {
            get => _emotion;
            set
            {
                this.RaiseAndSetIfChanged(ref _emotion, value);
                this.RaisePropertyChanged(nameof(EdgeColor));
                this.RaisePropertyChanged(nameof(IsNeutral));
                this.RaisePropertyChanged(nameof(IsPositive));
                this.RaisePropertyChanged(nameof(IsNegative));
                this.RaisePropertyChanged(nameof(IsAmbivalent));
                Edited?.Invoke();
            }
        }

        private double _strength = 0.5;
        public double Strength
        {
            get => _strength;
            set
            {
                this.RaiseAndSetIfChanged(ref _strength, value);
                this.RaisePropertyChanged(nameof(EdgeThickness));
                this.RaisePropertyChanged(nameof(StrengthLevel));
                RaiseStrengthFlags();
                Edited?.Invoke();
            }
        }

        /// <summary>Сила в кругляшках 1..5 — как шкалы параметров.</summary>
        public int StrengthLevel
        {
            get => Math.Max(1, Math.Min(5, (int)Math.Round(_strength * 5.0)));
            set => Strength = Math.Max(1, Math.Min(5, value)) / 5.0;
        }

        // Заполненность каждого кругляшка силы. Отдельные свойства, чтобы
        // не заводить конвертер сравнения на каждый уровень.
        public bool HasStrength1 => StrengthLevel >= 1;
        public bool HasStrength2 => StrengthLevel >= 2;
        public bool HasStrength3 => StrengthLevel >= 3;
        public bool HasStrength4 => StrengthLevel >= 4;
        public bool HasStrength5 => StrengthLevel >= 5;

        private void RaiseStrengthFlags()
        {
            this.RaisePropertyChanged(nameof(HasStrength1));
            this.RaisePropertyChanged(nameof(HasStrength2));
            this.RaisePropertyChanged(nameof(HasStrength3));
            this.RaisePropertyChanged(nameof(HasStrength4));
            this.RaisePropertyChanged(nameof(HasStrength5));
        }

        private bool _isBidirectional = true;
        public bool IsBidirectional
        {
            get => _isBidirectional;
            set { this.RaiseAndSetIfChanged(ref _isBidirectional, value); Edited?.Invoke(); }
        }

        private string _note = string.Empty;
        public string Note
        {
            get => _note;
            set { this.RaiseAndSetIfChanged(ref _note, value); Edited?.Invoke(); }
        }

        /// <summary>
        /// Как источник называет цель: «мама зовёт Андрюшей». Данные лежали
        /// в модели с самого начала, интерфейса к ним не было.
        /// </summary>
        public ObservableCollection<CharacterAddressForm> SourceCallsTargetAs { get; } = new();

        /// <summary>Сообщить о правке списка имён — коллекция сама не поднимает
        /// событие сохранения.</summary>
        public void NotifyCallsAsChanged() => Edited?.Invoke();

        /// <summary>
        /// Добавить обращение. Повод можно ввести в той же строке через тире:
        /// «Алинусик — нежно». Отдельного редактора нет намеренно — ввод
        /// остаётся потоковым, как у имён персонажа.
        /// </summary>
        public void AddAddressForm(string input)
        {
            var trimmed = input?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;

            var (value, occasion) = SplitValueAndOccasion(trimmed);
            if (string.IsNullOrEmpty(value)) return;

            if (SourceCallsTargetAs.Any(f =>
                string.Equals(f.Value, value, StringComparison.CurrentCultureIgnoreCase) &&
                string.Equals(f.Occasion, occasion, StringComparison.CurrentCultureIgnoreCase)))
                return;

            SourceCallsTargetAs.Add(new CharacterAddressForm { Value = value, Occasion = occasion });
            Edited?.Invoke();
        }

        public void RemoveAddressForm(string id)
        {
            var form = SourceCallsTargetAs.FirstOrDefault(f => f.Id == id);
            if (form == null) return;

            SourceCallsTargetAs.Remove(form);
            Edited?.Invoke();
        }

        // Разделителем считается тире с пробелами по краям: дефис внутри слова
        // остаётся частью обращения.
        private static (string Value, string Occasion) SplitValueAndOccasion(string input)
        {
            var separators = new[] { " — ", " – ", " - " };

            foreach (var separator in separators)
            {
                var index = input.IndexOf(separator, StringComparison.Ordinal);
                if (index <= 0) continue;

                return (input.Substring(0, index).Trim(),
                        input.Substring(index + separator.Length).Trim());
            }

            return (input, string.Empty);
        }

        // ── Положение на полотне ─────────────────────────────────────────

        private double _x, _y;
        public double X
        {
            get => _x;
            set { this.RaiseAndSetIfChanged(ref _x, value); this.RaisePropertyChanged(nameof(EdgeEnd)); this.RaisePropertyChanged(nameof(LabelX)); }
        }
        public double Y
        {
            get => _y;
            set { this.RaiseAndSetIfChanged(ref _y, value); this.RaisePropertyChanged(nameof(EdgeEnd)); this.RaisePropertyChanged(nameof(LabelY)); }
        }

        private Avalonia.Point _edgeStart;
        public Avalonia.Point EdgeStart
        {
            get => _edgeStart;
            set { this.RaiseAndSetIfChanged(ref _edgeStart, value); this.RaisePropertyChanged(nameof(LabelX)); this.RaisePropertyChanged(nameof(LabelY)); }
        }

        public Avalonia.Point EdgeEnd => new(_x + NodeSize / 2.0, _y + NodeSize / 2.0);

        /// <summary>Подпись типа связи сидит на середине ребра.</summary>
        public double LabelX => (_edgeStart.X + EdgeEnd.X) / 2.0 - 60;
        public double LabelY => (_edgeStart.Y + EdgeEnd.Y) / 2.0 - 9;

        public string EdgeCaption => _relationshipType;

        /// <summary>Цвет ребра — эмоция. Толщина — сила.</summary>
        public string EdgeColor => _emotion switch
        {
            CharacterRelationshipEmotion.Positive => "#4CAF50",
            CharacterRelationshipEmotion.Negative => "#F44336",
            CharacterRelationshipEmotion.Ambivalent => "#FF9800",
            _ => "#78909C"
        };

        public double EdgeThickness => 1.5 + _strength * 3.5;

        public bool IsNeutral => _emotion == CharacterRelationshipEmotion.Neutral;
        public bool IsPositive => _emotion == CharacterRelationshipEmotion.Positive;
        public bool IsNegative => _emotion == CharacterRelationshipEmotion.Negative;
        public bool IsAmbivalent => _emotion == CharacterRelationshipEmotion.Ambivalent;

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

        /// <summary>Скрыт фильтром полотна. Данные не трогаются.</summary>
        private bool _isVisible = true;
        public bool IsVisible { get => _isVisible; set => this.RaiseAndSetIfChanged(ref _isVisible, value); }

        public CharacterRelationshipItemViewModel(CharacterRelationship rel, Character? target)
        {
            RelationshipId = rel.Id;
            SourceCharacterId = rel.SourceCharacterId;
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
                TargetIcon = Models.CharacterGlyph.Resolve(target.FallbackIcon, target.Name);
            }

            // Формы — источник истины; у старых связей они собираются из списка
            // строк при загрузке проекта.
            Models.CharacterAddress.Normalize(rel);
            foreach (var form in rel.SourceCallsTargetForms) SourceCallsTargetAs.Add(form);
        }

        public CharacterRelationship ToModel() => new()
        {
            Id = RelationshipId,
            SourceCharacterId = SourceCharacterId,
            TargetCharacterId = TargetCharacterId,
            RelationshipType = RelationshipType,
            Context = Context,
            Emotion = Emotion,
            Strength = Strength,
            IsBidirectional = IsBidirectional,
            Note = Note,
            SourceCallsTargetForms = SourceCallsTargetAs.ToList(),
            // Старый список повторяет значения форм: код, который ещё не знает
            // о поводах, продолжает работать.
            SourceCallsTargetAs = SourceCallsTargetAs.Select(f => f.Value).ToList()
        };
    }

    /// <summary>
    /// Эго-граф связей персонажа: он в центре, вокруг — те, с кем связан.
    /// Полный граф проекта здесь не показывается принципиально (см. п. 3.1
    /// карты развития — на полусотне персонажей он нечитаем).
    /// </summary>
    public class CharacterRelationshipsTabViewModel : ReactiveObject
    {
        private readonly IRelationshipService _relService;
        private readonly ICharacterService _charService;
        private readonly string _characterId;

        // Размеры полотна фиксированы: эго-граф всегда помещается целиком,
        // панорамирование и масштаб ему не нужны — узлов единицы, не сотни.
        public double CanvasWidth => 680;
        public double CanvasHeight => 460;

        public ObservableCollection<CharacterRelationshipItemViewModel> Relationships { get; } = new();

        /// <summary>Персонажи, с которыми связи ещё нет — их можно перетащить.</summary>
        public ObservableCollection<Character> AvailableCharacters { get; } = new();

        public ReactiveCommand<Unit, Unit> AddRelationshipCommand { get; }
        public ReactiveCommand<string, Unit> RemoveRelationshipCommand { get; }

        // ── Центральный узел ─────────────────────────────────────────────

        public double CenterNodeSize => 62;
        public double CenterX => CanvasWidth / 2.0 - CenterNodeSize / 2.0;
        public double CenterY => CanvasHeight / 2.0 - CenterNodeSize / 2.0;

        private string _centerName = string.Empty;
        public string CenterName { get => _centerName; private set => this.RaiseAndSetIfChanged(ref _centerName, value); }

        private string _centerColor = "#607D8B";
        public string CenterColor { get => _centerColor; private set => this.RaiseAndSetIfChanged(ref _centerColor, value); }

        private string _centerIcon = "?";
        public string CenterIcon { get => _centerIcon; private set => this.RaiseAndSetIfChanged(ref _centerIcon, value); }

        // ── Выбранная связь ──────────────────────────────────────────────

        private CharacterRelationshipItemViewModel? _selected;
        public CharacterRelationshipItemViewModel? Selected
        {
            get => _selected;
            private set
            {
                if (_selected != null) _selected.IsSelected = false;
                this.RaiseAndSetIfChanged(ref _selected, value);
                if (_selected != null) _selected.IsSelected = true;
                this.RaisePropertyChanged(nameof(HasSelection));
            }
        }

        public bool HasSelection => _selected != null;

        public void Select(CharacterRelationshipItemViewModel? item) => Selected = item;

        // ── Фильтры полотна ──────────────────────────────────────────────
        // Контекст и эмоция — не поля формы, а способ посмотреть на связи
        // под нужным углом: «только вражда», «только то, что было в детстве».

        private CharacterRelationshipEmotion? _filterEmotion;
        public CharacterRelationshipEmotion? FilterEmotion
        {
            get => _filterEmotion;
            set { this.RaiseAndSetIfChanged(ref _filterEmotion, value); ApplyFilters(); RaiseFilterFlags(); }
        }

        private CharacterRelationshipContext? _filterContext;
        public CharacterRelationshipContext? FilterContext
        {
            get => _filterContext;
            set { this.RaiseAndSetIfChanged(ref _filterContext, value); ApplyFilters(); RaiseFilterFlags(); }
        }

        public bool IsFilterAll => _filterEmotion == null && _filterContext == null;
        public bool IsFilterPositive => _filterEmotion == CharacterRelationshipEmotion.Positive;
        public bool IsFilterNegative => _filterEmotion == CharacterRelationshipEmotion.Negative;

        private void RaiseFilterFlags()
        {
            this.RaisePropertyChanged(nameof(IsFilterAll));
            this.RaisePropertyChanged(nameof(IsFilterPositive));
            this.RaisePropertyChanged(nameof(IsFilterNegative));
        }

        public void ResetFilters()
        {
            _filterEmotion = null;
            _filterContext = null;
            this.RaisePropertyChanged(nameof(FilterEmotion));
            this.RaisePropertyChanged(nameof(FilterContext));
            ApplyFilters();
            RaiseFilterFlags();
        }

        private void ApplyFilters()
        {
            foreach (var item in Relationships)
            {
                var byEmotion = _filterEmotion == null || item.Emotion == _filterEmotion;
                var byContext = _filterContext == null || item.Context == _filterContext;
                item.IsVisible = byEmotion && byContext;
            }
        }

        public bool HasNoRelationships => Relationships.Count == 0;

        public CharacterRelationshipsTabViewModel(IRelationshipService rs, ICharacterService cs, string characterId)
        {
            _relService = rs;
            _charService = cs;
            _characterId = characterId;

            Refresh();

            AddRelationshipCommand = ReactiveCommand.Create(AddFirstAvailable);
            RemoveRelationshipCommand = ReactiveCommand.Create<string>(Remove);
        }

        public void Refresh()
        {
            var self = _charService.GetById(_characterId);
            if (self != null)
            {
                CenterName = self.Name;
                CenterColor = self.Color;
                CenterIcon = Models.CharacterGlyph.Resolve(self.FallbackIcon, self.Name);
            }

            Relationships.Clear();
            foreach (var rel in _relService.GetOutgoing(_characterId))
            {
                var target = _charService.GetById(rel.TargetCharacterId);
                Relationships.Add(Wrap(rel, target));
            }

            RefreshAvailable();
            Layout();
            ApplyFilters();
            this.RaisePropertyChanged(nameof(HasNoRelationships));
        }

        private CharacterRelationshipItemViewModel Wrap(CharacterRelationship rel, Character? target)
        {
            var item = new CharacterRelationshipItemViewModel(rel, target);

            // Правки отношения сохраняются сразу. Раньше Update не вызывался
            // нигде — тип, эмоция, сила и заметка терялись при перезагрузке.
            item.Edited += () => _relService.Update(item.ToModel());
            return item;
        }

        private void RefreshAvailable()
        {
            var linked = Relationships.Select(r => r.TargetCharacterId).ToHashSet();

            AvailableCharacters.Clear();
            foreach (var c in _charService.GetAll())
                if (c.Id != _characterId && !linked.Contains(c.Id))
                    AvailableCharacters.Add(c);
        }

        /// <summary>
        /// Раскладка по кругу вокруг центра. Радиус растёт с числом связей,
        /// чтобы узлы не наезжали друг на друга.
        /// </summary>
        private void Layout()
        {
            var count = Relationships.Count;
            if (count == 0) return;

            var cx = CanvasWidth / 2.0;
            var cy = CanvasHeight / 2.0;
            var radius = Math.Min(190.0, Math.Max(110.0, 26.0 * count));

            for (int i = 0; i < count; i++)
            {
                var angle = -Math.PI / 2.0 + i * (2 * Math.PI / count);
                var item = Relationships[i];
                item.EdgeStart = new Avalonia.Point(cx, cy);
                item.X = cx + Math.Cos(angle) * radius - CharacterRelationshipItemViewModel.NodeSize / 2.0;
                item.Y = cy + Math.Sin(angle) * radius - CharacterRelationshipItemViewModel.NodeSize / 2.0;
            }
        }

        /// <summary>
        /// Создать связь с персонажем — вызывается при отпускании перетащенного
        /// персонажа на полотно и по двойному клику в списке кандидатов.
        /// </summary>
        public void AddRelationshipTo(string targetCharacterId)
        {
            if (string.IsNullOrEmpty(targetCharacterId)) return;
            if (targetCharacterId == _characterId) return;
            if (Relationships.Any(r => r.TargetCharacterId == targetCharacterId)) return;

            var target = _charService.GetById(targetCharacterId);
            if (target == null) return;

            var rel = _relService.Create(_characterId, targetCharacterId);
            var item = Wrap(rel, target);
            Relationships.Add(item);

            RefreshAvailable();
            Layout();
            ApplyFilters();
            this.RaisePropertyChanged(nameof(HasNoRelationships));

            // Новая связь сразу выбрана: пользователь тянул персонажа, чтобы
            // описать отношение, а не чтобы полюбоваться кружком.
            Selected = item;
        }

        private void AddFirstAvailable()
        {
            var first = AvailableCharacters.FirstOrDefault();
            if (first != null) AddRelationshipTo(first.Id);
        }

        private void Remove(string relationshipId)
        {
            var item = Relationships.FirstOrDefault(r => r.RelationshipId == relationshipId);
            if (item == null) return;

            if (Selected == item) Selected = null;

            _relService.Delete(relationshipId);
            Relationships.Remove(item);

            RefreshAvailable();
            Layout();
            ApplyFilters();
            this.RaisePropertyChanged(nameof(HasNoRelationships));
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