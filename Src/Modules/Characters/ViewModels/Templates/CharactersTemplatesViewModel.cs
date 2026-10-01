using Avalonia.Media;
using Avalonia.Media.Imaging;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Controls;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Anketas;

namespace Writersword.Modules.Characters.ViewModels.Templates
{
    /// <summary>
    /// Шаблоны — наборы анкет под проект, блоками.
    ///
    /// Три уровня, и путать их нельзя: поле спрашивает одну вещь, анкета
    /// собирает поля в раздел, шаблон собирает анкеты под тип истории
    /// («Фэнтези» = «Обычный человек» + «Характер» + «Магия»). Одна анкета
    /// может стоять в нескольких шаблонах.
    ///
    /// У каждого блока значки анкет настоящие: под курсором карандаш —
    /// открыть анкету в редакторе — и крестик — убрать из шаблона; в конце
    /// ряда «+». Показываются первые десять, остальное раскрывается на месте.
    /// Встроенный шаблон неизменен: правка уходит в его свою копию.
    ///
    /// Один из шаблонов — шаблон проекта: его анкеты получает каждый новый
    /// персонаж. Остальные анкеты добавляются персонажу в карточке, ниже.
    /// </summary>
    public class CharactersTemplatesViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersTemplatesViewModel>();

        /// <summary>Сколько значков видно в свёрнутом блоке.</summary>
        public const int CollapsedLimit = 10;

        private readonly ICharacterAnketaService _anketaService;

        // Анкеты, которые получает каждый новый персонаж. Это и есть шаблон
        // проекта в действии: шаблон проекта задаёт их состав.
        private readonly ObservableCollection<string> _activeTemplateIds;

        private readonly ICharacterService? _characterService;
        private readonly ICharacterAvatarService? _avatarService;

        public ObservableCollection<TemplateBlockViewModel> CustomBlocks { get; } = new();
        public ObservableCollection<TemplateBlockViewModel> BuiltInBlocks { get; } = new();

        private IEnumerable<TemplateBlockViewModel> AllBlocks => CustomBlocks.Concat(BuiltInBlocks);

        public bool HasCustom => CustomBlocks.Count > 0;

        private string _searchQuery = string.Empty;

