using Avalonia.Input;
using Avalonia.Threading;
using Newtonsoft.Json;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Modules.Characters.Services;
using Writersword.Modules.Characters.ViewModels.Tabs;

namespace Writersword.Modules.Characters.ViewModels.Anketas
{
    /// <summary>
    /// Редактор анкет. Открытые анкеты — листы-вкладки: их можно править
    /// по нескольку сразу, переставлять и сохранять. У каждого листа своя
    /// история правок: Ctrl+Z на одном листе не трогает другие.
    ///
    /// Анкета — раздел полей карточки персонажа. Здесь задаётся всё, что
    /// потом одинаково у всех персонажей: какие поля, как они разложены по
    /// строкам и колонкам, как подписаны и как в них вводят значение.
    /// </summary>
    public class CharactersAnketasViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersAnketasViewModel>();

        private readonly ICharacterAnketaService _anketaService;
        private readonly ICharacterService _characterService;

        public ObservableCollection<AnketaSheetViewModel> Sheets { get; } = new();

        private AnketaSheetViewModel? _activeSheet;
        public AnketaSheetViewModel? ActiveSheet
        {
            get => _activeSheet;
            set
            {
                if (ReferenceEquals(_activeSheet, value)) return;
                if (_activeSheet != null) _activeSheet.IsActive = false;
                this.RaiseAndSetIfChanged(ref _activeSheet, value);
                if (_activeSheet != null) _activeSheet.IsActive = true;
                this.RaisePropertyChanged(nameof(HasActiveSheet));
                this.RaisePropertyChanged(nameof(HasNoSheets));
                ScheduleDrafts();
            }
        }

        public bool HasActiveSheet => _activeSheet != null;
        public bool HasNoSheets => Sheets.Count == 0;

        /// <summary>Анкета сохранена — открытой карточке персонажа пора перечитать поля.</summary>
        public event Action<string>? AnketaSaved;

        /// <summary>Только что открытый лист новой анкеты: курсор — в её название.</summary>
        public event Action<AnketaSheetViewModel>? NameFocusRequested;

        public CharactersAnketasViewModel(ICharacterAnketaService anketaService, ICharacterService characterService)
        {
            _anketaService = anketaService;
            _characterService = characterService;

            Sheets.CollectionChanged += (_, e) =>
            {
                if (e.OldItems != null)
                    foreach (AnketaSheetViewModel sheet in e.OldItems)
                        sheet.StateChanged -= OnSheetChanged;
                if (e.NewItems != null)
                    foreach (AnketaSheetViewModel sheet in e.NewItems)
                        sheet.StateChanged += OnSheetChanged;

                this.RaisePropertyChanged(nameof(HasNoSheets));
                this.RaisePropertyChanged(nameof(SheetCountCaption));
                ScheduleDrafts();
            };
        }

        /// <summary>Сколько листов открыто — у переключателя вкладки.</summary>
        public string SheetCountCaption => Sheets.Count == 0 ? string.Empty : Sheets.Count.ToString();

        // ── Листы ────────────────────────────────────────────────────────

        /// <summary>
        /// Свести листы к сохранённым анкетам: удалённые где-то ещё закрываются.
        /// Новые, ещё не сохранённые листы в проекте и не должны быть.
        /// </summary>
        public void Refresh()
        {
            foreach (var sheet in Sheets.ToList())
                if (!sheet.IsNew && _anketaService.GetById(sheet.AnketaId) == null)
                    RemoveSheet(sheet);

            if (_isPickerOpen) RefreshPicker();
        }

        /// <summary>Открыть анкету на листе — так её открывают из шаблонов и карточки.</summary>
        public void Select(string anketaId) => OpenAnketa(anketaId);

        public AnketaSheetViewModel? OpenAnketa(string anketaId, bool focusName = false)
        {
            var existing = Sheets.FirstOrDefault(s => s.AnketaId == anketaId);
            if (existing != null)
            {
                ActiveSheet = existing;
                return existing;
            }

            var anketa = _anketaService.GetById(anketaId);
            if (anketa == null) return null;

            var sheet = new AnketaSheetViewModel(anketa, _anketaService);
            var index = _activeSheet == null ? Sheets.Count : Sheets.IndexOf(_activeSheet) + 1;
            Sheets.Insert(Math.Clamp(index, 0, Sheets.Count), sheet);
            ActiveSheet = sheet;

            if (focusName) NameFocusRequested?.Invoke(sheet);
            return sheet;
        }

        private void RemoveSheet(AnketaSheetViewModel sheet)
        {
            var index = Sheets.IndexOf(sheet);
            if (index < 0) return;

            Sheets.RemoveAt(index);
            if (ReferenceEquals(_activeSheet, sheet))
                ActiveSheet = Sheets.Count == 0 ? null : Sheets[Math.Min(index, Sheets.Count - 1)];
        }

        /// <summary>Закрыть лист. Несохранённое теряется — спрашивает об этом вью.</summary>
        public void CloseSheet(AnketaSheetViewModel sheet) => RemoveSheet(sheet);

        /// <summary>Переставить лист: index — место до удаления со старого.</summary>
        public void MoveSheet(AnketaSheetViewModel sheet, int index)
        {
            var from = Sheets.IndexOf(sheet);
            if (from < 0) return;

            if (index > from) index--;
            index = Math.Clamp(index, 0, Sheets.Count - 1);
            if (index == from) return;

            Sheets.Move(from, index);
        }

        // ── Анкеты ───────────────────────────────────────────────────────

        /// <summary>
        /// Новая анкета — лист без записи в проекте. В проект она попадёт по
        /// «Сохранить»; до этого живёт черновиком.
        /// </summary>
        public void CreateAnketa()
        {
            var draft = new CharacterAnketa
            {
                Id = Guid.NewGuid().ToString(),
                Name = NextName("Новая анкета"),
                IsBuiltIn = false,
                CreatedAt = DateTime.UtcNow
            };

            OpenNew(draft, focusName: true);
        }

        /// <summary>
        /// Копия анкеты — своя, её можно править. Так правят встроенные:
        /// сами они неизменны, чтобы у всех проектов была одна и та же основа.
        /// Копия делается с того, что на листе, а не с сохранённого, и, как
        /// новая анкета, попадает в проект только по «Сохранить».
        /// </summary>
        public void DuplicateAnketa(AnketaSheetViewModel sheet)
        {
            var draft = sheet.BuildAnketa(forSave: false);
            draft.Id = Guid.NewGuid().ToString();
            draft.Name = NextName(sheet.Name + " (копия)");
            draft.IsBuiltIn = false;
            draft.CreatedAt = DateTime.UtcNow;

            OpenNew(draft, focusName: true);
        }

