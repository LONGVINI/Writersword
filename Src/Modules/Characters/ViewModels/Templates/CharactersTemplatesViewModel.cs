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
    /// («Фэнтези: Воин» = «Внешность» + «Характер» + «Бой» + «Магия»). Одна
    /// анкета может стоять в нескольких шаблонах, и у каждой в шаблоне своя
    /// глубина по умолчанию — буква К, П или Г в углу значка.
    ///
    /// У каждого блока значки анкет настоящие: под курсором карандаш —
    /// открыть анкету в редакторе — и крестик — убрать из шаблона; в конце
    /// ряда «+». Показываются первые десять, остальное раскрывается на месте.
    /// Встроенный шаблон неизменен: правка уходит в его свою копию.
    ///
    /// Свои шаблоны лежат по папкам, как персонажи: по умолчанию сверху «Мои
    /// шаблоны» — без папки, — за ними папки, внизу «Встроенные»; порядок
    /// разделов любой, его меняют стрелками в шапках. Блоки
    /// перетаскивают между любыми разделами, кроме встроенного. Скрытый
    /// шаблон остаётся на своём месте, только бледнее, а в поиске и в
    /// списке шаблонов карточки его нет.
    ///
    /// Персонажу шаблон подключают в его карточке.
    /// </summary>
    public class CharactersTemplatesViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersTemplatesViewModel>();

        /// <summary>Сколько значков видно в свёрнутом блоке.</summary>
        public const int CollapsedLimit = 10;

        private readonly ICharacterAnketaService _anketaService;
        private readonly ICharacterAvatarService? _avatarService;

        /// <summary>
        /// Разделы вкладки по порядку: «Мои шаблоны» (свои без папки), папки,
        /// «Встроенные». Порядок любой — его хранит проект.
        /// </summary>
        public ObservableCollection<TemplateFolderViewModel> Folders { get; } = new();

        private readonly TemplateFolderViewModel _ungrouped;
        private readonly TemplateFolderViewModel _builtIn;

        private IEnumerable<TemplateBlockViewModel> AllBlocks => Folders.SelectMany(f => f.Blocks);

        private string _searchQuery = string.Empty;

        /// <summary>
        /// Поиск по шаблонам: название, описание и названия их анкет. Скрытые
        /// шаблоны поиском не находятся.
        /// </summary>
        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (_searchQuery == value) return;
                this.RaiseAndSetIfChanged(ref _searchQuery, value ?? string.Empty);
                this.RaisePropertyChanged(nameof(IsSearching));
                ApplySearch();
            }
        }

        /// <summary>Идёт поиск: разделы показывают найденное и свёрнутыми.</summary>
        public bool IsSearching => !string.IsNullOrWhiteSpace(_searchQuery);

        private void ApplySearch()
        {
            foreach (var block in AllBlocks) block.ApplySearch(_searchQuery);
            RefreshFolderStates();
        }

        private void RefreshFolderStates()
        {
            for (int i = 0; i < Folders.Count; i++)
                Folders[i].RefreshState(canMoveUp: i > 0, canMoveDown: i + 1 < Folders.Count);
        }

        public ReactiveCommand<Unit, Unit> RestartOnboardingCommand { get; }

        public event Action? OnboardingRestartRequested;

        /// <summary>Открыть анкету в редакторе анкет.</summary>
        public event Action<string>? OpenAnketaRequested;

        public CharactersTemplatesViewModel(
            ICharacterAnketaService anketaService,
            ICharacterAvatarService? avatarService = null)
        {
            _anketaService = anketaService;
            _avatarService = avatarService;

            RestartOnboardingCommand = ReactiveCommand.Create(() => OnboardingRestartRequested?.Invoke());

            _ungrouped = new TemplateFolderViewModel(this, TemplateSections.Ungrouped, "Мои шаблоны");
            _builtIn = new TemplateFolderViewModel(this, TemplateSections.BuiltIn, "Встроенные");

            Refresh();
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
            // Шаблон выбрали — его папка становится текущей: новый шаблон
            // встанет туда же, где работают.
            if (block != null && FolderOf(block) is { IsBuiltIn: false } folder)
                SelectFolder(folder);

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
            // Разделы — в порядке проекта: папки, «Мои шаблоны» и «Встроенные»
            // вперемешку, как их расставили. Папки существующих разделов
            // остаются теми же объектами.
            var folders = _anketaService.GetTemplateFolders();
            var byId = new Dictionary<string, TemplateFolderViewModel>(StringComparer.Ordinal)
            {
                [_ungrouped.Id] = _ungrouped,
                [_builtIn.Id] = _builtIn
            };
            foreach (var folder in folders)
            {
                var section = Folders.FirstOrDefault(f => f.Id == folder.Id)
                              ?? new TemplateFolderViewModel(this, folder);
                section.Update(folder);
                byId[folder.Id] = section;
            }

            var sections = new List<TemplateFolderViewModel>();
            foreach (var id in _anketaService.GetTemplateSectionOrder())
                if (byId.Remove(id, out var ordered))
                    sections.Add(ordered);

            // Раздел, которого порядок не знает, не пропадает: «Мои шаблоны»
            // встают первыми, папки и «Встроенные» — в конец.
            if (byId.Remove(_ungrouped.Id, out var ungrouped)) sections.Insert(0, ungrouped);
            foreach (var folder in folders)
                if (byId.Remove(folder.Id, out var rest))
                    sections.Add(rest);
            if (byId.Remove(_builtIn.Id, out var builtIn)) sections.Add(builtIn);

            SyncCollection(Folders, sections);

            // Блоки: свой шаблон лежит в своей папке, а если такой папки нет
            // — в «Моих шаблонах». Блок, переехавший в другую папку, остаётся
            // тем же объектом: выбор и раскрытие при переезде не теряются.
            var folderIds = folders.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            var pool = new Dictionary<string, TemplateBlockViewModel>(StringComparer.Ordinal);
            foreach (var block in AllBlocks) pool[block.Id] = block;

            var templates = _anketaService.GetTemplates();
            foreach (var section in Folders)
            {
                IEnumerable<CharacterTemplate> list = section.IsBuiltIn
                    ? templates.Where(t => t.IsBuiltIn)
                    : section.IsUngrouped
                        ? templates.Where(t => !t.IsBuiltIn && (string.IsNullOrEmpty(t.FolderId) || !folderIds.Contains(t.FolderId)))
                        : templates.Where(t => !t.IsBuiltIn && t.FolderId == section.Id);

                var blocks = new List<TemplateBlockViewModel>();
                foreach (var template in list)
                {
                    if (!pool.TryGetValue(template.Id, out var block))
                    {
                        block = new TemplateBlockViewModel(template.Id, template.IsBuiltIn, this);
                        pool[template.Id] = block;
                    }
                    block.IsHidden = _anketaService.IsTemplateHidden(template.Id);
                    block.Update(template);
                    blocks.Add(block);
                }

                SyncCollection(section.Blocks, blocks);
                section.SetExpandedSilently(!_anketaService.IsTemplateFolderCollapsed(section.Id));
            }

            if (_activeBlock != null && !AllBlocks.Contains(_activeBlock))
                ActiveBlock = null;

            if (_activeFolder != null && !Folders.Contains(_activeFolder))
                SelectFolder(null);

            ApplySearch();
        }

        /// <summary>
        /// Свести коллекцию к нужному составу на месте: оставшиеся элементы не
        /// пересоздаются, лишние уходят, недостающие встают на свои места.
        /// </summary>
        private static void SyncCollection<T>(ObservableCollection<T> collection, IList<T> target) where T : class
        {
            for (int i = collection.Count - 1; i >= 0; i--)
                if (!target.Contains(collection[i]))
                    collection.RemoveAt(i);

            for (int i = 0; i < target.Count; i++)
            {
                if (i < collection.Count && ReferenceEquals(collection[i], target[i])) continue;

                var from = collection.IndexOf(target[i]);
                if (from >= 0) collection.Move(from, i);
                else collection.Insert(Math.Min(i, collection.Count), target[i]);
            }
        }

        /// <summary>Раздел, в котором лежит блок.</summary>
        public TemplateFolderViewModel? FolderOf(TemplateBlockViewModel block) =>
            Folders.FirstOrDefault(f => f.Blocks.Contains(block));

        // ── Папки ────────────────────────────────────────────────────────

        private TemplateFolderViewModel? _activeFolder;

        /// <summary>
        /// Текущая папка — та, по которой щёлкнули последней или в которой
        /// выбран шаблон. Новый шаблон встаёт в неё; встроенные текущими не
        /// бывают.
        /// </summary>
        public void SelectFolder(TemplateFolderViewModel? folder)
        {
            if (folder is { IsBuiltIn: true }) folder = null;
            if (ReferenceEquals(_activeFolder, folder)) return;

            if (_activeFolder != null) _activeFolder.IsSelected = false;
            _activeFolder = folder;
            if (_activeFolder != null) _activeFolder.IsSelected = true;
        }

        /// <summary>Новая папка — в конце папок, сразу с полем названия.</summary>
        public TemplateFolderViewModel? CreateFolder()
        {
            var names = new HashSet<string>(_anketaService.GetTemplateFolders().Select(f => f.Name), StringComparer.CurrentCultureIgnoreCase);
            var name = "Новая папка";
            for (int i = 2; names.Contains(name); i++) name = "Новая папка " + i;

            var created = _anketaService.CreateTemplateFolder(name);
            Refresh();

            var folder = Folders.FirstOrDefault(f => f.Id == created.Id);
            if (folder == null) return null;

            SelectFolder(folder);
            folder.IsRenaming = true;
            return folder;
        }

        private void EditFolder(string folderId, Action<CharacterTemplateFolder> change)
        {
            var source = _anketaService.GetTemplateFolders().FirstOrDefault(f => f.Id == folderId);
            if (source == null) return;

            var copy = new CharacterTemplateFolder
            {
                Id = source.Id,
                Name = source.Name,
                Comment = source.Comment,
                Color = source.Color,
                CreatedAt = source.CreatedAt
            };
            change(copy);

            _anketaService.UpdateTemplateFolder(copy);
            Refresh();
        }

        internal void RenameFolder(string folderId, string name) =>
            EditFolder(folderId, f => f.Name = string.IsNullOrWhiteSpace(name) ? "Папка" : name.Trim());

        internal void SetFolderComment(string folderId, string comment) =>
            EditFolder(folderId, f => f.Comment = comment?.Trim() ?? string.Empty);

        internal void SetFolderColor(string folderId, string color) =>
            EditFolder(folderId, f => f.Color = string.IsNullOrWhiteSpace(color) ? "#607D8B" : color);

        /// <summary>Удалить папку: её шаблоны уходят в конец «Моих шаблонов».</summary>
        public void DeleteFolder(string folderId)
        {
            _anketaService.DeleteTemplateFolder(folderId);
            Refresh();
        }

        internal void MoveFolder(string folderId, int shift)
        {
            _anketaService.MoveTemplateFolder(folderId, shift);
            Refresh();
        }

        internal void SetFolderCollapsed(string folderId, bool collapsed) =>
            _anketaService.SetTemplateFolderCollapsed(folderId, collapsed);

        // ── Скрытые шаблоны ──────────────────────────────────────────────

        /// <summary>
        /// Скрыть шаблон или вернуть: скрытый стоит на своём месте бледнее, а
        /// в поиске и в списке шаблонов карточки его нет.
        /// </summary>
        public void ToggleHidden(TemplateBlockViewModel block)
        {
            _anketaService.SetTemplateHidden(block.Id, !block.IsHidden);
            block.IsHidden = _anketaService.IsTemplateHidden(block.Id);
            block.ApplySearch(_searchQuery);
            RefreshFolderStates();
        }

        private TemplateBlockViewModel? BlockOf(string templateId) =>
            AllBlocks.FirstOrDefault(b => b.Id == templateId);

        internal CharacterAnketa? GetAnketa(string id) => _anketaService.GetById(id);

        internal IReadOnlyList<CharacterAnketa> GetAllAnketas() => _anketaService.GetAll();

        internal Bitmap? LoadImage(string imageRef) =>
            string.IsNullOrWhiteSpace(imageRef) ? null : _avatarService?.LoadThumbnail(imageRef, 160);

        public bool CanUseImages => _avatarService != null;

        /// <summary>
        /// Правка шаблона: копия меняется, сохраняется сразу, блок обновляется.
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
                AnketaDepths = new Dictionary<string, int>(source.AnketaDepths ?? new Dictionary<string, int>()),
                CreatedAt = source.CreatedAt,
                Icon = source.Icon,
                IconColor = source.IconColor,
                IconBackground = source.IconBackground,
                IconBorder = source.IconBorder,
                FrameColor = source.FrameColor,
                ImageRef = source.ImageRef,
                FolderId = source.FolderId ?? string.Empty
            };

            if (!change(copy)) return null;

            if (source.IsBuiltIn) return EditBuiltIn(source, copy);

            _anketaService.UpdateTemplate(copy);
            BlockOf(copy.Id)?.Update(copy);
            return copy.Id;
        }

        /// <summary>Своя копия встроенного шаблона с уже внесённой правкой.</summary>
        private string EditBuiltIn(CharacterTemplate source, CharacterTemplate changed)
        {
            var created = _anketaService.CreateTemplate(NextName(source.Name + " (копия)"));
            created.Description = changed.Description;
            created.AnketaIds = changed.AnketaIds.ToList();
            created.AnketaDepths = new Dictionary<string, int>(changed.AnketaDepths);
            created.Icon = changed.Icon;
            created.IconColor = changed.IconColor;
            created.IconBackground = changed.IconBackground;
            created.IconBorder = changed.IconBorder;
            created.FrameColor = changed.FrameColor;
            created.ImageRef = changed.ImageRef;
            created.FolderId = string.Empty;
            _anketaService.UpdateTemplate(created);

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

        /// <summary>
        /// Название и описание одной правкой — из полей шапки блока. Одной,
        /// а не двумя: у встроенного шаблона каждая правка заводит копию.
        /// </summary>
        public string? SetNameAndDescription(string templateId, string name, string description) =>
            Edit(templateId, t =>
            {
                var nextName = string.IsNullOrWhiteSpace(name) ? "Шаблон без названия" : name.Trim();
                var nextDescription = description?.Trim() ?? string.Empty;
                if (t.Name == nextName && t.Description == nextDescription) return false;
                t.Name = nextName;
                t.Description = nextDescription;
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

        /// <summary>
        /// Значок, цвет, подложка и рамка одной правкой — из окна значков.
        /// Одной, а не несколькими: у встроенного шаблона каждая правка заводит
        /// копию, и значок с цветом разъехались бы по разным копиям. Сюда же
        /// цвет внешней рамки блока — он выбирается в том же окне.
        /// </summary>
        public string? SetIconAndColor(string templateId, string icon, string color, string background, string border,
            string blockFrame) =>
            Edit(templateId, t =>
            {
                var plate = background ?? string.Empty;
                var frame = border ?? string.Empty;
                var outer = blockFrame ?? string.Empty;
                if (t.Icon == icon &&
                    string.Equals(t.IconColor, color, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.IconBackground ?? string.Empty, plate, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.IconBorder ?? string.Empty, frame, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.FrameColor ?? string.Empty, outer, StringComparison.OrdinalIgnoreCase))
                    return false;
                t.Icon = icon;
                t.IconColor = color;
                t.IconBackground = plate;
                t.IconBorder = frame;
                t.FrameColor = outer;
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
            Edit(templateId, t =>
            {
                if (!t.AnketaIds.Remove(anketaId)) return false;
                t.AnketaDepths.Remove(anketaId);
                return true;
            });

        /// <summary>
        /// Глубина анкеты в шаблоне: 1 — кратко, 2 — подробно, 3 — глубоко,
        /// null — по важности персонажа. Глубже последнего уровня анкеты не
        /// бывает. Возвращает шаблон, где глубина встала: у встроенного — его
        /// новую копию.
        /// </summary>
        public string? SetAnketaDepth(string templateId, string anketaId, int? depth)
        {
            var anketa = _anketaService.GetById(anketaId);
            if (anketa == null) return null;

            var max = CharacterDetailLevels.MaxOf(anketa);
            int? level = depth.HasValue ? Math.Clamp(depth.Value, CharacterDetailLevels.Brief, max) : null;

            return Edit(templateId, t =>
            {
                if (!t.AnketaIds.Contains(anketaId)) return false;

                int? current = t.AnketaDepths.TryGetValue(anketaId, out var stored) ? stored : null;
                if (current == level) return false;

                if (level.HasValue) t.AnketaDepths[anketaId] = level.Value;
                else t.AnketaDepths.Remove(anketaId);
                return true;
            });
        }

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
        /// Перестановка на лету, пока блок тащат: двигается только то, что на
        /// экране, — блок встаёт в раздел folder на место index, соседи
        /// расступаются. В проект порядок уходит одним шагом по отпусканию —
        /// CommitArrangement. Встроенный раздел блоков не принимает.
        /// </summary>
        public void PreviewPlaceBlock(TemplateBlockViewModel block, TemplateFolderViewModel folder, int index)
        {
            if (folder.IsBuiltIn || !block.IsEditable) return;
            if (FolderOf(block) is not { } from) return;

            if (ReferenceEquals(from, folder))
            {
                var current = folder.Blocks.IndexOf(block);
                var to = Math.Clamp(index, 0, folder.Blocks.Count - 1);
                if (current >= 0 && to != current) folder.Blocks.Move(current, to);
            }
            else
            {
                from.Blocks.Remove(block);
                folder.Blocks.Insert(Math.Clamp(index, 0, folder.Blocks.Count), block);
            }

            RefreshFolderStates();
        }

        /// <summary>
        /// Записать в проект раскладку своих шаблонов такой, какой её видно:
        /// порядок разделов сверху вниз и порядок блоков в каждом.
        /// </summary>
        public void CommitArrangement()
        {
            var order = new List<(string Id, string FolderId)>();
            foreach (var folder in Folders)
            {
                if (folder.IsBuiltIn) continue;
                var folderId = folder.IsUngrouped ? string.Empty : folder.Id;
                foreach (var block in folder.Blocks)
                    order.Add((block.Id, folderId));
            }

            _anketaService.ArrangeTemplates(order);
            Refresh();
        }

        /// <summary>
        /// Новый шаблон — в конце текущей папки (или «Моих шаблонов»), сразу
        /// выбранный, чтобы в него можно было добавлять анкеты.
        /// </summary>
        public TemplateBlockViewModel? CreateTemplate()
        {
            var template = _anketaService.CreateTemplate(NextName("Новый шаблон"));
            template.FolderId = _activeFolder is { IsCustom: true } ? _activeFolder.Id : string.Empty;
            _anketaService.UpdateTemplate(template);
            Refresh();

            var block = BlockOf(template.Id);
            Activate(block);
            return block;
        }

        /// <summary>
        /// Копия шаблона — своя, её можно править. Так правят встроенные.
        /// Встаёт в конец той же папки; копия встроенного — в конец «Моих
        /// шаблонов».
        /// </summary>
        public TemplateBlockViewModel? DuplicateTemplate(string templateId)
        {
            var source = _anketaService.GetTemplateById(templateId);
            if (source == null) return null;

            var copy = _anketaService.CreateTemplate(NextName(source.Name + " (копия)"));
            copy.Description = source.Description;
            copy.AnketaIds = source.AnketaIds.ToList();
            copy.AnketaDepths = new Dictionary<string, int>(source.AnketaDepths ?? new Dictionary<string, int>());
            copy.Icon = source.Icon;
            copy.IconColor = source.IconColor;
            copy.IconBackground = source.IconBackground;
            copy.IconBorder = source.IconBorder;
            copy.FrameColor = source.FrameColor;
            copy.ImageRef = source.ImageRef;
            copy.FolderId = source.IsBuiltIn ? string.Empty : source.FolderId ?? string.Empty;
            _anketaService.UpdateTemplate(copy);

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

            Refresh();
        }

        public void OpenAnketa(string anketaId) => OpenAnketaRequested?.Invoke(anketaId);

        /// <summary>Новую анкету просят для шаблона: она встанет в него после первого сохранения.</summary>
        public event Action<string>? NewAnketaRequested;

        public void RequestNewAnketa(string templateId) => NewAnketaRequested?.Invoke(templateId);

        private bool _pickerAsList;

        /// <summary>Меню «+» списком с названиями; иначе — значками с подсказками.</summary>
        public bool PickerAsList
        {
            get => _pickerAsList;
            set => this.RaiseAndSetIfChanged(ref _pickerAsList, value);
        }

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

        private string _iconBackground = string.Empty;

        /// <summary>Подложка значка: пусто — бледная цвета шаблона, «fill» — залитая, «none» — без подложки, иначе свой код цвета.</summary>
        public string IconBackground { get => _iconBackground; private set => this.RaiseAndSetIfChanged(ref _iconBackground, value); }

        private string _iconBorder = string.Empty;

        /// <summary>Рамка вокруг подложки значка: пусто — нет, иначе CharacterIconFrame.</summary>
        public string IconBorder { get => _iconBorder; private set => this.RaiseAndSetIfChanged(ref _iconBorder, value); }

        private string _frameColor = string.Empty;

        /// <summary>Цвет внешней рамки блока: пусто — цвета шаблона, иначе свой код цвета.</summary>
        public string FrameColor { get => _frameColor; private set => this.RaiseAndSetIfChanged(ref _frameColor, value); }

        private IBrush _colorBrush = Brushes.Gray;

        /// <summary>Цвет внешней рамки блока и ленточки — свой или цвета шаблона, как цвет у карточки персонажа.</summary>
        public IBrush ColorBrush { get => _colorBrush; private set => this.RaiseAndSetIfChanged(ref _colorBrush, value); }

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

        private bool _isMatch = true;
        public bool IsMatch { get => _isMatch; private set => this.RaiseAndSetIfChanged(ref _isMatch, value); }

        private bool _isHidden;

        /// <summary>
        /// Шаблон скрыт: стоит на своём месте бледнее, а в поиске и в списке
        /// шаблонов карточки его нет.
        /// </summary>
        public bool IsHidden
        {
            get => _isHidden;
            internal set
            {
                this.RaiseAndSetIfChanged(ref _isHidden, value);
                this.RaisePropertyChanged(nameof(HideTip));
            }
        }

        public string HideTip => _isHidden ? "Показать шаблон" : "Скрыть шаблон";

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
                this.RaisePropertyChanged(nameof(CanAddAnketas));
            }
        }

        public bool IsInactive => !_isActive;

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
            if (!active) PickerItems.Clear();
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
            if (!_isTextEditing) SyncTextEdit();
            Icon = string.IsNullOrWhiteSpace(template.Icon) ? CharacterAnketaIcons.Default : template.Icon;
            IconColor = string.IsNullOrWhiteSpace(template.IconColor) ? CharacterAnketaIcons.DefaultColor : template.IconColor;
            IconBackground = template.IconBackground ?? string.Empty;
            IconBorder = template.IconBorder ?? string.Empty;
            FrameColor = template.FrameColor ?? string.Empty;
            // Цвет — код цвета программы: одноцвет или градиент. Им красятся
            // значок с подложкой, если она не своя, а рамка блока — им же,
            // если у рамки нет своего цвета. Сам значок рисует AnketaIconBadge в
            // виде плитки: тот же, что в окне значков, поэтому превью там
            // совпадает с шапкой блока.
            ColorBrush = AnketaIconBadge.BrushOf(string.IsNullOrWhiteSpace(FrameColor) ? IconColor : FrameColor);

            if (!string.Equals(_imageRef, template.ImageRef ?? string.Empty, StringComparison.Ordinal))
            {
                _imageRef = template.ImageRef ?? string.Empty;
                Image = _owner.LoadImage(_imageRef);
            }

            _anketaIds = template.AnketaIds
                .Where(id => _owner.GetAnketa(id) != null)
                .ToList();

            _depths = new Dictionary<string, int>(template.AnketaDepths ?? new Dictionary<string, int>());

            Glyphs = _anketaIds
                .Take(CharactersTemplatesViewModel.CollapsedLimit)
                .Select(id => _owner.GetAnketa(id)!)
                .Select(a => new AnketaGlyph(a.Id, a.Icon, a.IconColor, a.Name, a.Description,
                    a.IconBackground ?? string.Empty, a.IconBorder ?? string.Empty))
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
        }

        public void ApplySearch(string query)
        {
            // Скрытый шаблон поиском не находится — но без поиска он на месте.
            IsMatch = string.IsNullOrWhiteSpace(query) ||
                      (!_isHidden &&
                       query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                           .All(word => _searchText.Contains(word, StringComparison.CurrentCultureIgnoreCase)));
        }

        /// <summary>
        /// Живые значки сводятся к нужному составу на месте: перестановка не
        /// пересоздаёт остальные, и они не мигают.
        /// </summary>
        private Dictionary<string, int> _depths = new();

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
                    var item = new TemplateAnketaItemViewModel(anketa, Id);
                    item.SetDepth(_depths.TryGetValue(anketa.Id, out var newDepth) ? newDepth : null);
                    Anketas.Insert(Math.Min(i, Anketas.Count), item);
                    continue;
                }

                var from = Anketas.IndexOf(existing);
                if (from != i && i < Anketas.Count) Anketas.Move(from, i);
                if (_owner.GetAnketa(existing.Id) is { } fresh) existing.Update(fresh);
                existing.SetDepth(_depths.TryGetValue(existing.Id, out var depth) ? depth : null);
            }
        }

        // ── Название и описание в шапке ──────────────────────────────────

        // Пока поле в фокусе, записанное снаружи не перебивает набираемое:
        // запись обрезает пробелы, и курсор терял бы пробел в конце слова.
        private bool _isTextEditing;

        private string _editName = string.Empty;

        /// <summary>Название в поле шапки. Записывается по Enter или уходу из поля.</summary>
        public string EditName
        {
            get => _editName;
            set => this.RaiseAndSetIfChanged(ref _editName, value ?? string.Empty);
        }

        private string _editDescription = string.Empty;

        /// <summary>Описание в поле шапки. Записывается по Enter или уходу из поля.</summary>
        public string EditDescription
        {
            get => _editDescription;
            set => this.RaiseAndSetIfChanged(ref _editDescription, value ?? string.Empty);
        }

        public void BeginTextEdit() => _isTextEditing = true;

        /// <summary>Записать название и описание одной правкой.</summary>
        public void CommitTextEdit()
        {
            if (!_isTextEditing) return;
            _isTextEditing = false;

            _owner.SetNameAndDescription(Id, _editName, _editDescription);
            SyncTextEdit();
        }

        /// <summary>Вернуть поля к записанному.</summary>
        public void CancelTextEdit()
        {
            _isTextEditing = false;
            SyncTextEdit();
        }

        private void SyncTextEdit()
        {
            EditName = Name;
            EditDescription = Description;
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
            IconBackground = anketa.IconBackground ?? string.Empty;
            IconBorder = anketa.IconBorder ?? string.Empty;
            FieldCount = anketa.Fields.Count;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Icon { get; }
        public string IconColor { get; }
        public string IconBackground { get; }
        public string IconBorder { get; }
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

        private string _iconBackground = string.Empty;
        public string IconBackground { get => _iconBackground; private set => this.RaiseAndSetIfChanged(ref _iconBackground, value); }

        private string _iconBorder = string.Empty;
        public string IconBorder { get => _iconBorder; private set => this.RaiseAndSetIfChanged(ref _iconBorder, value); }

        private bool _isDragSource;
        public bool IsDragSource { get => _isDragSource; set => this.RaiseAndSetIfChanged(ref _isDragSource, value); }

        public void Update(CharacterAnketa anketa)
        {
            Name = anketa.Name;
            Description = anketa.Description;
            Icon = anketa.Icon;
            IconColor = anketa.IconColor;
            IconBackground = anketa.IconBackground ?? string.Empty;
            IconBorder = anketa.IconBorder ?? string.Empty;
            HasLevels = CharacterDetailLevels.HasLevels(anketa);
            _maxDepth = CharacterDetailLevels.MaxOf(anketa);
            RebuildDepthChoices();
        }

        // ── Глубина ──────────────────────────────────────────────────────

        private bool _hasLevels;

        /// <summary>У анкеты есть поля глубже краткого — шаблону есть что задавать.</summary>
        public bool HasLevels
        {
            get => _hasLevels;
            private set => this.RaiseAndSetIfChanged(ref _hasLevels, value);
        }

        private int? _depth;

        /// <summary>Глубина анкеты в шаблоне. Пусто — по важности персонажа.</summary>
        public int? Depth => _depth;

        public void SetDepth(int? depth)
        {
            _depth = depth.HasValue ? CharacterDetailLevels.Clamp(depth.Value) : null;
            this.RaisePropertyChanged(nameof(Depth));
            this.RaisePropertyChanged(nameof(GlyphLevel));
            this.RaisePropertyChanged(nameof(DepthTip));
            this.RaisePropertyChanged(nameof(HasFixedDepth));
            RebuildDepthChoices();
        }

        public bool HasFixedDepth => _depth.HasValue;

        /// <summary>Уровень для значка-полосок: 0 — по важности персонажа (контур).</summary>
        public int GlyphLevel => _depth ?? 0;

        public string DepthTip => (_depth.HasValue
                                      ? "Глубина в шаблоне: " + CharacterDetailLevels.NameOf(_depth.Value).ToLowerInvariant() + ". "
                                      : "Глубина по важности персонажа. ")
                                  + "Щелчок — выбрать. " + CharacterDetailLevels.SymbolHint;

        private int _maxDepth = CharacterDetailLevels.Brief;

        /// <summary>Варианты глубины в меню значка: по важности и уровни до последнего уровня анкеты.</summary>
        public IReadOnlyList<TemplateDepthChoiceViewModel> DepthChoices { get; private set; } =
            Array.Empty<TemplateDepthChoiceViewModel>();

        private void RebuildDepthChoices()
        {
            var choices = new List<TemplateDepthChoiceViewModel>
            {
                new(TemplateId, Id, 0, "По важности персонажа",
                    "Главному — глубоко, второстепенному — подробно, эпизодическому — кратко",
                    !_depth.HasValue)
            };

            foreach (var level in CharacterDetailLevels.All)
                if (level <= _maxDepth)
                    choices.Add(new TemplateDepthChoiceViewModel(TemplateId, Id, level, CharacterDetailLevels.NameOf(level),
                        CharacterDetailLevels.HintOf(level), _depth == level));

            DepthChoices = choices;
            this.RaisePropertyChanged(nameof(DepthChoices));
        }
    }

    /// <summary>Вариант глубины анкеты в шаблоне — строкой меню со значком-полосками.</summary>
    public class TemplateDepthChoiceViewModel
    {
        public TemplateDepthChoiceViewModel(string templateId, string anketaId, int level, string name, string hint, bool isSelected)
        {
            TemplateId = templateId;
            AnketaId = anketaId;
            Level = level;
            Name = name;
            Hint = hint;
            IsSelected = isSelected;
        }

        public string TemplateId { get; }

        public string AnketaId { get; }

        /// <summary>0 — по важности персонажа, 1–3 — уровень.</summary>
        public int Level { get; }

        public string Name { get; }

        public string Hint { get; }

        public bool IsSelected { get; }
    }

    /// <summary>
    /// Раздел вкладки шаблонов — как папка персонажей: шапка с цветом,
    /// названием, подписью, числом шаблонов и стрелкой сворачивания, под ней
    /// блоки. Бывает трёх видов: своя папка (её переименовывают, красят,
    /// двигают вверх и вниз, удаляют), «Мои шаблоны» — свои шаблоны без
    /// папки — и «Встроенные». Два последних не правятся, но вверх и вниз
    /// двигаются, как папки.
    /// </summary>
    public class TemplateFolderViewModel : ReactiveObject
    {
        private readonly CharactersTemplatesViewModel _owner;

        /// <summary>Своя папка.</summary>
        public TemplateFolderViewModel(CharactersTemplatesViewModel owner, CharacterTemplateFolder folder)
            : this(owner, folder.Id, folder.Name)
        {
            IsCustom = true;
            Update(folder);
        }

        /// <summary>Раздел без папки: «Мои шаблоны» или «Встроенные».</summary>
        public TemplateFolderViewModel(CharactersTemplatesViewModel owner, string id, string name)
        {
            _owner = owner;
            Id = id;
            _name = name;
            IsUngrouped = id == TemplateSections.Ungrouped;
            IsBuiltIn = id == TemplateSections.BuiltIn;

            Blocks.CollectionChanged += (_, _) =>
            {
                this.RaisePropertyChanged(nameof(Count));
                this.RaisePropertyChanged(nameof(IsEmpty));
                this.RaisePropertyChanged(nameof(ShowEmptyHint));
            };

            ToggleExpandCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
            SelectOrRenameCommand = ReactiveCommand.Create(() =>
            {
                if (IsSelected && IsCustom) IsRenaming = true;
                else _owner.SelectFolder(this);
            });
            ConfirmRenameCommand = ReactiveCommand.Create(ConfirmRename);
            StartEditCommentCommand = ReactiveCommand.Create(() =>
            {
                if (IsCustom) IsEditingComment = true;
            });
            ConfirmCommentCommand = ReactiveCommand.Create(ConfirmComment);
            MoveUpCommand = ReactiveCommand.Create(() => _owner.MoveFolder(Id, -1));
            MoveDownCommand = ReactiveCommand.Create(() => _owner.MoveFolder(Id, 1));
        }

        /// <summary>Ключ раздела: Id папки или TemplateSections.</summary>
        public string Id { get; }

        public bool IsCustom { get; }
        public bool IsUngrouped { get; }
        public bool IsBuiltIn { get; }

        /// <summary>Раздел принимает перетащенные блоки — все, кроме встроенного.</summary>
        public bool AcceptsBlocks => !IsBuiltIn;

        public ObservableCollection<TemplateBlockViewModel> Blocks { get; } = new();

        public int Count => Blocks.Count;

        public bool IsEmpty => Blocks.Count == 0;

        // ── Шапка ────────────────────────────────────────────────────────

        private string _name;

        /// <summary>
        /// Название. Поле правки пишет сюда по буквам, а в проект название
        /// уходит по Enter или уходу из поля — ConfirmRename.
        /// </summary>
        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value ?? string.Empty);
        }

        private string _savedName = string.Empty;

        private string _comment = string.Empty;

        /// <summary>Серая подпись справа от названия; в проект — по ConfirmComment.</summary>
        public string Comment
        {
            get => _comment;
            set => this.RaiseAndSetIfChanged(ref _comment, value ?? string.Empty);
        }

        private string _savedComment = string.Empty;

        private string _color = "#607D8B";

        /// <summary>Цвет папки — кружок в начале шапки, как у папок персонажей.</summary>
        public string Color
        {
            get => _color;
            set
            {
                if (_color == value) return;
                this.RaiseAndSetIfChanged(ref _color, value ?? string.Empty);
                if (IsCustom && !_updating) _owner.SetFolderColor(Id, _color);
            }
        }

        private bool _updating;

        internal void Update(CharacterTemplateFolder folder)
        {
            _updating = true;
            try
            {
                if (!_isRenaming) Name = folder.Name;
                if (!_isEditingComment) Comment = folder.Comment ?? string.Empty;
                Color = string.IsNullOrWhiteSpace(folder.Color) ? "#607D8B" : folder.Color;
                _savedName = folder.Name;
                _savedComment = folder.Comment ?? string.Empty;
            }
            finally
            {
                _updating = false;
            }
        }

        private bool _isRenaming;
        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                if (value && !IsCustom) return;
                if (value && !_isRenaming) _savedName = _name;
                this.RaiseAndSetIfChanged(ref _isRenaming, value);
            }
        }

        private bool _isEditingComment;
        public bool IsEditingComment
        {
            get => _isEditingComment;
            set
            {
                if (value && !IsCustom) return;
                if (value && !_isEditingComment) _savedComment = _comment;
                this.RaiseAndSetIfChanged(ref _isEditingComment, value);
            }
        }

        public void ConfirmRename()
        {
            if (!_isRenaming) return;
            IsRenaming = false;
            if (string.IsNullOrWhiteSpace(_name)) Name = _savedName;
            if (_name != _savedName) _owner.RenameFolder(Id, _name);
        }

        /// <summary>Esc в поле названия: прежнее название возвращается.</summary>
        public void CancelRename()
        {
            if (!_isRenaming) return;
            Name = _savedName;
            IsRenaming = false;
        }

        public void ConfirmComment()
        {
            if (!_isEditingComment) return;
            IsEditingComment = false;
            if (_comment != _savedComment) _owner.SetFolderComment(Id, _comment);
        }

        public void CancelComment()
        {
            if (!_isEditingComment) return;
            Comment = _savedComment;
            IsEditingComment = false;
        }

        private bool _isSelected;

        /// <summary>Текущая папка: новый шаблон встаёт в неё.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        private bool _isDragOver;

        /// <summary>Над свёрнутой папкой тащат блок — отпущенный ляжет в её конец.</summary>
        public bool IsDragOver
        {
            get => _isDragOver;
            set => this.RaiseAndSetIfChanged(ref _isDragOver, value);
        }

        // ── Сворачивание и поиск ─────────────────────────────────────────

        private bool _isExpanded = true;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                this.RaiseAndSetIfChanged(ref _isExpanded, value);
                _owner.SetFolderCollapsed(Id, !value);
                RaiseVisibility();
            }
        }

        /// <summary>Свёрнутость из сохранённого — без записи обратно.</summary>
        internal void SetExpandedSilently(bool expanded)
        {
            if (_isExpanded == expanded) return;
            _isExpanded = expanded;
            this.RaisePropertyChanged(nameof(IsExpanded));
            RaiseVisibility();
        }

        /// <summary>Блоки видны: раздел развёрнут или идёт поиск — найденное показывается и в свёрнутом.</summary>
        public bool ShowBlocks => _isExpanded || _owner.IsSearching;

        /// <summary>
        /// Раздел виден: без поиска — всегда, при поиске — только если в нём
        /// что-то нашлось.
        /// </summary>
        public bool IsShown => !_owner.IsSearching || Blocks.Any(b => b.IsMatch);

        /// <summary>Пустой развёрнутый раздел показывает подсказку — и это же место, куда бросают блок.</summary>
        public bool ShowEmptyHint => !IsBuiltIn && Blocks.Count == 0 && ShowBlocks;

        public string EmptyHint => IsUngrouped
            ? "Шаблонов без папки нет. «+» сверху создаёт новый, а встроенный можно продублировать — копия встанет сюда"
            : "Папка пуста — перетащите сюда шаблон";

        private bool _canMoveUp;
        public bool CanMoveUp { get => _canMoveUp; private set => this.RaiseAndSetIfChanged(ref _canMoveUp, value); }

        private bool _canMoveDown;
        public bool CanMoveDown { get => _canMoveDown; private set => this.RaiseAndSetIfChanged(ref _canMoveDown, value); }

        internal void RefreshState(bool canMoveUp, bool canMoveDown)
        {
            CanMoveUp = canMoveUp;
            CanMoveDown = canMoveDown;
            RaiseVisibility();
        }

        private void RaiseVisibility()
        {
            this.RaisePropertyChanged(nameof(ShowBlocks));
            this.RaisePropertyChanged(nameof(IsShown));
            this.RaisePropertyChanged(nameof(ShowEmptyHint));
        }

        // ── Команды ──────────────────────────────────────────────────────

        public ReactiveCommand<Unit, Unit> ToggleExpandCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectOrRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmRenameCommand { get; }
        public ReactiveCommand<Unit, Unit> StartEditCommentCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmCommentCommand { get; }
        public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
        public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    }
}