        /// <summary>Поиск по шаблонам: название, описание и названия их анкет.</summary>
        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (_searchQuery == value) return;
                this.RaiseAndSetIfChanged(ref _searchQuery, value ?? string.Empty);
                ApplySearch();
            }
        }

        private void ApplySearch()
        {
            foreach (var block in AllBlocks) block.ApplySearch(_searchQuery);
        }

        public ReactiveCommand<Unit, Unit> RestartOnboardingCommand { get; }

        public event Action? OnboardingRestartRequested;

        /// <summary>Открыть анкету в редакторе анкет.</summary>
        public event Action<string>? OpenAnketaRequested;

        /// <summary>Анкеты шаблона проекта разнесены по персонажам — карточке пора перечитать поля.</summary>
        public event Action? CharactersUpdated;

        public CharactersTemplatesViewModel(
            ICharacterAnketaService anketaService,
            ObservableCollection<string> activeTemplateIds,
            ICharacterService? characterService = null,
            ICharacterAvatarService? avatarService = null)
        {
            _anketaService = anketaService;
            _activeTemplateIds = activeTemplateIds;
            _characterService = characterService;
            _avatarService = avatarService;

            RestartOnboardingCommand = ReactiveCommand.Create(() => OnboardingRestartRequested?.Invoke());

            _activeTemplateIds.CollectionChanged += (_, _) => RaiseProject();

            Refresh();
        }

        // ── Шаблон проекта ───────────────────────────────────────────────

        private string? _projectTemplateId;

        /// <summary>
        /// Шаблон проекта. Хранится в данных проекта; пусто — шаблон не выбран,
        /// и новые персонажи получают анкеты, отмеченные раньше по одной.
        /// </summary>
        public string? ProjectTemplateId
        {
            get => _projectTemplateId;
            set
            {
                if (_projectTemplateId == value) return;
                this.RaiseAndSetIfChanged(ref _projectTemplateId, value);
                foreach (var block in AllBlocks)
                    block.IsProject = block.Id == value;
                RaiseProject();
            }
        }

        public string ProjectCaption
        {
            get
            {
                var template = _projectTemplateId == null ? null : _anketaService.GetTemplateById(_projectTemplateId);
                if (template != null) return template.Name;
                return _activeTemplateIds.Count > 0 ? "Свой набор анкет" : "Не выбран";
            }
        }

        public string ProjectAnketasCaption
        {
            get
            {
                var names = _activeTemplateIds
                    .Select(id => _anketaService.GetById(id)?.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();
                return names.Count == 0 ? "Без анкет" : string.Join(" · ", names);
            }
        }

        public bool HasProject => _activeTemplateIds.Count > 0;

        private void RaiseProject()
        {
            this.RaisePropertyChanged(nameof(ProjectCaption));
            this.RaisePropertyChanged(nameof(ProjectAnketasCaption));
            this.RaisePropertyChanged(nameof(HasProject));
        }

        /// <summary>
        /// Сделать шаблон шаблоном проекта: каждый новый персонаж будет
        /// получать его анкеты. Уже созданных это не трогает — для них есть
        /// «Применить к созданным».
        /// </summary>
        public void SetProjectTemplate(string templateId)
        {
            var template = _anketaService.GetTemplateById(templateId);
            if (template == null) return;

            ProjectTemplateId = template.Id;
            SyncActiveAnketas(template);
        }

        private void SyncActiveAnketas(CharacterTemplate template)
        {
            _activeTemplateIds.Clear();
            foreach (var id in template.AnketaIds)
                if (_anketaService.GetById(id) != null && !_activeTemplateIds.Contains(id))
                    _activeTemplateIds.Add(id);
        }

        /// <summary>
        /// Подключить анкеты шаблона проекта всем уже созданным персонажам.
        /// Подключённое раньше остаётся на месте, заполненное не трогается.
        /// </summary>
        public int ApplyProjectToAll()
        {
            if (_characterService == null) return 0;

            var anketas = _activeTemplateIds
                .Select(id => _anketaService.GetById(id))
                .Where(a => a != null)
                .Cast<CharacterAnketa>()
                .ToList();

            int touched = 0;
            foreach (var character in _characterService.GetAll().ToList())
            {
                bool changed = false;
                foreach (var anketa in anketas)
                {
                    if (character.AttachedAnketaIds.Contains(anketa.Id)) continue;
                    _characterService.ApplyAnketa(character.Id, anketa, false);
                    changed = true;
                }
                if (changed) touched++;
            }

            _logger.Debug("Project template applied to {Count} characters", touched);
            if (touched > 0) CharactersUpdated?.Invoke();
            return touched;
        }

        // ── Блоки ────────────────────────────────────────────────────────

        private TemplateBlockViewModel? _activeBlock;

        /// <summary>Выбранный блок — единственный живой: его значки двигают и правят.</summary>
        public TemplateBlockViewModel? ActiveBlock
        {
            get => _activeBlock;
            private set => this.RaiseAndSetIfChanged(ref _activeBlock, value);
        }

        public void Activate(TemplateBlockViewModel? block)
        {
            if (ReferenceEquals(_activeBlock, block)) return;

            _activeBlock?.SetActive(false);
            ActiveBlock = block;
            block?.SetActive(true);
        }

        /// <summary>
        /// Свести блоки к сохранённым шаблонам. Блоки существующих шаблонов
        /// остаются теми же объектами: выбор и раскрытие не сбрасываются.
        /// </summary>
        public void Refresh()
        {
            var templates = _anketaService.GetTemplates();
            SyncBlocks(CustomBlocks, templates.Where(t => !t.IsBuiltIn).ToList());
            SyncBlocks(BuiltInBlocks, templates.Where(t => t.IsBuiltIn).ToList());

            if (_activeBlock != null && !AllBlocks.Contains(_activeBlock))
                ActiveBlock = null;

            this.RaisePropertyChanged(nameof(HasCustom));
            ApplySearch();
            RaiseProject();
        }

        private void SyncBlocks(ObservableCollection<TemplateBlockViewModel> blocks, List<CharacterTemplate> templates)
        {
            var ids = templates.Select(t => t.Id).ToHashSet();
            for (int i = blocks.Count - 1; i >= 0; i--)
                if (!ids.Contains(blocks[i].Id))
                    blocks.RemoveAt(i);

            for (int i = 0; i < templates.Count; i++)
            {
                var template = templates[i];
                var existing = blocks.FirstOrDefault(b => b.Id == template.Id);
                if (existing == null)
                {
                    existing = new TemplateBlockViewModel(template.Id, template.IsBuiltIn, this);
                    blocks.Insert(Math.Min(i, blocks.Count), existing);
                }
                else
                {
                    var from = blocks.IndexOf(existing);
                    if (from != i) blocks.Move(from, i);
                }

                existing.IsProject = template.Id == _projectTemplateId;
                existing.Update(template);
            }
        }

        private TemplateBlockViewModel? BlockOf(string templateId) =>
            AllBlocks.FirstOrDefault(b => b.Id == templateId);

        internal CharacterAnketa? GetAnketa(string id) => _anketaService.GetById(id);

        internal IReadOnlyList<CharacterAnketa> GetAllAnketas() => _anketaService.GetAll();

        internal Bitmap? LoadImage(string imageRef) =>
            string.IsNullOrWhiteSpace(imageRef) ? null : _avatarService?.LoadThumbnail(imageRef, 160);

        public bool CanUseImages => _avatarService != null;

        /// <summary>
        /// Правка шаблона: копия меняется, сохраняется сразу, блок обновляется,
        /// а если это шаблон проекта — вслед меняется и состав анкет для новых
        /// персонажей.
        ///
        /// Встроенный шаблон неизменен: правка уходит в его свою копию, она
        /// встаёт среди своих шаблонов и становится выбранной. Возвращается
        /// шаблон, который на деле изменился, или null, если менять было нечего.
        /// </summary>
        private string? Edit(string templateId, Func<CharacterTemplate, bool> change)
        {
            var source = _anketaService.GetTemplateById(templateId);
            if (source == null) return null;

            var copy = new CharacterTemplate
            {
                Id = source.Id,
                Name = source.Name,
                Description = source.Description,
                IsBuiltIn = false,
                AnketaIds = source.AnketaIds.ToList(),
                CreatedAt = source.CreatedAt,
                Icon = source.Icon,
                IconColor = source.IconColor,
                ImageRef = source.ImageRef
            };

            if (!change(copy)) return null;

            if (source.IsBuiltIn) return EditBuiltIn(source, copy);

            _anketaService.UpdateTemplate(copy);
            BlockOf(copy.Id)?.Update(copy);

            if (copy.Id == _projectTemplateId)
                SyncActiveAnketas(copy);

            RaiseProject();
            return copy.Id;
        }

        /// <summary>
        /// Своя копия встроенного шаблона с уже внесённой правкой. Если
        /// встроенный был шаблоном проекта, им становится копия — её и
        /// собирались править под проект.
        /// </summary>
        private string EditBuiltIn(CharacterTemplate source, CharacterTemplate changed)
        {
            var created = _anketaService.CreateTemplate(NextName(source.Name + " (копия)"));
            created.Description = changed.Description;
            created.AnketaIds = changed.AnketaIds.ToList();
            created.Icon = changed.Icon;
            created.IconColor = changed.IconColor;
            created.ImageRef = changed.ImageRef;
            _anketaService.UpdateTemplate(created);

            if (source.Id == _projectTemplateId)
                SetProjectTemplate(created.Id);

            Refresh();
            Activate(BlockOf(created.Id));
            CopyCreated?.Invoke(created.Id);

            _logger.Debug("Built-in template {Source} edited into copy {Copy}", source.Id, created.Id);
            return created.Id;
        }

        /// <summary>Правка встроенного шаблона создала его копию — вью прокручивает к ней.</summary>
        public event Action<string>? CopyCreated;

        /// <summary>Блок шаблона по идентификатору — своего или встроенного.</summary>
        public TemplateBlockViewModel? FindBlock(string templateId) => BlockOf(templateId);

        public string? Rename(string templateId, string name) =>
            Edit(templateId, t =>
            {
                var next = string.IsNullOrWhiteSpace(name) ? "Шаблон без названия" : name.Trim();
                if (t.Name == next) return false;
                t.Name = next;
                return true;
            });

        public string? SetDescription(string templateId, string description) =>
            Edit(templateId, t =>
            {
                var next = description?.Trim() ?? string.Empty;
                if (t.Description == next) return false;
                t.Description = next;
                return true;
            });

        public string? SetIcon(string templateId, string icon) =>
            Edit(templateId, t =>
            {
                if (t.Icon == icon) return false;
                t.Icon = icon;
                return true;
            });

        public string? SetIconColor(string templateId, string hex) =>
            Edit(templateId, t =>
            {
                if (string.Equals(t.IconColor, hex, StringComparison.OrdinalIgnoreCase)) return false;
                t.IconColor = hex;
                return true;
            });

        /// <summary>Картинка блока: кладётся в хранилище картинок проекта, как аватар персонажа.</summary>
        public async Task SetImageAsync(string templateId, byte[] data, string suggestedName)
        {
            if (_avatarService == null || data == null || data.Length == 0) return;
            if (_anketaService.GetTemplateById(templateId) is not { IsBuiltIn: false }) return;

            try
            {
                var imageRef = _avatarService.FindStoredByContent(data)
                               ?? await _avatarService.SaveToProjectAsync(data, suggestedName);
                if (string.IsNullOrWhiteSpace(imageRef)) return;

                Edit(templateId, t =>
                {
                    t.ImageRef = imageRef;
                    return true;
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Template image save failed");
            }
        }

        public string? ClearImage(string templateId) =>
            Edit(templateId, t =>
            {
                if (string.IsNullOrEmpty(t.ImageRef)) return false;
                t.ImageRef = string.Empty;
                return true;
            });

        /// <summary>
        /// Поставить анкету в конец шаблона. Одна анкета в шаблоне бывает
        /// только раз. Возвращает шаблон, куда она встала: у встроенного —
        /// его новую копию.
        /// </summary>
        public string? AddAnketa(string templateId, string anketaId)
        {
            if (_anketaService.GetById(anketaId) == null) return null;

            return Edit(templateId, t =>
            {
                if (t.AnketaIds.Contains(anketaId)) return false;
                t.AnketaIds.Add(anketaId);
                return true;
            });
        }

        public string? RemoveAnketa(string templateId, string anketaId) =>
            Edit(templateId, t => t.AnketaIds.Remove(anketaId));

        /// <summary>
        /// Переставить анкету внутри шаблона: index — место до удаления со
        /// старого, как его показывает полоска вставки.
        /// </summary>
        public string? MoveAnketa(string templateId, string anketaId, int index) =>
            Edit(templateId, t =>
            {
                var from = t.AnketaIds.IndexOf(anketaId);
                if (from < 0) return false;

                if (index > from) index--;
                index = Math.Clamp(index, 0, t.AnketaIds.Count - 1);
                if (index == from) return false;

                t.AnketaIds.RemoveAt(from);
                t.AnketaIds.Insert(index, anketaId);
                return true;
            });

        /// <summary>
        /// Переставить свой шаблон: index — место до удаления со старого, как
        /// его показывает полоска вставки.
        /// </summary>
        public void MoveTemplate(string templateId, int index)
        {
            var block = CustomBlocks.FirstOrDefault(b => b.Id == templateId);
            if (block == null) return;

            var from = CustomBlocks.IndexOf(block);
            if (index > from) index--;
            index = Math.Clamp(index, 0, CustomBlocks.Count - 1);
            if (index == from) return;

            _anketaService.MoveTemplate(templateId, index);
            CustomBlocks.Move(from, index);
        }

        /// <summary>Новый шаблон — сразу выбранный, чтобы в него можно было добавлять анкеты.</summary>
        public TemplateBlockViewModel? CreateTemplate()
        {
            var template = _anketaService.CreateTemplate(NextName("Новый шаблон"));
            Refresh();

            var block = BlockOf(template.Id);
            Activate(block);
            return block;
        }

        /// <summary>Копия шаблона — своя, её можно править. Так правят встроенные.</summary>
        public TemplateBlockViewModel? DuplicateTemplate(string templateId)
        {
            var source = _anketaService.GetTemplateById(templateId);
            if (source == null) return null;

            var copy = _anketaService.CreateTemplate(NextName(source.Name + " (копия)"));
            copy.Description = source.Description;
            copy.AnketaIds = source.AnketaIds.ToList();
            copy.Icon = source.Icon;
            copy.IconColor = source.IconColor;
            copy.ImageRef = source.ImageRef;
            _anketaService.UpdateTemplate(copy);

            // Копия встроенного шаблона проекта становится шаблоном проекта:
            // её и собирались править под проект.
            if (source.IsBuiltIn && source.Id == _projectTemplateId)
                SetProjectTemplate(copy.Id);

            Refresh();

            var block = BlockOf(copy.Id);
            Activate(block);
            return block;
        }

        public void DeleteTemplate(string templateId)
        {
            var template = _anketaService.GetTemplateById(templateId);
            if (template == null || template.IsBuiltIn) return;

            _anketaService.DeleteTemplate(templateId);

            // Анкеты шаблона проекта остаются за проектом: удаляется набор,
            // а не то, что новые персонажи уже получают.
            if (ProjectTemplateId == templateId) ProjectTemplateId = null;

            Refresh();
        }

        public void OpenAnketa(string anketaId) => OpenAnketaRequested?.Invoke(anketaId);

        private string NextName(string baseName)
        {
            var names = new HashSet<string>(_anketaService.GetTemplates().Select(t => t.Name), StringComparer.CurrentCultureIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; ; i++)
            {
                var candidate = baseName + " " + i;
                if (!names.Contains(candidate)) return candidate;
            }
        }
    }

    /// <summary>
    /// Блок шаблона. Неактивный хранит только то, что нужно нарисовать:
    /// название, описание, картинку и полосу значков. Живые значки,
    /// выбор значка и цвета, список для добавления — только у выбранного.
    /// </summary>
    public class TemplateBlockViewModel : ReactiveObject
    {
        private readonly CharactersTemplatesViewModel _owner;
        private List<string> _anketaIds = new();
        private string _searchText = string.Empty;

        public TemplateBlockViewModel(string id, bool isBuiltIn, CharactersTemplatesViewModel owner)
        {
            Id = id;
            IsBuiltIn = isBuiltIn;
            _owner = owner;
        }

        public string Id { get; }
        public bool IsBuiltIn { get; }
        public bool IsEditable => !IsBuiltIn;

        private string _name = string.Empty;
        public string Name { get => _name; private set => this.RaiseAndSetIfChanged(ref _name, value); }

        private string _description = string.Empty;
        public string Description
        {
            get => _description;
            private set
            {
                this.RaiseAndSetIfChanged(ref _description, value);
                this.RaisePropertyChanged(nameof(HasDescription));
            }
        }

        public bool HasDescription => !string.IsNullOrWhiteSpace(_description);

        private string _icon = CharacterAnketaIcons.Default;
        public string Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }

        private string _iconColor = CharacterAnketaIcons.DefaultColor;
        public string IconColor { get => _iconColor; private set => this.RaiseAndSetIfChanged(ref _iconColor, value); }

        private IBrush _colorBrush = Brushes.Gray;

        /// <summary>Цвет блока — рамка и подложка значка, как цвет у карточки персонажа.</summary>
        public IBrush ColorBrush { get => _colorBrush; private set => this.RaiseAndSetIfChanged(ref _colorBrush, value); }

        private Geometry? _iconGeometry;
        public Geometry? IconGeometry { get => _iconGeometry; private set => this.RaiseAndSetIfChanged(ref _iconGeometry, value); }

        private string _imageRef = string.Empty;
        private Bitmap? _image;
        public Bitmap? Image
        {
            get => _image;
            private set
            {
                this.RaiseAndSetIfChanged(ref _image, value);
                this.RaisePropertyChanged(nameof(HasImage));
                this.RaisePropertyChanged(nameof(HasNoImage));
            }
        }

        public bool HasImage => _image != null;
        public bool HasNoImage => _image == null;

        private bool _isProject;
        public bool IsProject
        {
            get => _isProject;
            set
            {
                this.RaiseAndSetIfChanged(ref _isProject, value);
                this.RaisePropertyChanged(nameof(ProjectTip));
            }
        }

        public string ProjectTip => _isProject ? "Шаблон проекта" : "Сделать шаблоном проекта";

        private bool _isMatch = true;
        public bool IsMatch { get => _isMatch; private set => this.RaiseAndSetIfChanged(ref _isMatch, value); }

        private bool _isDragSource;
        public bool IsDragSource { get => _isDragSource; set => this.RaiseAndSetIfChanged(ref _isDragSource, value); }

        // ── Анкеты ───────────────────────────────────────────────────────

        public int AnketaCount => _anketaIds.Count;

        public string CountCaption
        {
            get
            {
                var n = _anketaIds.Count;
                if (n == 0) return "Без анкет";
                var mod100 = n % 100;
                var mod10 = n % 10;
                var word = mod100 is >= 11 and <= 14 ? "анкет"
                    : mod10 == 1 ? "анкета"
                    : mod10 is >= 2 and <= 4 ? "анкеты"
                    : "анкет";
                return n + " " + word;
            }
        }

        public bool IsEmpty => _anketaIds.Count == 0;

        private IReadOnlyList<AnketaGlyph> _glyphs = Array.Empty<AnketaGlyph>();

        /// <summary>Первые значки для полосы неактивного блока.</summary>
        public IReadOnlyList<AnketaGlyph> Glyphs { get => _glyphs; private set => this.RaiseAndSetIfChanged(ref _glyphs, value); }

        /// <summary>Значки анкет блока: у каждого блока свои, с правкой и удалением.</summary>
        public ObservableCollection<TemplateAnketaItemViewModel> Anketas { get; } = new();

        public bool HasMore => _anketaIds.Count > CharactersTemplatesViewModel.CollapsedLimit;

        public string MoreCaption => _isExpanded
            ? "Свернуть"
            : "Ещё " + (_anketaIds.Count - CharactersTemplatesViewModel.CollapsedLimit);

        // ── Выбор и раскрытие ────────────────────────────────────────────

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isActive, value);
                this.RaisePropertyChanged(nameof(IsInactive));
                this.RaisePropertyChanged(nameof(CanCustomize));
                this.RaisePropertyChanged(nameof(CanAddAnketas));
            }
        }

        public bool IsInactive => !_isActive;

        /// <summary>Значок, цвет, картинку, название меняют у выбранного своего блока.</summary>
        public bool CanCustomize => _isActive && IsEditable;

        /// <summary>Анкету добавляют в любой шаблон; во встроенный — через его копию.</summary>
        public bool CanAddAnketas => true;

        private bool _isExpanded;

        /// <summary>Блок раскрыт целиком — видны все анкеты, а не первые десять.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isExpanded, value);
                this.RaisePropertyChanged(nameof(MoreCaption));
            }
        }

        internal void SetActive(bool active)
        {
            if (!active) IsExpanded = false;
            IsActive = active;
            if (!active)
            {
                IconOptions.Clear();
                IconColorOptions.Clear();
                PickerItems.Clear();
            }
        }

        public void ToggleExpanded()
        {
            if (!_isActive) _owner.Activate(this);
            if (!HasMore && !_isExpanded) return;
            IsExpanded = !_isExpanded;
            RebuildLive();
        }

        // ── Обновление ───────────────────────────────────────────────────

        internal void Update(CharacterTemplate template)
        {
            Name = template.Name;
            Description = template.Description;
            Icon = string.IsNullOrWhiteSpace(template.Icon) ? CharacterAnketaIcons.Default : template.Icon;
            IconColor = string.IsNullOrWhiteSpace(template.IconColor) ? CharacterAnketaIcons.DefaultColor : template.IconColor;
            ColorBrush = Color.TryParse(IconColor, out var color)
                ? new SolidColorBrush(color)
                : new SolidColorBrush(Color.Parse(CharacterAnketaIcons.DefaultColor));
            IconGeometry = AnketaIconBadge.GetGeometry(Icon);

            if (!string.Equals(_imageRef, template.ImageRef ?? string.Empty, StringComparison.Ordinal))
            {
                _imageRef = template.ImageRef ?? string.Empty;
                Image = _owner.LoadImage(_imageRef);
            }

            _anketaIds = template.AnketaIds
                .Where(id => _owner.GetAnketa(id) != null)
                .ToList();

            Glyphs = _anketaIds
                .Take(CharactersTemplatesViewModel.CollapsedLimit)
                .Select(id => _owner.GetAnketa(id)!)
                .Select(a => new AnketaGlyph(a.Id, a.Icon, a.IconColor, a.Name, a.Description))
                .ToList();

            _searchText = template.Name + " " + template.Description + " " +
                          string.Join(" ", _anketaIds.Select(id => _owner.GetAnketa(id)?.Name));

            this.RaisePropertyChanged(nameof(AnketaCount));
            this.RaisePropertyChanged(nameof(CountCaption));
            this.RaisePropertyChanged(nameof(IsEmpty));
            this.RaisePropertyChanged(nameof(HasMore));
            this.RaisePropertyChanged(nameof(MoreCaption));

            if (!HasMore && _isExpanded) IsExpanded = false;
            RebuildLive();
            SyncIconOptions();
        }

        public void ApplySearch(string query)
        {
            IsMatch = string.IsNullOrWhiteSpace(query) ||
                      query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .All(word => _searchText.Contains(word, StringComparison.CurrentCultureIgnoreCase));
        }

        /// <summary>
        /// Живые значки сводятся к нужному составу на месте: перестановка не
        /// пересоздаёт остальные, и они не мигают.
        /// </summary>
        private void RebuildLive()
        {
            var ids = (_isExpanded ? _anketaIds : _anketaIds.Take(CharactersTemplatesViewModel.CollapsedLimit)).ToList();

            for (int i = Anketas.Count - 1; i >= 0; i--)
                if (!ids.Contains(Anketas[i].Id))
                    Anketas.RemoveAt(i);

            for (int i = 0; i < ids.Count; i++)
            {
                var existing = Anketas.FirstOrDefault(a => a.Id == ids[i]);
                if (existing == null)
                {
                    var anketa = _owner.GetAnketa(ids[i]);
                    if (anketa == null) continue;
                    Anketas.Insert(Math.Min(i, Anketas.Count), new TemplateAnketaItemViewModel(anketa, Id));
                    continue;
                }

                var from = Anketas.IndexOf(existing);
                if (from != i && i < Anketas.Count) Anketas.Move(from, i);
                if (_owner.GetAnketa(existing.Id) is { } fresh) existing.Update(fresh);
            }
        }

        // ── Значок, цвет ─────────────────────────────────────────────────

        public ObservableCollection<AnketaIconOptionViewModel> IconOptions { get; } = new();
        public ObservableCollection<AnketaIconColorOptionViewModel> IconColorOptions { get; } = new();

        /// <summary>Наборы для выбора значка и цвета строятся, когда открывают окошко блока.</summary>
        public void PrepareCustomize()
        {
            IconOptions.Clear();
            IconColorOptions.Clear();

            foreach (var entry in CharacterAnketaIcons.All)
                IconOptions.Add(new AnketaIconOptionViewModel(entry.Key, entry.Label, entry.Key == Icon,
                    key => _owner.SetIcon(Id, key)) { IconColor = IconColor });

            foreach (var hex in CharacterAnketaIcons.Colors)
                IconColorOptions.Add(new AnketaIconColorOptionViewModel(hex,
                    string.Equals(hex, IconColor, StringComparison.OrdinalIgnoreCase),
                    h => _owner.SetIconColor(Id, h)) { Icon = Icon });

            EditName = Name;
            EditDescription = Description;
        }

        private void SyncIconOptions()
        {
            foreach (var option in IconOptions)
            {
                option.SetSelectedSilently(option.Key == Icon);
                option.IconColor = IconColor;
            }

            foreach (var option in IconColorOptions)
            {
                option.SetSelectedSilently(string.Equals(option.Hex, IconColor, StringComparison.OrdinalIgnoreCase));
                option.Icon = Icon;
            }
        }

        private string _editName = string.Empty;

        /// <summary>Название в окошке блока — сохраняется на ходу.</summary>
        public string EditName
        {
            get => _editName;
            set
            {
                if (_editName == value) return;
                this.RaiseAndSetIfChanged(ref _editName, value ?? string.Empty);
                _owner.Rename(Id, _editName);
            }
        }

        private string _editDescription = string.Empty;

        public string EditDescription
        {
            get => _editDescription;
            set
            {
                if (_editDescription == value) return;
                this.RaiseAndSetIfChanged(ref _editDescription, value ?? string.Empty);
                _owner.SetDescription(Id, _editDescription);
            }
        }

        // ── Добавление анкеты ────────────────────────────────────────────

        /// <summary>Анкеты для меню «+ Анкета»: каких ещё нет в шаблоне, с поиском.</summary>
        public ObservableCollection<TemplatePickItemViewModel> PickerItems { get; } = new();

        private string _pickerQuery = string.Empty;
        public string PickerQuery
        {
            get => _pickerQuery;
            set
            {
                if (_pickerQuery == value) return;
                this.RaiseAndSetIfChanged(ref _pickerQuery, value ?? string.Empty);
                RefreshPicker();
            }
        }

        public bool HasPickerItems => PickerItems.Count > 0;

        public void RefreshPicker()
        {
            PickerItems.Clear();

            var words = _pickerQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var anketa in _owner.GetAllAnketas()
                         .Where(a => !_anketaIds.Contains(a.Id))
                         .OrderBy(a => a.IsBuiltIn)
                         .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var text = anketa.Name + " " + anketa.Description + " " + string.Join(" ", anketa.Fields.Select(f => f.Name));
                if (words.Any(w => !text.Contains(w, StringComparison.CurrentCultureIgnoreCase))) continue;
                PickerItems.Add(new TemplatePickItemViewModel(anketa));
            }

            this.RaisePropertyChanged(nameof(HasPickerItems));
        }
    }

    /// <summary>Анкета в меню добавления в шаблон.</summary>
    public class TemplatePickItemViewModel
    {
        public TemplatePickItemViewModel(CharacterAnketa anketa)
        {
            Id = anketa.Id;
            Name = anketa.Name;
            Description = anketa.Description;
            Icon = anketa.Icon;
            IconColor = anketa.IconColor;
            FieldCount = anketa.Fields.Count;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Icon { get; }
        public string IconColor { get; }
        public int FieldCount { get; }
    }

    /// <summary>Живой значок анкеты в выбранном блоке.</summary>
    public class TemplateAnketaItemViewModel : ReactiveObject
    {
        public TemplateAnketaItemViewModel(CharacterAnketa anketa, string templateId)
        {
            Id = anketa.Id;
            TemplateId = templateId;
            Update(anketa);
        }

        public string Id { get; }

        /// <summary>Шаблон, в чьём блоке стоит значок.</summary>
        public string TemplateId { get; }

        private string _name = string.Empty;
        public string Name { get => _name; private set => this.RaiseAndSetIfChanged(ref _name, value); }

        private string _description = string.Empty;
        public string Description { get => _description; private set => this.RaiseAndSetIfChanged(ref _description, value); }

        private string _icon = string.Empty;
        public string Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }

        private string _iconColor = string.Empty;
        public string IconColor { get => _iconColor; private set => this.RaiseAndSetIfChanged(ref _iconColor, value); }

        private bool _isDragSource;
        public bool IsDragSource { get => _isDragSource; set => this.RaiseAndSetIfChanged(ref _isDragSource, value); }

        public void Update(CharacterAnketa anketa)
        {
            Name = anketa.Name;
            Description = anketa.Description;
            Icon = anketa.Icon;
            IconColor = anketa.IconColor;
        }
    }
}