        /// <summary>Лист с анкетой, которой в проекте ещё нет, — за активным.</summary>
        private AnketaSheetViewModel OpenNew(CharacterAnketa draft, bool focusName)
        {
            var sheet = new AnketaSheetViewModel(draft, _anketaService, isNew: true);
            var index = _activeSheet == null ? Sheets.Count : Sheets.IndexOf(_activeSheet) + 1;
            Sheets.Insert(Math.Clamp(index, 0, Sheets.Count), sheet);
            ActiveSheet = sheet;

            _logger.Debug("Anketa sheet opened unsaved: '{Name}'", draft.Name);
            if (focusName) NameFocusRequested?.Invoke(sheet);
            return sheet;
        }

        /// <summary>
        /// Удалить свою анкету. Персонажи, к которым она подключена, значения
        /// сохраняют — удаляется определение, а не написанное.
        /// </summary>
        public void DeleteAnketa(AnketaSheetViewModel sheet)
        {
            if (sheet.IsNew)
            {
                RemoveSheet(sheet);
                return;
            }

            var anketa = _anketaService.GetById(sheet.AnketaId);
            if (anketa == null || anketa.IsBuiltIn) return;

            _anketaService.Delete(sheet.AnketaId);
            RemoveSheet(sheet);
            AnketaSaved?.Invoke(sheet.AnketaId);
        }

        /// <summary>
        /// Принять анкету из файла. Идентификатор выдаётся новый, признак
        /// встроенной снимается: чужая анкета становится своей, её можно
        /// править. Идентификаторы полей сохраняются как есть — именно они
        /// делают карточки сравнимыми с чужими.
        /// </summary>
        public void ImportAnketa(CharacterAnketa imported)
        {
            if (imported == null) return;

            imported.Id = Guid.NewGuid().ToString();
            imported.Name = string.IsNullOrWhiteSpace(imported.Name)
                ? NextName("Анкета без названия")
                : NextName(imported.Name);
            imported.IsBuiltIn = false;
            imported.CreatedAt = DateTime.UtcNow;
            imported.Fields ??= new List<CharacterAnketaField>();
            imported.Layout ??= new List<CharacterAnketaRow>();
            imported.Assets ??= new List<CharacterAnketaAsset>();

            _logger.Debug("Anketa imported: '{Name}', {Count} fields", imported.Name, imported.Fields.Count);
            OpenNew(imported, focusName: false);
        }

        /// <summary>
        /// Анкета для файла — такая, как на листе: в файл уходит то, что
        /// видно, вместе со своими значками.
        /// </summary>
        public CharacterAnketa BuildForExport(AnketaSheetViewModel sheet) => sheet.BuildAnketa(forSave: true);

        public void Save(AnketaSheetViewModel? sheet)
        {
            if (sheet == null || sheet.IsReadOnly) return;

            if (sheet.IsNew)
            {
                // Первое сохранение: анкета впервые встаёт в проект.
                var created = _anketaService.Create(string.IsNullOrWhiteSpace(sheet.Name)
                    ? "Анкета без названия"
                    : sheet.Name.Trim());
                sheet.MarkCreated(created.Id, created.CreatedAt);
            }
            else if (_anketaService.GetById(sheet.AnketaId) == null)
            {
                return;
            }

            var anketa = sheet.BuildAnketa(forSave: true);
            _anketaService.Update(anketa);
            _characterService.SyncAnketa(anketa);
            sheet.MarkSaved();

            _logger.Debug("Anketa saved: {Id}, {Count} fields", anketa.Id, anketa.Fields.Count);
            AnketaSaved?.Invoke(anketa.Id);
        }

        public void SaveAll()
        {
            foreach (var sheet in Sheets.Where(s => s.IsDirty).ToList())
                Save(sheet);
        }

        public bool HasUnsaved => Sheets.Any(s => s.IsDirty);

        private string NextName(string baseName)
        {
            var names = new HashSet<string>(_anketaService.GetAll().Select(a => a.Name), StringComparer.CurrentCultureIgnoreCase);
            foreach (var sheet in Sheets) names.Add(sheet.Name);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; ; i++)
            {
                var candidate = baseName + " " + i;
                if (!names.Contains(candidate)) return candidate;
            }
        }

        // ── Черновики ────────────────────────────────────────────────────
        //
        // Открытые листы и их несохранённое состояние пишутся в данные
        // программы (AnketaDraftStore), по записи на проект. Правки на листе
        // записываются с короткой задержкой, перестановка и закрытие листов —
        // сразу. В анкеты проекта всё это попадает только по «Сохранить».

        private static readonly TimeSpan DraftWriteDelay = TimeSpan.FromMilliseconds(400);

        private string? _draftKey;
        private bool _draftsAttached;
        private bool _suspendDrafts;
        private DispatcherTimer? _draftTimer;

        private void OnSheetChanged() => ScheduleDrafts();

        private void ScheduleDrafts()
        {
            if (_suspendDrafts || _draftKey == null) return;

            if (_draftTimer == null)
            {
                _draftTimer = new DispatcherTimer { Interval = DraftWriteDelay };
                _draftTimer.Tick += (_, _) =>
                {
                    _draftTimer?.Stop();
                    WriteDrafts();
                };
            }

            _draftTimer.Stop();
            _draftTimer.Start();
        }

        /// <summary>Дописать отложенную запись черновиков — при уходе с вкладки и закрытии модуля.</summary>
        public void FlushDrafts()
        {
            _draftTimer?.Stop();
            WriteDrafts();
        }

        private void WriteDrafts()
        {
            if (_suspendDrafts || _draftKey == null) return;

            try
            {
                var state = new AnketaDraftStore.State
                {
                    ActiveId = _activeSheet?.AnketaId,
                    Sheets = Sheets.Select(s => new AnketaDraftStore.SheetEntry
                    {
                        AnketaId = s.AnketaId,
                        IsNew = s.IsNew,
                        Draft = s.IsDirty ? s.CurrentJson : null
                    }).ToList()
                };

                AnketaDraftStore.Save(_draftKey, state);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Anketa drafts write failed");
            }
        }

        /// <summary>
        /// Отвязать редактор от проекта: черновики дописываются, листы
        /// закрываются. Зовётся перед загрузкой данных другого проекта.
        /// </summary>
        public void DetachDrafts()
        {
            if (_draftsAttached) FlushDrafts();

            _suspendDrafts = true;
            try
            {
                foreach (var sheet in Sheets.ToList())
                    RemoveSheet(sheet);
            }
            finally
            {
                _suspendDrafts = false;
            }

            _draftKey = null;
            _draftsAttached = false;
        }

        /// <summary>
        /// Привязать редактор к проекту и вернуть его листы: сохранённые
        /// анкеты — с их несохранёнными правками, новые — из черновика.
        /// Ключ null (сравнение версий) — без черновиков.
        /// </summary>
        public void AttachDrafts(string? key)
        {
            if (_draftsAttached && string.Equals(_draftKey, key, StringComparison.Ordinal)) return;

            DetachDrafts();
            _draftsAttached = true;
            _draftKey = key;
            if (key == null) return;

            AnketaDraftStore.State? state;
            try
            {
                state = AnketaDraftStore.Get(key);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Anketa drafts read failed");
                return;
            }

            if (state == null) return;

            _suspendDrafts = true;
            try
            {
                foreach (var entry in state.Sheets)
                {
                    try
                    {
                        RestoreSheet(entry);
                    }
                    catch (Exception ex)
                    {
                        // Испорченный черновик одного листа не мешает остальным.
                        _logger.Warning(ex, "Anketa draft restore failed: {Id}", entry.AnketaId);
                    }
                }

                ActiveSheet = Sheets.FirstOrDefault(s => s.AnketaId == state.ActiveId) ?? Sheets.FirstOrDefault();
            }
            finally
            {
                _suspendDrafts = false;
            }

            _logger.Debug("Anketa sheets restored: {Count}", Sheets.Count);
        }

        private void RestoreSheet(AnketaDraftStore.SheetEntry entry)
        {
            if (Sheets.Any(s => s.AnketaId == entry.AnketaId)) return;

            if (entry.IsNew)
            {
                if (string.IsNullOrWhiteSpace(entry.Draft)) return;

                var draft = JsonConvert.DeserializeObject<CharacterAnketa>(entry.Draft);
                if (draft == null) return;

                draft.Id = string.IsNullOrWhiteSpace(entry.AnketaId) ? Guid.NewGuid().ToString() : entry.AnketaId;
                draft.IsBuiltIn = false;
                draft.Fields ??= new List<CharacterAnketaField>();
                draft.Layout ??= new List<CharacterAnketaRow>();
                draft.Assets ??= new List<CharacterAnketaAsset>();
                Sheets.Add(new AnketaSheetViewModel(draft, _anketaService, isNew: true));
                return;
            }

            var anketa = _anketaService.GetById(entry.AnketaId);
            if (anketa == null) return;

            var sheet = new AnketaSheetViewModel(anketa, _anketaService);
            if (!string.IsNullOrWhiteSpace(entry.Draft) && sheet.IsEditable)
                sheet.LoadDraft(entry.Draft);
            Sheets.Add(sheet);
        }

        // ── Клавиши ──────────────────────────────────────────────────────

        /// <summary>
        /// Отмена, возврат и сохранение — у активного листа. Ctrl+Z отменяет
        /// только правки этого листа: у каждого своя история.
        /// </summary>
        public bool HandleKey(Key key, KeyModifiers modifiers)
        {
            if (_activeSheet is not { } sheet) return false;

            var ctrl = modifiers == KeyModifiers.Control;
            var ctrlShift = modifiers == (KeyModifiers.Control | KeyModifiers.Shift);

            if (ctrl && key == Key.Z) return sheet.Undo();
            if ((ctrl && key == Key.Y) || (ctrlShift && key == Key.Z)) return sheet.Redo();

            if (ctrl && key == Key.S)
            {
                Save(sheet);
                return true;
            }

            if (ctrlShift && key == Key.S)
            {
                SaveAll();
                return true;
            }

            return false;
        }

        // ── Открытие анкеты ──────────────────────────────────────────────

        public ObservableCollection<AnketaPickItemViewModel> PickerItems { get; } = new();

        private string _pickerQuery = string.Empty;

        /// <summary>Поиск в меню «+»: по названию, описанию и полям.</summary>
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

        private bool _isPickerOpen;

        public void OpenPicker()
        {
            _isPickerOpen = true;
            _pickerQuery = string.Empty;
            this.RaisePropertyChanged(nameof(PickerQuery));
            RefreshPicker();
        }

        public void ClosePicker() => _isPickerOpen = false;

        public void RefreshPicker()
        {
            PickerItems.Clear();

            var words = _pickerQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var anketa in _anketaService.GetAll()
                         .OrderBy(a => a.IsBuiltIn)
                         .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var text = anketa.Name + " " + anketa.Description + " " +
                           string.Join(" ", anketa.Fields.Select(f => f.Name));
                if (words.Any(w => !text.Contains(w, StringComparison.CurrentCultureIgnoreCase))) continue;

                PickerItems.Add(new AnketaPickItemViewModel(anketa, Sheets.Any(s => s.AnketaId == anketa.Id)));
            }
        }
    }

    /// <summary>Анкета в меню открытия листа.</summary>
    public class AnketaPickItemViewModel
    {
        public AnketaPickItemViewModel(CharacterAnketa anketa, bool isOpen)
        {
            Id = anketa.Id;
            Name = anketa.Name;
            Description = anketa.Description;
            Icon = anketa.Icon;
            IconColor = anketa.IconColor;
            IsBuiltIn = anketa.IsBuiltIn;
            FieldCount = anketa.Fields.Count;
            IsOpen = isOpen;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Icon { get; }
        public string IconColor { get; }
        public bool IsBuiltIn { get; }
        public int FieldCount { get; }
        public bool IsOpen { get; }
    }

    /// <summary>
    /// Лист редактора — одна анкета. Правится черновик; в анкету проекта он
    /// уходит по «Сохранить».
    ///
    /// История правок — снимки черновика: перед каждой правкой прежнее
    /// состояние уходит в стопку отмены. Набор текста в одном поле копится в
    /// одну запись, иначе Ctrl+Z стирал бы по букве.
    /// </summary>
    public class AnketaSheetViewModel : ReactiveObject
    {
        private readonly ICharacterAnketaService _service;

        private CharacterAnketa _meta = new();
        private readonly Dictionary<string, AnketaFieldEditorViewModel> _fields = new(StringComparer.Ordinal);
        private List<CharacterAnketaAsset> _assets = new();

        // Скопированный вид поля. Общий для всех листов: вид берут на одном
        // листе и переносят на поля другого.
        private static AnketaDisplaySnapshot? _displayClipboard;

        public AnketaSheetViewModel(CharacterAnketa anketa, ICharacterAnketaService service, bool isNew = false)
        {
            _service = service;

            var draft = Clone(anketa);
            CharacterAnketaLayout.Normalize(draft);

            AnketaId = draft.Id;
            IsReadOnly = draft.IsBuiltIn;
            IsNew = isNew && !draft.IsBuiltIn;

            Load(draft);

            _current = Serialize();
            _saved = _current;
        }

        private static CharacterAnketa Clone(CharacterAnketa anketa) =>
            JsonConvert.DeserializeObject<CharacterAnketa>(JsonConvert.SerializeObject(anketa))!;

        public string AnketaId { get; private set; }

        /// <summary>Анкеты ещё нет в проекте: она появится там по первому «Сохранить».</summary>
        public bool IsNew { get; private set; }

        /// <summary>Состояние листа меняется: правка, отмена, сохранение.</summary>
        public event Action? StateChanged;

        /// <summary>Нынешнее состояние листа — для черновика.</summary>
        internal string CurrentJson => _current;

        /// <summary>
        /// Первое сохранение новой анкеты: лист получает её место в проекте.
        /// Временный идентификатор уходит и из истории — отмена не должна
        /// вернуть лист к анкете, которой в проекте нет.
        /// </summary>
        internal void MarkCreated(string id, DateTime createdAt)
        {
            AnketaId = id;
            _meta.Id = id;
            _meta.CreatedAt = createdAt;
            IsNew = false;
            this.RaisePropertyChanged(nameof(AnketaId));
            this.RaisePropertyChanged(nameof(IsNew));
        }

        /// <summary>
        /// Несохранённое состояние из черновика. Оно не попадает в историю:
        /// отменять нечего, но лист отмечен несохранённым.
        /// </summary>
        internal void LoadDraft(string json)
        {
            if (IsReadOnly) return;

            var draft = JsonConvert.DeserializeObject<CharacterAnketa>(json);
            if (draft == null) return;

            BindToSheet(draft);

            _restoring = true;
            try
            {
                SelectedField = null;
                SelectedRow = null;
                CharacterAnketaLayout.Normalize(draft);
                Load(draft);
            }
            finally
            {
                _restoring = false;
            }

            _current = Serialize();
            _currentSelected = null;
            _lastMergeKey = null;
            RaiseHistory();
        }

        /// <summary>Состояние из истории или черновика — всегда этой же анкеты.</summary>
        private void BindToSheet(CharacterAnketa draft)
        {
            draft.Id = AnketaId;
            draft.IsBuiltIn = _meta.IsBuiltIn;
            draft.CreatedAt = _meta.CreatedAt;
            draft.ProjectTypeTags ??= _meta.ProjectTypeTags.ToList();
            draft.Fields ??= new List<CharacterAnketaField>();
            draft.Layout ??= new List<CharacterAnketaRow>();
            draft.Assets ??= new List<CharacterAnketaAsset>();
        }

        /// <summary>Встроенная анкета: смотреть можно, править — только копию.</summary>
        public bool IsReadOnly { get; }
        public bool IsEditable => !IsReadOnly;

        private bool _isActive;
        public bool IsActive { get => _isActive; set => this.RaiseAndSetIfChanged(ref _isActive, value); }

        private bool _isDragSource;
        public bool IsDragSource { get => _isDragSource; set => this.RaiseAndSetIfChanged(ref _isDragSource, value); }

        // ── Название и значок ────────────────────────────────────────────

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                this.RaiseAndSetIfChanged(ref _name, value ?? string.Empty);
                RecordChange("meta:name");
            }
        }

        private string _description = string.Empty;
        public string Description
        {
            get => _description;
            set
            {
                if (_description == value) return;
                this.RaiseAndSetIfChanged(ref _description, value ?? string.Empty);
                RecordChange("meta:description");
            }
        }

        private string _icon = CharacterAnketaIcons.Default;
        public string Icon { get => _icon; private set => this.RaiseAndSetIfChanged(ref _icon, value); }

        private string _iconColor = CharacterAnketaIcons.DefaultColor;
        public string IconColor { get => _iconColor; private set => this.RaiseAndSetIfChanged(ref _iconColor, value); }

        public ObservableCollection<AnketaIconOptionViewModel> IconOptions { get; } = new();
        public ObservableCollection<AnketaIconColorOptionViewModel> IconColorOptions { get; } = new();

        /// <summary>Наборы для выбора значка анкеты строятся, когда открывают выбор.</summary>
        public void PrepareIconOptions()
        {
            IconOptions.Clear();
            IconColorOptions.Clear();

            foreach (var entry in CharacterAnketaIcons.All)
                IconOptions.Add(new AnketaIconOptionViewModel(entry.Key, entry.Label, entry.Key == Icon, SetIcon) { IconColor = IconColor });

            foreach (var hex in CharacterAnketaIcons.Colors)
                IconColorOptions.Add(new AnketaIconColorOptionViewModel(hex,
                    string.Equals(hex, IconColor, StringComparison.OrdinalIgnoreCase), SetIconColor) { Icon = Icon });
        }

        private void SetIcon(string key)
        {
            if (IsReadOnly || Icon == key) return;
            Icon = key;
            foreach (var option in IconOptions) option.SetSelectedSilently(option.Key == key);
            foreach (var option in IconColorOptions) option.Icon = key;
            RecordChange(null);
        }

        private void SetIconColor(string hex)
        {
            if (IsReadOnly || string.Equals(IconColor, hex, StringComparison.OrdinalIgnoreCase)) return;
            IconColor = hex;
            foreach (var option in IconColorOptions)
                option.SetSelectedSilently(string.Equals(option.Hex, hex, StringComparison.OrdinalIgnoreCase));
            foreach (var option in IconOptions) option.IconColor = hex;
            RecordChange(null);
        }

        // ── Поля и раскладка ─────────────────────────────────────────────

        public ObservableCollection<AnketaLayoutRowViewModel> Rows { get; } = new();

        public bool HasRows => Rows.Count > 0;

        internal AnketaFieldEditorViewModel? FieldByKey(string? key) =>
            !string.IsNullOrEmpty(key) && _fields.TryGetValue(key, out var field) ? field : null;

        private AnketaFieldEditorViewModel CreateEditor(CharacterAnketaField field)
        {
            AnketaFieldEditorViewModel? editor = null;
            editor = new AnketaFieldEditorViewModel(field, _service, () => OnFieldEdited(editor!)) { Owner = this };
            _fields[field.Key] = editor;
            return editor;
        }

        private void Load(CharacterAnketa draft)
        {
            _meta = draft;

            _name = draft.Name;
            _description = draft.Description;
            Icon = string.IsNullOrWhiteSpace(draft.Icon) ? CharacterAnketaIcons.Default : draft.Icon;
            IconColor = string.IsNullOrWhiteSpace(draft.IconColor) ? CharacterAnketaIcons.DefaultColor : draft.IconColor;
            this.RaisePropertyChanged(nameof(Name));
            this.RaisePropertyChanged(nameof(Description));

            _assets = (draft.Assets ?? new List<CharacterAnketaAsset>()).ToList();

            _fields.Clear();
            foreach (var field in draft.Fields)
                CreateEditor(field);

            Rows.Clear();
            foreach (var row in draft.Layout)
                Rows.Add(new AnketaLayoutRowViewModel(this, row));

            RebuildGlyphChoices();
            this.RaisePropertyChanged(nameof(HasRows));
        }

        private AnketaFieldEditorViewModel? _selectedField;

        /// <summary>Выбранное поле — его настройки в панели справа.</summary>
        public AnketaFieldEditorViewModel? SelectedField
        {
            get => _selectedField;
            set
            {
                if (ReferenceEquals(_selectedField, value)) return;
                if (_selectedField != null) _selectedField.IsSelected = false;
                this.RaiseAndSetIfChanged(ref _selectedField, value);
                if (_selectedField != null)
                {
                    _selectedField.IsSelected = true;
                    SelectedRow = RowOf(_selectedField);
                }
                this.RaisePropertyChanged(nameof(HasSelectedField));
                this.RaisePropertyChanged(nameof(HasNoSelectedField));
            }
        }

        public bool HasSelectedField => _selectedField != null;
        public bool HasNoSelectedField => _selectedField == null;

        private AnketaLayoutRowViewModel? _selectedRow;

        /// <summary>Строка, с которой работают кнопки раскладки: новое встаёт после неё.</summary>
        public AnketaLayoutRowViewModel? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (ReferenceEquals(_selectedRow, value)) return;
                if (_selectedRow != null) _selectedRow.IsSelected = false;
                this.RaiseAndSetIfChanged(ref _selectedRow, value);
                if (_selectedRow != null) _selectedRow.IsSelected = true;
            }
        }

        private AnketaLayoutRowViewModel? RowOf(AnketaFieldEditorViewModel field) =>
            Rows.FirstOrDefault(r => r.Cells.Any(c => ReferenceEquals(c.Field, field)));

        private AnketaLayoutCellViewModel? CellOf(AnketaFieldEditorViewModel field) =>
            Rows.SelectMany(r => r.Cells).FirstOrDefault(c => ReferenceEquals(c.Field, field));

        private string NewKey()
        {
            string key;
            do key = "f_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            while (_fields.ContainsKey(key));
            return key;
        }

        /// <summary>Индекс, после которого встаёт новое: за выбранной строкой, иначе в конец.</summary>
        private int InsertIndex()
        {
            var row = _selectedRow ?? (_selectedField != null ? RowOf(_selectedField) : null);
            var index = row == null ? -1 : Rows.IndexOf(row);
            return index < 0 ? Rows.Count : index + 1;
        }

        private static CharacterAnketaField NewField(CharacterParameterType type)
        {
            var field = new CharacterAnketaField
            {
                Name = AnketaFieldEditorViewModel.TypeLabelOf(type),
                Type = type
            };

            switch (type)
            {
                case CharacterParameterType.Numeric:
                    field.DefaultMinValue = 0;
                    field.DefaultMaxValue = 5;
                    field.Step = 1;
                    field.Display = CharacterFieldDisplay.Balls;
                    break;
                case CharacterParameterType.StateList:
                case CharacterParameterType.MultiChoice:
                    field.StatesRaw = "Первый, Второй, Третий";
                    break;
                case CharacterParameterType.Color:
                    field.Palette = new List<string> { "#5B7FA8", "#6B8F4E", "#7A5236", "#9A9A9A", "#3A2A20" };
                    break;
                case CharacterParameterType.Number:
                    field.Step = 1;
                    break;
            }

            return field;
        }

        /// <summary>
        /// Новое поле — в пустую ячейку выбранной строки, а если её нет — новой
        /// строкой за выбранной.
        /// </summary>
        public AnketaFieldEditorViewModel? AddField(CharacterParameterType type)
        {
            if (IsReadOnly) return null;

            var field = NewField(type);
            field.Key = NewKey();
            var editor = CreateEditor(field);

            var target = _selectedRow is { IsFields: true } row
                ? row.Cells.FirstOrDefault(c => !c.HasField)
                : null;

            if (target == null)
            {
                var newRow = new AnketaLayoutRowViewModel(this, new CharacterAnketaRow { Cells = new List<string> { string.Empty } });
                Rows.Insert(InsertIndex(), newRow);
                target = newRow.Cells[0];
            }

            target.Field = editor;
            SelectedField = editor;
            this.RaisePropertyChanged(nameof(HasRows));
            RecordChange(null);
            return editor;
        }

        /// <summary>Пустая строка на одну, две или три ячейки — за выбранной.</summary>
        public void AddRow(int columns)
        {
            if (IsReadOnly) return;

            columns = Math.Clamp(columns, 1, CharacterAnketaLayout.MaxColumns);
            var row = new AnketaLayoutRowViewModel(this, new CharacterAnketaRow
            {
                Cells = Enumerable.Repeat(string.Empty, columns).ToList()
            });
            Rows.Insert(InsertIndex(), row);
            SelectedField = null;
            SelectedRow = row;
            this.RaisePropertyChanged(nameof(HasRows));
            RecordChange(null);
        }

        /// <summary>Подзаголовок группы — за выбранной строкой.</summary>
        public AnketaLayoutRowViewModel? AddGroup()
        {
            if (IsReadOnly) return null;

            var row = new AnketaLayoutRowViewModel(this, new CharacterAnketaRow
            {
                Kind = CharacterAnketaRowKind.Group,
                Title = "Новая группа"
            });
            Rows.Insert(InsertIndex(), row);
            SelectedField = null;
            SelectedRow = row;
            this.RaisePropertyChanged(nameof(HasRows));
            RecordChange(null);
            return row;
        }

        /// <summary>
        /// Сменить число колонок строки. Поля сохраняют порядок; не
        /// поместившиеся уходят новыми строками под ней — ничего не теряется.
        /// </summary>
        public void SetColumns(AnketaLayoutRowViewModel row, int columns)
        {
            if (IsReadOnly || !row.IsFields) return;

            columns = Math.Clamp(columns, 1, CharacterAnketaLayout.MaxColumns);
            if (row.Cells.Count == columns) return;

            if (columns > row.Cells.Count)
            {
                while (row.Cells.Count < columns) row.Cells.Add(new AnketaLayoutCellViewModel(row, null));
            }
            else
            {
                var fields = row.Cells.Where(c => c.HasField).Select(c => c.Field!).ToList();
                var keep = fields.Take(columns).ToList();
                var rest = fields.Skip(columns).ToList();

                row.Cells.Clear();
                foreach (var field in keep) row.Cells.Add(new AnketaLayoutCellViewModel(row, field));
                while (row.Cells.Count < columns) row.Cells.Add(new AnketaLayoutCellViewModel(row, null));

                var index = Rows.IndexOf(row) + 1;
                for (int i = 0; i < rest.Count; i += columns)
                {
                    var extra = new AnketaLayoutRowViewModel(this, new CharacterAnketaRow());
                    extra.Cells.Clear();
                    foreach (var field in rest.Skip(i).Take(columns))
                        extra.Cells.Add(new AnketaLayoutCellViewModel(extra, field));
                    while (extra.Cells.Count < columns) extra.Cells.Add(new AnketaLayoutCellViewModel(extra, null));
                    Rows.Insert(index++, extra);
                }
            }

            row.RaiseColumns();
            RecordChange(null);
        }

        /// <summary>Удалить строку вместе с её полями. Значения у персонажей останутся.</summary>
        public void DeleteRow(AnketaLayoutRowViewModel row)
        {
            if (IsReadOnly) return;

            foreach (var cell in row.Cells.Where(c => c.HasField))
            {
                if (ReferenceEquals(_selectedField, cell.Field)) SelectedField = null;
                _fields.Remove(cell.Field!.Key);
            }

            if (ReferenceEquals(_selectedRow, row)) SelectedRow = null;
            Rows.Remove(row);
            this.RaisePropertyChanged(nameof(HasRows));
            RecordChange(null);
        }

        /// <summary>Переставить строку: index — место до удаления со старого.</summary>
        public void MoveRow(AnketaLayoutRowViewModel row, int index)
        {
            if (IsReadOnly) return;

            var from = Rows.IndexOf(row);
            if (from < 0) return;

            if (index > from) index--;
            index = Math.Clamp(index, 0, Rows.Count - 1);
            if (index == from) return;

            Rows.Move(from, index);
            RecordChange(null);
        }

        /// <summary>Перенести поле в другую ячейку; поле, что там стояло, встаёт на его место.</summary>
        public void MoveField(AnketaLayoutCellViewModel from, AnketaLayoutCellViewModel to)
        {
            if (IsReadOnly || ReferenceEquals(from, to) || !from.HasField) return;

            var moving = from.Field;
            from.Field = to.Field;
            to.Field = moving;
            SelectedField = moving;
            RecordChange(null);
        }

        public void DeleteField(AnketaFieldEditorViewModel field)
        {
            if (IsReadOnly) return;

            var cell = CellOf(field);
            if (cell == null) return;

            cell.Field = null;
            _fields.Remove(field.Key);
            if (ReferenceEquals(_selectedField, field)) SelectedField = null;
            RecordChange(null);
        }

        /// <summary>Копия поля со всеми настройками — в свободную ячейку той же строки или строкой ниже.</summary>
        public AnketaFieldEditorViewModel? DuplicateField(AnketaFieldEditorViewModel source)
        {
            if (IsReadOnly) return null;

            var row = RowOf(source);
            if (row == null) return null;

            var field = source.Snapshot();
            field.Key = NewKey();
            field.FieldId = string.Empty;
            field.Name = string.IsNullOrWhiteSpace(field.Name) ? string.Empty : field.Name + " (копия)";
            var editor = CreateEditor(field);

            var target = row.Cells.FirstOrDefault(c => !c.HasField);
            if (target == null)
            {
                var newRow = new AnketaLayoutRowViewModel(this, new CharacterAnketaRow { Cells = new List<string> { string.Empty } });
                Rows.Insert(Rows.IndexOf(row) + 1, newRow);
                target = newRow.Cells[0];
            }

            target.Field = editor;
            SelectedField = editor;
            RecordChange(null);
            return editor;
        }

        internal void OnGroupTitleEdited(AnketaLayoutRowViewModel row) => RecordChange("group:" + row.Id);

        // ── Вид: копировать и вставить ───────────────────────────────────

        public bool CanPasteDisplay => _displayClipboard.HasValue && !IsReadOnly;

        public void CopyDisplay(AnketaFieldEditorViewModel field)
        {
            _displayClipboard = field.CaptureDisplay();
            this.RaisePropertyChanged(nameof(CanPasteDisplay));
        }

        public void PasteDisplay(AnketaFieldEditorViewModel field)
        {
            if (IsReadOnly || _displayClipboard is not { } snapshot) return;
            field.ApplyDisplayFrom(snapshot);
        }

        /// <summary>Вставить скопированный вид во все поля группы — от подзаголовка над полем до следующего.</summary>
        public void PasteDisplayToGroup(AnketaFieldEditorViewModel field)
        {
            if (IsReadOnly || _displayClipboard is not { } snapshot) return;

            var row = RowOf(field);
            if (row == null) return;

            var index = Rows.IndexOf(row);
            var start = index;
            while (start > 0 && !Rows[start - 1].IsGroup) start--;
            var end = index;
            while (end < Rows.Count - 1 && !Rows[end + 1].IsGroup) end++;

            _batch = true;
            try
            {
                for (int i = start; i <= end; i++)
                    foreach (var cell in Rows[i].Cells.Where(c => c.HasField))
                        cell.Field!.ApplyDisplayFrom(snapshot);
            }
            finally
            {
                _batch = false;
            }

            RecordChange(null);
        }

        /// <summary>Скопированный вид виден и на только что открытом листе.</summary>
        public void RefreshClipboard() => this.RaisePropertyChanged(nameof(CanPasteDisplay));

        // ── Значки ───────────────────────────────────────────────────────

        /// <summary>Значки для подписи и оценки: встроенные и свои значки анкеты.</summary>
        public ObservableCollection<AnketaGlyphOptionViewModel> GlyphChoices { get; } = new();

        private void RebuildGlyphChoices()
        {
            GlyphChoices.Clear();
            foreach (var asset in _assets)
                GlyphChoices.Add(new AnketaGlyphOptionViewModel(asset.Glyph, asset.Name, asset.Id));
            foreach (var entry in CharacterAnketaIcons.All)
                GlyphChoices.Add(new AnketaGlyphOptionViewModel(entry.Key, entry.Label, null));
        }

        /// <summary>Свой значок из файла: ложится в анкету и уезжает вместе с ней.</summary>
        public string? AddAsset(string glyph, string name)
        {
            if (IsReadOnly || string.IsNullOrWhiteSpace(glyph)) return null;

            var existing = _assets.FirstOrDefault(a => a.Glyph == glyph);
            if (existing != null) return existing.Glyph;

            _assets.Add(new CharacterAnketaAsset
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Свой значок" : name,
                Glyph = glyph
            });
            RebuildGlyphChoices();
            RecordChange(null);
            return glyph;
        }

        /// <summary>Убрать свой значок из анкеты. Поля, где он стоял, остаются со значком: он записан в них самих.</summary>
        public void RemoveAsset(string assetId)
        {
            if (IsReadOnly) return;
            if (_assets.RemoveAll(a => a.Id == assetId) == 0) return;
            RebuildGlyphChoices();
            RecordChange(null);
        }

        // ── Сборка ───────────────────────────────────────────────────────

        /// <summary>
        /// Анкета с листа. Порядок и группы полей выводятся из раскладки.
        /// При сохранении новым полям выдаются идентификаторы из имени — так
        /// одноимённые поля разных анкет сравниваются между собой; в черновике
        /// для истории правок они не выдаются, иначе застыли бы на первом имени.
        /// </summary>
        public CharacterAnketa BuildAnketa(bool forSave)
        {
            if (forSave) AssignFieldIds();

            var result = new CharacterAnketa
            {
                Id = _meta.Id,
                Name = string.IsNullOrWhiteSpace(_name) ? _meta.Name : _name.Trim(),
                Description = _description?.Trim() ?? string.Empty,
                IsBuiltIn = _meta.IsBuiltIn,
                ProjectTypeTags = _meta.ProjectTypeTags.ToList(),
                CreatedAt = _meta.CreatedAt,
                Icon = _icon,
                IconColor = _iconColor,
                Assets = _assets.Select(a => new CharacterAnketaAsset { Id = a.Id, Name = a.Name, Glyph = a.Glyph }).ToList()
            };

            var order = 0;
            foreach (var row in Rows)
            {
                result.Layout.Add(row.ToRow());
                foreach (var cell in row.Cells)
                    if (cell.Field != null)
                        result.Fields.Add(cell.Field.ToField(order++));
            }

            CharacterAnketaLayout.ApplyOrderAndGroups(result);
            return result;
        }

        private void AssignFieldIds()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var editors = Rows.SelectMany(r => r.Cells).Where(c => c.Field != null).Select(c => c.Field!).ToList();

            foreach (var editor in editors.Where(e => !string.IsNullOrWhiteSpace(e.FieldId)))
                used.Add(editor.FieldId);

            foreach (var editor in editors.Where(e => string.IsNullOrWhiteSpace(e.FieldId)))
            {
                var baseId = CharacterFieldId.FromName(editor.Name);
                if (string.IsNullOrEmpty(baseId)) baseId = "field";
                var id = baseId;
                for (int i = 2; used.Contains(id); i++) id = baseId + "_" + i;
                editor.AssignFieldId(id);
                used.Add(id);
            }
        }

        // ── История правок ───────────────────────────────────────────────

        private readonly record struct HistoryState(string Json, string? SelectedKey);

        private readonly List<HistoryState> _undo = new();
        private readonly List<HistoryState> _redo = new();
        private const int HistoryLimit = 300;

        private string _current = string.Empty;
        private string? _currentSelected;
        private string _saved = string.Empty;
        private string? _lastMergeKey;
        private DateTime _lastChangeAt;
        private bool _restoring;
        private bool _batch;

        private string Serialize() => JsonConvert.SerializeObject(BuildAnketa(forSave: false));

        private void OnFieldEdited(AnketaFieldEditorViewModel field) => RecordChange("field:" + field.Key);

        /// <summary>
        /// Правка случилась: прежнее состояние — в стопку отмены. Подряд идущие
        /// правки одного места за полторы секунды — одна запись: так набор
        /// названия отменяется целиком, а не по букве.
        /// </summary>
        internal void RecordChange(string? mergeKey)
        {
            if (_restoring || _batch) return;

            var json = Serialize();
            if (json == _current) return;

            var now = DateTime.UtcNow;
            var merge = mergeKey != null &&
                        mergeKey == _lastMergeKey &&
                        (now - _lastChangeAt).TotalSeconds < 1.5 &&
                        _undo.Count > 0;

            if (!merge)
            {
                _undo.Add(new HistoryState(_current, _currentSelected));
                if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
            }

            _redo.Clear();
            _current = json;
            _currentSelected = _selectedField?.Key;
            _lastMergeKey = mergeKey;
            _lastChangeAt = now;

            RaiseHistory();
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Есть несохранённое: правки или анкета, которой ещё нет в проекте.</summary>
        public bool IsDirty => IsNew || _current != _saved;

        public string HistoryCaption => "Отмен: " + _undo.Count + " · повторов: " + _redo.Count;

        private void RaiseHistory()
        {
            this.RaisePropertyChanged(nameof(CanUndo));
            this.RaisePropertyChanged(nameof(CanRedo));
            this.RaisePropertyChanged(nameof(IsDirty));
            this.RaisePropertyChanged(nameof(HistoryCaption));
            StateChanged?.Invoke();
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;

            _redo.Add(new HistoryState(_current, _selectedField?.Key));
            var state = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Restore(state);
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;

            _undo.Add(new HistoryState(_current, _selectedField?.Key));
            var state = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Restore(state);
            return true;
        }

        private void Restore(HistoryState state)
        {
            _restoring = true;
            try
            {
                SelectedField = null;
                SelectedRow = null;

                var draft = JsonConvert.DeserializeObject<CharacterAnketa>(state.Json)!;
                BindToSheet(draft);
                CharacterAnketaLayout.Normalize(draft);
                Load(draft);

                SelectedField = FieldByKey(state.SelectedKey);
            }
            finally
            {
                _restoring = false;
            }

            _current = state.Json;
            _currentSelected = state.SelectedKey;
            _lastMergeKey = null;
            RaiseHistory();
        }

        /// <summary>Лист сохранён: нынешнее состояние — сохранённое.</summary>
        public void MarkSaved()
        {
            // Сохранение выдаёт новым полям идентификаторы — это тоже часть
            // состояния, но не правка, которую стоило бы отменять.
            _current = Serialize();
            _currentSelected = _selectedField?.Key;
            _saved = _current;
            RaiseHistory();
        }

        // ── Превью ───────────────────────────────────────────────────────

        private CharacterAnketaSectionViewModel? _preview;

        /// <summary>Анкета так, как она встанет в карточку персонажа.</summary>
        public CharacterAnketaSectionViewModel? Preview
        {
            get => _preview;
            private set => this.RaiseAndSetIfChanged(ref _preview, value);
        }

        private bool _isPreviewOpen;
        public bool IsPreviewOpen
        {
            get => _isPreviewOpen;
            set
            {
                if (_isPreviewOpen == value) return;
                if (value) BuildPreview();
                this.RaiseAndSetIfChanged(ref _isPreviewOpen, value);
            }
        }

        private void BuildPreview()
        {
            var draft = BuildAnketa(forSave: false);
            var items = _service.BuildParameters(draft)
                .Select(p => new CharacterParameterItemViewModel(p))
                .ToList();

            Preview = new CharacterAnketaSectionViewModel(draft.Name, draft.Id, items, draft)
            {
                Icon = draft.Icon,
                IconColor = draft.IconColor
            };
        }
    }

    /// <summary>Строка раскладки на листе: подзаголовок группы или одна–три ячейки.</summary>
    public class AnketaLayoutRowViewModel : ReactiveObject
    {
        private readonly AnketaSheetViewModel _sheet;

        public AnketaLayoutRowViewModel(AnketaSheetViewModel sheet, CharacterAnketaRow row)
        {
            _sheet = sheet;
            Kind = row.Kind;
            _title = row.Title ?? string.Empty;

            if (Kind == CharacterAnketaRowKind.Fields)
            {
                var keys = row.Cells.Count == 0 ? new List<string> { string.Empty } : row.Cells;
                foreach (var key in keys.Take(CharacterAnketaLayout.MaxColumns))
                    Cells.Add(new AnketaLayoutCellViewModel(this, sheet.FieldByKey(key)));
            }
        }

        /// <summary>Опознаватель строки на время работы — для склейки правок заголовка.</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public CharacterAnketaRowKind Kind { get; }
        public bool IsGroup => Kind == CharacterAnketaRowKind.Group;
        public bool IsFields => Kind == CharacterAnketaRowKind.Fields;

        public AnketaSheetViewModel Sheet => _sheet;

        private string _title;
        public string Title
        {
            get => _title;
            set
            {
                if (_title == value) return;
                this.RaiseAndSetIfChanged(ref _title, value ?? string.Empty);
                _sheet.OnGroupTitleEdited(this);
            }
        }

        public ObservableCollection<AnketaLayoutCellViewModel> Cells { get; } = new();

        public int Columns => Math.Max(1, Cells.Count);
        public bool IsOneColumn => Columns == 1;
        public bool IsTwoColumns => Columns == 2;
        public bool IsThreeColumns => Columns == 3;

        internal void RaiseColumns()
        {
            this.RaisePropertyChanged(nameof(Columns));
            this.RaisePropertyChanged(nameof(IsOneColumn));
            this.RaisePropertyChanged(nameof(IsTwoColumns));
            this.RaisePropertyChanged(nameof(IsThreeColumns));
        }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

        private bool _isDragSource;
        public bool IsDragSource { get => _isDragSource; set => this.RaiseAndSetIfChanged(ref _isDragSource, value); }

        public CharacterAnketaRow ToRow() => new()
        {
            Kind = Kind,
            Title = _title,
            Cells = IsFields ? Cells.Select(c => c.Field?.Key ?? string.Empty).ToList() : new List<string>()
        };
    }

    /// <summary>Ячейка строки на листе: поле или пустое место под поле.</summary>
    public class AnketaLayoutCellViewModel : ReactiveObject
    {
        public AnketaLayoutCellViewModel(AnketaLayoutRowViewModel row, AnketaFieldEditorViewModel? field)
        {
            Row = row;
            _field = field;
        }

        public AnketaLayoutRowViewModel Row { get; }

        private AnketaFieldEditorViewModel? _field;
        public AnketaFieldEditorViewModel? Field
        {
            get => _field;
            set
            {
                this.RaiseAndSetIfChanged(ref _field, value);
                this.RaisePropertyChanged(nameof(HasField));
                this.RaisePropertyChanged(nameof(IsEmpty));
            }
        }

        public bool HasField => _field != null;
        public bool IsEmpty => _field == null;

        private bool _isDropTarget;
        public bool IsDropTarget { get => _isDropTarget; set => this.RaiseAndSetIfChanged(ref _isDropTarget, value); }
    }

    /// <summary>Значок в выборе значка подписи или оценки.</summary>
    public class AnketaGlyphOptionViewModel
    {
        public AnketaGlyphOptionViewModel(string glyph, string label, string? assetId)
        {
            Glyph = glyph;
            Label = label;
            AssetId = assetId;
        }

        public string Glyph { get; }
        public string Label { get; }

        /// <summary>Свой значок анкеты — его можно убрать из анкеты.</summary>
        public string? AssetId { get; }
        public bool IsAsset => AssetId != null;
    }

    /// <summary>Значок в выборе значка анкеты.</summary>
    public class AnketaIconOptionViewModel : ReactiveObject
    {
        private readonly Action<string> _select;

        public AnketaIconOptionViewModel(string key, string label, bool isSelected, Action<string> select)
        {
            Key = key;
            Label = label;
            _isSelected = isSelected;
            _select = select;
        }

        public string Key { get; }
        public string Label { get; }

        private string _iconColor = CharacterAnketaIcons.DefaultColor;
        /// <summary>Цвет, которым показан значок в выборе, — текущий цвет анкеты.</summary>
        public string IconColor { get => _iconColor; set => this.RaiseAndSetIfChanged(ref _iconColor, value); }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!value) { this.RaisePropertyChanged(); return; }
                if (_isSelected) return;
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                _select(Key);
            }
        }

        public void SetSelectedSilently(bool value) =>
            this.RaiseAndSetIfChanged(ref _isSelected, value, nameof(IsSelected));
    }

    /// <summary>Цвет в выборе цвета значка анкеты.</summary>
    public class AnketaIconColorOptionViewModel : ReactiveObject
    {
        private readonly Action<string> _select;

        public AnketaIconColorOptionViewModel(string hex, bool isSelected, Action<string> select)
        {
            Hex = hex;
            _isSelected = isSelected;
            _select = select;
        }

        public string Hex { get; }

        private string _icon = CharacterAnketaIcons.Default;
        /// <summary>Значок, которым показан цвет в выборе, — текущий значок анкеты.</summary>
        public string Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!value) { this.RaisePropertyChanged(); return; }
                if (_isSelected) return;
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                _select(Hex);
            }
        }

        public void SetSelectedSilently(bool value) =>
            this.RaiseAndSetIfChanged(ref _isSelected, value, nameof(IsSelected));
    }

}
