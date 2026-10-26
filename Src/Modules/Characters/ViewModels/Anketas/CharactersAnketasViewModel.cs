using Avalonia.Input;
using Avalonia.Media;
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
        public AnketaSheetViewModel CreateAnketa()
        {
            var draft = new CharacterAnketa
            {
                Id = Guid.NewGuid().ToString(),
                Name = NextName("Новая анкета"),
                IsBuiltIn = false,
                CreatedAt = DateTime.UtcNow
            };

            return OpenNew(draft, focusName: true);
        }

        /// <summary>
        /// Новая анкета для шаблона: лист помнит шаблон, и после первого
        /// сохранения анкета встаёт в него (AnketaCreatedForTemplate).
        /// </summary>
        public void CreateAnketaFor(string templateId)
        {
            var sheet = CreateAnketa();
            sheet.PendingTemplateId = templateId;
            ScheduleDrafts();
        }

        /// <summary>Анкету, начатую из шаблона, впервые сохранили: (анкета, шаблон).</summary>
        public event Action<string, string>? AnketaCreatedForTemplate;

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

            var pendingTemplate = sheet.IsNew ? sheet.PendingTemplateId : null;

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

            if (pendingTemplate != null)
            {
                sheet.PendingTemplateId = null;
                AnketaCreatedForTemplate?.Invoke(anketa.Id, pendingTemplate);
            }
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
                        TemplateId = s.IsNew ? s.PendingTemplateId : null,
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
                Sheets.Add(new AnketaSheetViewModel(draft, _anketaService, isNew: true)
                {
                    PendingTemplateId = string.IsNullOrWhiteSpace(entry.TemplateId) ? null : entry.TemplateId
                });
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

        // ── Заготовки полей ──────────────────────────────────────────────

        /// <summary>Порядок типов в списке заготовок — тот же, что у кнопок «Добавить поле».</summary>
        private static readonly CharacterParameterType[] PresetTypeOrder =
        {
            CharacterParameterType.Numeric,
            CharacterParameterType.Number,
            CharacterParameterType.StateList,
            CharacterParameterType.MultiChoice,
            CharacterParameterType.Boolean,
            CharacterParameterType.Color,
            CharacterParameterType.Text,
            CharacterParameterType.LongText
        };

        /// <summary>Меню «Заготовки»: свои сверху, встроенные — по типам, с подзаголовками.</summary>
        public ObservableCollection<FieldPresetItemViewModel> PresetItems { get; } = new();

        private string _presetQuery = string.Empty;

        /// <summary>Поиск в меню заготовок: по названию, пояснению и вариантам.</summary>
        public string PresetQuery
        {
            get => _presetQuery;
            set
            {
                if (_presetQuery == value) return;
                this.RaiseAndSetIfChanged(ref _presetQuery, value ?? string.Empty);
                RefreshPresets();
            }
        }

        public void OpenPresets()
        {
            _presetQuery = string.Empty;
            this.RaisePropertyChanged(nameof(PresetQuery));
            RefreshPresets();
        }

        public void RefreshPresets()
        {
            PresetItems.Clear();

            var words = _presetQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Matches(CharacterFieldPreset preset)
            {
                var text = preset.Name + " " + preset.Hint + " " + preset.Field.StatesRaw + " "
                           + AnketaFieldEditorViewModel.TypeLabelOf(preset.Field.Type);
                return words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
            }

            var presets = _anketaService.GetFieldPresets().Where(Matches).ToList();

            var custom = presets.Where(p => !p.IsBuiltIn).ToList();
            if (custom.Count > 0)
            {
                PresetItems.Add(FieldPresetItemViewModel.Header("Свои"));
                foreach (var preset in custom) PresetItems.Add(new FieldPresetItemViewModel(preset));
            }

            foreach (var type in PresetTypeOrder)
            {
                var ofType = presets.Where(p => p.IsBuiltIn && p.Field.Type == type).ToList();
                if (ofType.Count == 0) continue;

                PresetItems.Add(FieldPresetItemViewModel.Header(AnketaFieldEditorViewModel.TypeLabelOf(type)));
                foreach (var preset in ofType) PresetItems.Add(new FieldPresetItemViewModel(preset));
            }
        }

        /// <summary>
        /// Новое поле из заготовки на открытом листе — туда же, куда встало
        /// бы новое поле: в выбранное свободное место или строкой за выбранной.
        /// </summary>
        public AnketaFieldEditorViewModel? AddPresetField(string presetId)
        {
            if (_activeSheet is not { IsEditable: true } sheet) return null;

            var preset = _anketaService.GetFieldPresets().FirstOrDefault(p => p.Id == presetId);
            return preset == null ? null : sheet.AddFieldFromPreset(preset.Field);
        }

        /// <summary>Сохранить поле своей заготовкой: в списке оно встанет среди своих.</summary>
        public bool SaveFieldAsPreset(AnketaFieldEditorViewModel field)
        {
            var saved = _anketaService.SaveFieldPreset(field.Snapshot());
            if (saved == null) return false;

            RefreshPresets();
            return true;
        }

        public void DeletePreset(string presetId)
        {
            _anketaService.DeleteFieldPreset(presetId);
            RefreshPresets();
        }
    }

    /// <summary>Строка меню заготовок: подзаголовок типа или сама заготовка.</summary>
    public class FieldPresetItemViewModel
    {
        private FieldPresetItemViewModel(string title)
        {
            IsHeader = true;
            Title = title;
        }

        public FieldPresetItemViewModel(CharacterFieldPreset preset)
        {
            Id = preset.Id;
            Name = preset.Name;
            Hint = preset.Hint;
            IsCustom = !preset.IsBuiltIn;
            TypeLabel = AnketaFieldEditorViewModel.TypeLabelOf(preset.Field.Type);
            TypeIcon = AnketaFieldEditorViewModel.TypeIconPath(preset.Field.Type);

            var color = Color.Parse(AnketaFieldEditorViewModel.TypeColorOf(preset.Field.Type));
            TypeBrush = new SolidColorBrush(color);
            TypeSoftBrush = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
        }

        public static FieldPresetItemViewModel Header(string title) => new(title);

        public bool IsHeader { get; }
        public bool IsItem => !IsHeader;
        public string Title { get; } = string.Empty;

        public string Id { get; } = string.Empty;
        public string Name { get; } = string.Empty;
        public string Hint { get; } = string.Empty;

        /// <summary>Своя заготовка — её можно удалить.</summary>
        public bool IsCustom { get; }

        /// <summary>Тип словом — в своих, где типы вперемешку.</summary>
        public string TypeLabel { get; } = string.Empty;

        public string TypeIcon { get; } = string.Empty;
        public IBrush? TypeBrush { get; }
        public IBrush? TypeSoftBrush { get; }
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
            IconBackground = anketa.IconBackground ?? string.Empty;
            IconBorder = anketa.IconBorder ?? string.Empty;
            IsBuiltIn = anketa.IsBuiltIn;
            FieldCount = anketa.Fields.Count;
            IsOpen = isOpen;
            AudienceLabel = anketa.Audience == CharacterAnketaAudience.Collective ? "группа" : string.Empty;
            HasLevels = CharacterDetailLevels.HasLevels(anketa);
        }

        /// <summary>Пометка «группа» у анкет для групп: их ищут отдельно.</summary>
        public string AudienceLabel { get; }
        public bool HasAudienceLabel => !string.IsNullOrEmpty(AudienceLabel);

        /// <summary>У анкеты есть уровни детализации глубже краткого.</summary>
        public bool HasLevels { get; }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Icon { get; }
        public string IconColor { get; }
        public string IconBackground { get; }
        public string IconBorder { get; }
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

            BuildPreviewDepthOptions();
            Load(draft);

            _current = Serialize();
            _saved = _current;
        }

        private static CharacterAnketa Clone(CharacterAnketa anketa) =>
            JsonConvert.DeserializeObject<CharacterAnketa>(JsonConvert.SerializeObject(anketa))!;

        public string AnketaId { get; private set; }

        /// <summary>Анкеты ещё нет в проекте: она появится там по первому «Сохранить».</summary>
        public bool IsNew { get; private set; }

        /// <summary>Шаблон, из которого начали новую анкету: после первого сохранения она встанет в него.</summary>
        public string? PendingTemplateId { get; set; }

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

        private string _iconBackground = string.Empty;

        /// <summary>Подложка значка: пусто — бледная цвета значка, «fill» — залитая, «none» — без подложки, иначе свой код цвета.</summary>
        public string IconBackground { get => _iconBackground; private set => this.RaiseAndSetIfChanged(ref _iconBackground, value); }

        private string _iconBorder = string.Empty;

        /// <summary>Рамка вокруг подложки значка: пусто — нет, иначе CharacterIconFrame.</summary>
        public string IconBorder { get => _iconBorder; private set => this.RaiseAndSetIfChanged(ref _iconBorder, value); }

        /// <summary>
        /// Поставить значок и цвет анкеты — из окна значков или броском
        /// картинки на значок. Значок — встроенный ключ или своя картинка из
        /// библиотеки проекта, цвет — код цвета программы, градиент тоже.
        /// Подложка и рамка остаются прежними.
        /// </summary>
        public void ApplyIcon(string icon, string color) => ApplyIcon(icon, color, _iconBackground, _iconBorder);

        /// <summary>Значок, цвет, подложка и рамка одной правкой — из окна значков.</summary>
        public void ApplyIcon(string icon, string color, string background, string border)
        {
            if (IsReadOnly) return;

            var newIcon = string.IsNullOrWhiteSpace(icon) ? CharacterAnketaIcons.Default : icon;
            var newColor = string.IsNullOrWhiteSpace(color) ? CharacterAnketaIcons.DefaultColor : color;
            var newBackground = background ?? string.Empty;
            var newBorder = border ?? string.Empty;
            if (newIcon == Icon && newColor == IconColor && newBackground == IconBackground && newBorder == IconBorder)
                return;

            Icon = newIcon;
            IconColor = newColor;
            IconBackground = newBackground;
            IconBorder = newBorder;
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
            _audience = draft.Audience;
            BuildAudienceOptions();
            Icon = string.IsNullOrWhiteSpace(draft.Icon) ? CharacterAnketaIcons.Default : draft.Icon;
            IconColor = string.IsNullOrWhiteSpace(draft.IconColor) ? CharacterAnketaIcons.DefaultColor : draft.IconColor;
            IconBackground = draft.IconBackground ?? string.Empty;
            IconBorder = draft.IconBorder ?? string.Empty;
            this.RaisePropertyChanged(nameof(Name));
            this.RaisePropertyChanged(nameof(Description));

            _assets = (draft.Assets ?? new List<CharacterAnketaAsset>()).ToList();
            _look = draft.Look is { IsEmpty: false } look ? look.Clone() : null;
            RaiseAnketaLookProperties();

            _fields.Clear();
            foreach (var field in draft.Fields)
                CreateEditor(field);

            // Строка полей без единого поля — бывшее пустое место под поле:
            // пустых мест на листе больше нет, их заменило свободное место у
            // конца строки и под строками.
            Rows.Clear();
            foreach (var row in draft.Layout)
            {
                if (row.Kind == CharacterAnketaRowKind.Fields && row.Cells.All(key => FieldByKey(key) == null))
                    continue;
                Rows.Add(new AnketaLayoutRowViewModel(this, row));
            }

            RebuildGlyphChoices();
            this.RaisePropertyChanged(nameof(HasRows));
            this.RaisePropertyChanged(nameof(Audience));
            RefreshDepths();
        }

        // ── Для кого ─────────────────────────────────────────────────────

        private CharacterAnketaAudience _audience = CharacterAnketaAudience.Any;

        /// <summary>Для кого анкета: персонажа, группы или обоих. Карточка предлагает только подходящие.</summary>
        public CharacterAnketaAudience Audience => _audience;

        public ObservableCollection<AnketaOptionViewModel<CharacterAnketaAudience>> AudienceOptions { get; } = new();

        private void BuildAudienceOptions()
        {
            AudienceOptions.Clear();

            void Add(CharacterAnketaAudience value, string label, string hint) =>
                AudienceOptions.Add(new AnketaOptionViewModel<CharacterAnketaAudience>(
                    value, label, hint, _audience == value, SetAudience));

            Add(CharacterAnketaAudience.Individual, "Персонаж",
                "Анкета отдельного персонажа: внешность, характер, биография. Группе не предлагается");
            Add(CharacterAnketaAudience.Collective, "Группа",
                "Анкета народа, расы, организации: численность, обычаи, власть. Отдельному персонажу не предлагается");
            Add(CharacterAnketaAudience.Any, "Все",
                "Подходит и персонажу, и группе: фракция, цели, магия");
        }

        private void SetAudience(CharacterAnketaAudience value)
        {
            if (IsReadOnly || _audience == value)
            {
                foreach (var option in AudienceOptions) option.SetSelectedSilently(option.Value == _audience);
                return;
            }

            _audience = value;
            foreach (var option in AudienceOptions) option.SetSelectedSilently(option.Value == value);
            this.RaisePropertyChanged(nameof(Audience));
            RecordChange(null);
        }

        // ── Уровни детализации ───────────────────────────────────────────
        //
        // Анкета раскрывается на три глубины: кратко, подробно, глубоко.
        // Уровень задаёт подзаголовок группы, поле может перебить его своим.
        // Показ листа на уровне приглушает всё, что глубже: так видно, что
        // увидит в карточке эпизодический персонаж, а что — главный герой.

        private int _previewDepth;

        /// <summary>Уровень показа листа. 0 — все поля без приглушения.</summary>
        public int PreviewDepth => _previewDepth;

        public ObservableCollection<AnketaOptionViewModel<int>> PreviewDepthOptions { get; } = new();

        private void BuildPreviewDepthOptions()
        {
            PreviewDepthOptions.Clear();
            PreviewDepthOptions.Add(new AnketaOptionViewModel<int>(0, "Все",
                "Все поля анкеты. Новые поля встают на уровень своей группы", _previewDepth == 0, SetPreviewDepth));
            foreach (var level in CharacterDetailLevels.All)
                PreviewDepthOptions.Add(new AnketaOptionViewModel<int>(level, CharacterDetailLevels.NameOf(level),
                    CharacterDetailLevels.HintOf(level) + ". Глубже — приглушено, новые поля встают на этот уровень",
                    _previewDepth == level, SetPreviewDepth)
                {
                    GlyphLevel = level
                });
        }

        private void SetPreviewDepth(int value)
        {
            _previewDepth = value;
            foreach (var option in PreviewDepthOptions) option.SetSelectedSilently(option.Value == value);
            this.RaisePropertyChanged(nameof(PreviewDepth));
            this.RaisePropertyChanged(nameof(PreviewCaption));
            RefreshDepths();
            if (_isPreviewOpen) BuildPreview();
        }

        private string _levelsCaption = string.Empty;

        /// <summary>Сколько полей видно на каждом уровне: «Кратко 4 · Подробно 9 · Глубоко 15».</summary>
        public string LevelsCaption
        {
            get => _levelsCaption;
            private set => this.RaiseAndSetIfChanged(ref _levelsCaption, value);
        }

        /// <summary>
        /// Пересчитать уровни полей по раскладке: подзаголовок задаёт уровень
        /// полям под ним, своё значение поля его перебивает. Заодно — что
        /// приглушить при показе на уровне и сводка по уровням в шапке.
        /// </summary>
        internal void RefreshDepths()
        {
            var groupLevel = CharacterDetailLevels.Brief;
            var counts = new int[CharacterDetailLevels.Max + 1];

            foreach (var row in Rows)
            {
                if (row.IsGroup)
                {
                    groupLevel = row.Depth;
                    row.IsBeyondPreview = _previewDepth > 0 && row.Depth > _previewDepth;
                    continue;
                }

                row.IsBeyondPreview = false;

                foreach (var cell in row.Cells)
                {
                    if (cell.Field == null) continue;

                    var level = cell.Field.Depth ?? groupLevel;
                    cell.Field.EffectiveDepth = level;
                    cell.Field.IsBeyondPreview = _previewDepth > 0 && level > _previewDepth;
                    counts[level]++;
                }
            }

            var total = 0;
            var parts = new List<string>();
            foreach (var level in CharacterDetailLevels.All)
            {
                total += counts[level];
                if (counts[level] > 0 || level == CharacterDetailLevels.Brief)
                    parts.Add(CharacterDetailLevels.NameOf(level) + " " + total);
            }

            LevelsCaption = parts.Count > 1 ? string.Join(" · ", parts) : string.Empty;
        }

        /// <summary>Уровень группы, под которой стоит строка: ближайший подзаголовок выше.</summary>
        private int GroupDepthOf(AnketaLayoutRowViewModel row)
        {
            var index = Rows.IndexOf(row);
            for (int i = index - 1; i >= 0; i--)
                if (Rows[i].IsGroup) return Rows[i].Depth;
            return CharacterDetailLevels.Brief;
        }

        /// <summary>
        /// Новое поле встаёт на уровень показа листа, если он выбран: так
        /// анкету собирают слоями — сначала краткое, потом всё глубже.
        /// Совпадает с уровнем группы — своё значение не нужно.
        /// </summary>
        private void ApplyPreviewDepth(AnketaFieldEditorViewModel editor, AnketaLayoutRowViewModel row)
        {
            if (_previewDepth <= 0) return;
            editor.SetDepthSilently(GroupDepthOf(row) == _previewDepth ? null : _previewDepth);
        }

        internal void OnGroupDepthEdited(AnketaLayoutRowViewModel row)
        {
            RefreshDepths();
            RecordChange(null);
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
                    ClearSlot();
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

                // Выбрали другую строку — свободное место прежней уже не выбрано.
                if (value != null && !ReferenceEquals(value, _slotRow)) ClearSlot();
            }
        }

        // ── Свободное место ──────────────────────────────────────────────
        //
        // Новое поле ставят не только броском: можно выбрать свободное место
        // — в конце строки, где ещё есть место, или под всеми строками — и
        // добавить поле лентой. Выбрано не больше одного места.

        private AnketaLayoutRowViewModel? _slotRow;
        private bool _isEndSlotSelected;

        /// <summary>Выбрано место под всеми строками: новое поле встанет туда новой строкой.</summary>
        public bool IsEndSlotSelected
        {
            get => _isEndSlotSelected;
            private set => this.RaiseAndSetIfChanged(ref _isEndSlotSelected, value);
        }

        // ── Перетаскивание поля: места ───────────────────────────────────

        private bool _isFieldDragging;

        /// <summary>
        /// Поле тащат: на листе видны все места, куда его можно поставить, —
        /// каждое поле (на него — поменяться местами, к его левому краю —
        /// встать перед ним), свободные места в концах строк и новая строка
        /// внизу. Остальное приглушено.
        /// </summary>
        public bool IsFieldDragging
        {
            get => _isFieldDragging;
            set
            {
                if (_isFieldDragging == value) return;
                this.RaiseAndSetIfChanged(ref _isFieldDragging, value);

                // Пока тащат, у каждой строки все три места равной ширины:
                // поля стоят на своих, свободные видны рядом пустыми.
                foreach (var row in Rows) row.RaiseLayoutColumns();
            }
        }

        private bool _isEndSlotDropTarget;

        /// <summary>Тащимое поле над местом новой строки внизу.</summary>
        public bool IsEndSlotDropTarget
        {
            get => _isEndSlotDropTarget;
            set => this.RaiseAndSetIfChanged(ref _isEndSlotDropTarget, value);
        }

        /// <summary>
        /// Поменять два поля местами — в одной строке или в разных. Строки
        /// сохраняют число полей, двигаются только сами поля.
        /// </summary>
        public void SwapFields(AnketaLayoutCellViewModel from, AnketaLayoutCellViewModel to)
        {
            if (IsReadOnly || ReferenceEquals(from, to)) return;
            if (from.Field is not { } moving || to.Field is not { } other) return;

            from.Field = other;
            to.Field = moving;

            SelectedField = moving;
            SelectedRow = to.Row;
            RecordChange(null);
        }

        /// <summary>Выбрать свободное место в конце строки: новое поле встанет туда, рядом с её полями.</summary>
        public void SelectSlot(AnketaLayoutRowViewModel row)
        {
            if (IsReadOnly || !row.CanTakeMore) return;

            SelectedField = null;
            SelectedRow = row;
            ClearSlot();
            _slotRow = row;
            row.IsSlotSelected = true;
        }

        /// <summary>Выбрать место под всеми строками: новое поле встанет туда новой строкой.</summary>
        public void SelectEndSlot()
        {
            if (IsReadOnly) return;

            SelectedField = null;
            SelectedRow = null;
            ClearSlot();
            IsEndSlotSelected = true;
        }

        private void ClearSlot()
        {
            if (_slotRow != null) _slotRow.IsSlotSelected = false;
            _slotRow = null;
            IsEndSlotSelected = false;
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
        /// Новое поле — в выбранное свободное место: в конец строки, рядом с
        /// её полями, или под всеми строками. Места не выбрано — новой
        /// строкой за выбранной.
        /// </summary>
        public AnketaFieldEditorViewModel? AddField(CharacterParameterType type)
        {
            if (IsReadOnly) return null;
            return AddFieldFrom(NewField(type));
        }

        /// <summary>
        /// Новое поле из заготовки — со всеми её настройками, туда же, куда
        /// встало бы новое поле. Ключ, идентификатор, группа и уровень у него
        /// свои, по месту.
        /// </summary>
        public AnketaFieldEditorViewModel? AddFieldFromPreset(CharacterAnketaField template)
        {
            if (IsReadOnly || template == null) return null;

            var field = JsonConvert.DeserializeObject<CharacterAnketaField>(JsonConvert.SerializeObject(template))!;
            field.FieldId = string.Empty;
            field.GroupName = string.Empty;
            field.Order = 0;
            field.Depth = null;
            return AddFieldFrom(field);
        }

        private AnketaFieldEditorViewModel AddFieldFrom(CharacterAnketaField field)
        {
            field.Key = NewKey();
            var editor = CreateEditor(field);

            AnketaLayoutRowViewModel row;
            if (_slotRow is { CanTakeMore: true } slot && Rows.Contains(slot))
            {
                row = slot;
            }
            else
            {
                row = NewFieldsRow();
                Rows.Insert(_isEndSlotSelected ? Rows.Count : InsertIndex(), row);
            }

            ApplyPreviewDepth(editor, row);
            row.Cells.Add(new AnketaLayoutCellViewModel(row, editor));
            SelectedField = editor;
            this.RaisePropertyChanged(nameof(HasRows));
            RecordChange(null);
            return editor;
        }

        /// <summary>Строка полей без полей — под поле, которое сейчас в неё встанет.</summary>
        private AnketaLayoutRowViewModel NewFieldsRow() =>
            new(this, new CharacterAnketaRow { Cells = new List<string>() });

        /// <summary>
        /// Строка осталась без полей — её больше нет: пустых строк на листе
        /// не бывает.
        /// </summary>
        private void RemoveIfEmpty(AnketaLayoutRowViewModel row)
        {
            if (!row.IsFields || row.Cells.Count > 0) return;

            if (ReferenceEquals(_selectedRow, row)) SelectedRow = null;
            if (ReferenceEquals(_slotRow, row)) ClearSlot();
            Rows.Remove(row);
            this.RaisePropertyChanged(nameof(HasRows));
        }

        /// <summary>Подзаголовок группы — за выбранной строкой.</summary>
        public AnketaLayoutRowViewModel? AddGroup()
        {
            if (IsReadOnly) return null;

            // Новая группа встаёт на уровень показа листа, а без него — на
            // уровень группы выше: анкету чаще всего наращивают вглубь.
            var index = InsertIndex();
            var above = CharacterDetailLevels.Brief;
            for (int i = index - 1; i >= 0; i--)
                if (Rows[i].IsGroup) { above = Rows[i].Depth; break; }

            var row = new AnketaLayoutRowViewModel(this, new CharacterAnketaRow
            {
                Kind = CharacterAnketaRowKind.Group,
                Title = "Новая группа",
                Depth = _previewDepth > 0 ? _previewDepth : above
            });
            Rows.Insert(index, row);
            SelectedField = null;
            SelectedRow = row;
            this.RaisePropertyChanged(nameof(HasRows));
            RecordChange(null);
            return row;
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
            if (ReferenceEquals(_slotRow, row)) ClearSlot();
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

        /// <summary>
        /// Поле можно поставить рядом с другим: в своей строке — всегда, в
        /// чужой — пока в ней меньше трёх полей. Ставить рядом с самим собой
        /// незачем.
        /// </summary>
        public bool CanPlaceBeside(AnketaLayoutCellViewModel from, AnketaLayoutCellViewModel target) =>
            !IsReadOnly
            && from.HasField
            && !ReferenceEquals(from, target)
            && (ReferenceEquals(from.Row, target.Row) || target.Row.Cells.Count < CharacterAnketaLayout.MaxColumns);

        /// <summary>
        /// Место поля рядом с другим: индекс в строке цели, на который оно
        /// встанет, или -1, если от броска ничего не изменится.
        /// </summary>
        public int BesideIndex(AnketaLayoutCellViewModel from, AnketaLayoutCellViewModel target, bool after)
        {
            if (!CanPlaceBeside(from, target)) return -1;

            var row = target.Row;
            var index = row.Cells.IndexOf(target) + (after ? 1 : 0);
            if (!ReferenceEquals(from.Row, row)) return index;

            var fromIndex = row.Cells.IndexOf(from);
            if (index > fromIndex) index--;
            return index == fromIndex ? -1 : index;
        }

        /// <summary>
        /// Поставить поле рядом с другим — перед ним или после. Строка цели
        /// растёт, строка, откуда ушло поле, сжимается, а без полей исчезает.
        /// </summary>
        public void PlaceFieldBeside(AnketaLayoutCellViewModel from, AnketaLayoutCellViewModel target, bool after)
        {
            var index = BesideIndex(from, target, after);
            if (index < 0 || from.Field is not { } field) return;

            var source = from.Row;
            var row = target.Row;

            if (ReferenceEquals(source, row))
            {
                source.Cells.Move(source.Cells.IndexOf(from), index);
            }
            else
            {
                source.Cells.Remove(from);
                row.Cells.Insert(index, new AnketaLayoutCellViewModel(row, field));
                RemoveIfEmpty(source);
            }

            SelectedField = field;
            SelectedRow = row;
            RecordChange(null);
        }

        /// <summary>
        /// Бросок поля между строками ничего не меняет: поле одно в своей
        /// строке и встало бы ровно туда же.
        /// </summary>
        public bool IsNewRowNoop(AnketaLayoutCellViewModel from, int index)
        {
            var at = Rows.IndexOf(from.Row);
            return from.Row.Cells.Count == 1 && (index == at || index == at + 1);
        }

        /// <summary>
        /// Поставить поле отдельной строкой — на место index среди строк
        /// (до того, как оно уйдёт со своего). Строка, откуда ушло поле,
        /// сжимается, а без полей исчезает.
        /// </summary>
        public void PlaceFieldInNewRow(AnketaLayoutCellViewModel from, int index)
        {
            if (IsReadOnly || from.Field is not { } field) return;

            index = Math.Clamp(index, 0, Rows.Count);
            if (IsNewRowNoop(from, index)) return;

            var source = from.Row;
            var row = NewFieldsRow();
            row.Cells.Add(new AnketaLayoutCellViewModel(row, field));
            Rows.Insert(index, row);

            source.Cells.Remove(from);
            RemoveIfEmpty(source);

            SelectedField = field;
            SelectedRow = row;
            this.RaisePropertyChanged(nameof(HasRows));
            RecordChange(null);
        }

        public void DeleteField(AnketaFieldEditorViewModel field)
        {
            if (IsReadOnly) return;

            var cell = CellOf(field);
            if (cell == null) return;

            var row = cell.Row;
            row.Cells.Remove(cell);
            _fields.Remove(field.Key);
            if (ReferenceEquals(_selectedField, field)) SelectedField = null;
            RemoveIfEmpty(row);
            RecordChange(null);
        }

        /// <summary>Копия поля со всеми настройками — рядом с ним, если в строке есть место, иначе строкой ниже.</summary>
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

            if (row.CanTakeMore)
            {
                var at = row.Cells.IndexOf(CellOf(source)!) + 1;
                row.Cells.Insert(at, new AnketaLayoutCellViewModel(row, editor));
            }
            else
            {
                var newRow = NewFieldsRow();
                newRow.Cells.Add(new AnketaLayoutCellViewModel(newRow, editor));
                Rows.Insert(Rows.IndexOf(row) + 1, newRow);
            }

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

        // ── Особый вид анкеты ────────────────────────────────────────────
        //
        // Расстояние между строками, выравнивание полей, шрифт и размеры,
        // высота описаний — для всей анкеты. Поле и строка могут задать своё.

        private CharacterAnketaLook? _look;

        /// <summary>Особый вид анкеты; null — как в программе.</summary>
        public CharacterAnketaLook? Look => _look;

        public IReadOnlyList<AnketaLookChoice> AnketaAlignChoices { get; } = AnketaLookChoice.AnketaAlignChoices;

        public AnketaLookChoice SelectedFieldAlign
        {
            get => AnketaAlignChoices.FirstOrDefault(c => c.Value == CharacterLookAlign.Normalize(_look?.FieldAlign))
                   ?? AnketaAlignChoices[0];
            set
            {
                var normalized = CharacterLookAlign.Normalize(value?.Value);
                if (CharacterLookAlign.Normalize(_look?.FieldAlign) == normalized) return;
                EditAnketaLook(look => look.FieldAlign = normalized, "align");
            }
        }

        public string LookRowSpacingText
        {
            get => FormatLook(_look?.RowSpacing);
            set
            {
                if (!TryParseLook(value, 0, 200, out var spacing) || _look?.RowSpacing == spacing) return;
                EditAnketaLook(look => look.RowSpacing = spacing, "row-spacing");
            }
        }

        public string LookFontFamily
        {
            get => _look?.FontFamily ?? string.Empty;
            set
            {
                var name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (_look?.FontFamily == name) return;
                EditAnketaLook(look => look.FontFamily = name, "font");
            }
        }

        public string LookLabelSizeText
        {
            get => FormatLook(_look?.LabelFontSize);
            set
            {
                if (!TryParseLook(value, 6, 72, out var size) || _look?.LabelFontSize == size) return;
                EditAnketaLook(look => look.LabelFontSize = size, "label-size");
            }
        }

        public string LookValueSizeText
        {
            get => FormatLook(_look?.ValueFontSize);
            set
            {
                if (!TryParseLook(value, 6, 72, out var size) || _look?.ValueFontSize == size) return;
                EditAnketaLook(look => look.ValueFontSize = size, "value-size");
            }
        }

        public string LookLongTextHeightText
        {
            get => FormatLook(_look?.LongTextHeight);
            set
            {
                if (!TryParseLook(value, CharacterParameterItemViewModel.MinInputHeight,
                        CharacterParameterItemViewModel.MaxInputHeight, out var height)
                    || _look?.LongTextHeight == height) return;
                EditAnketaLook(look => look.LongTextHeight = height, "long-height");
            }
        }

        /// <summary>У анкеты есть особый вид — заголовок раздела отмечен точкой.</summary>
        public bool HasCustomLook => _look is { IsEmpty: false };

        /// <summary>Вернуть анкете вид программы. Свой вид полей и отступы строк остаются.</summary>
        public void ResetAnketaLook()
        {
            if (IsReadOnly || _look == null) return;

            _look = null;
            ApplyAnketaLookEverywhere();
            RaiseAnketaLookProperties();
            RecordChange(null);
        }

        private void EditAnketaLook(Action<CharacterAnketaLook> change, string mergeKey)
        {
            if (IsReadOnly)
            {
                RaiseAnketaLookProperties();
                return;
            }

            var look = _look?.Clone() ?? new CharacterAnketaLook();
            change(look);
            _look = look.IsEmpty ? null : look;

            ApplyAnketaLookEverywhere();
            RaiseAnketaLookProperties();
            RecordChange("look:" + mergeKey);
        }

        /// <summary>Вид анкеты дошёл до предпросмотров полей и отступов строк.</summary>
        private void ApplyAnketaLookEverywhere()
        {
            foreach (var editor in _fields.Values) editor.RefreshAnketaLook();
            foreach (var row in Rows) row.RaiseRowMargin();
        }

        private void RaiseAnketaLookProperties()
        {
            this.RaisePropertyChanged(nameof(Look));
            this.RaisePropertyChanged(nameof(SelectedFieldAlign));
            this.RaisePropertyChanged(nameof(LookRowSpacingText));
            this.RaisePropertyChanged(nameof(LookFontFamily));
            this.RaisePropertyChanged(nameof(LookLabelSizeText));
            this.RaisePropertyChanged(nameof(LookValueSizeText));
            this.RaisePropertyChanged(nameof(LookLongTextHeightText));
            this.RaisePropertyChanged(nameof(HasCustomLook));
        }

        private static string FormatLook(double? value) =>
            value.HasValue ? value.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

        /// <summary>Число из поля особого вида: пусто — null, иначе в пределах.</summary>
        private static bool TryParseLook(string? text, double min, double max, out double? value)
        {
            value = null;
            var raw = (text ?? string.Empty).Trim().Replace(',', '.');
            if (raw.Length == 0) return true;
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return false;
            value = Math.Clamp(parsed, min, max);
            return true;
        }

        /// <summary>Отступ под строкой поля; null — как у анкеты.</summary>
        internal double? RowSpacingOf(AnketaFieldEditorViewModel field) =>
            Rows.FirstOrDefault(r => r.Cells.Any(c => ReferenceEquals(c.Field, field)))?.SpacingAfter;

        /// <summary>Задать отступ под строкой поля. Остальные поля строки узнают о нём.</summary>
        internal void SetRowSpacing(AnketaFieldEditorViewModel field, double? spacing, bool record = true)
        {
            if (IsReadOnly) return;

            var row = Rows.FirstOrDefault(r => r.Cells.Any(c => ReferenceEquals(c.Field, field)));
            if (row == null || row.SpacingAfter == spacing) return;

            row.SpacingAfter = spacing;
            foreach (var cell in row.Cells) cell.Field?.RaiseRowSpacing();

            if (record) RecordChange("row-spacing:" + row.Id);
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
                Audience = _audience,
                ProjectTypeTags = _meta.ProjectTypeTags.ToList(),
                CreatedAt = _meta.CreatedAt,
                Icon = _icon,
                IconColor = _iconColor,
                IconBackground = _iconBackground,
                IconBorder = _iconBorder,
                Assets = _assets.Select(a => new CharacterAnketaAsset { Id = a.Id, Name = a.Name, Glyph = a.Glyph }).ToList(),
                Look = _look is { IsEmpty: false } ? _look.Clone() : null
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
            // Уровни полей зависят от раскладки: поле переехало под другой
            // подзаголовок — у него другой уровень.
            RefreshDepths();

            // Встроенную анкету не правят — правят её копию. Случайный сдвиг
            // ручки ширины или подобное на ней не должен делать лист
            // «несохранённым»: сохранить его всё равно нельзя.
            if (_restoring || _batch || IsReadOnly) return;

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

            // Превью показывает анкету на выбранном уровне — так, как её
            // увидит персонаж с такой глубиной.
            var levels = CharacterDetailLevels.ByFieldId(draft);
            var hidden = _previewDepth > 0
                ? levels.Where(p => p.Value > _previewDepth).Select(p => p.Key).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var items = _service.BuildParameters(draft)
                .Where(p => !hidden.Contains(CharacterFieldId.Resolve(p)))
                .Select(p => new CharacterParameterItemViewModel(p))
                .ToList();

            Preview = new CharacterAnketaSectionViewModel(draft.Name, draft.Id, items, draft, hidden)
            {
                Icon = draft.Icon,
                IconColor = draft.IconColor,
                IconBackground = draft.IconBackground ?? string.Empty,
                IconBorder = draft.IconBorder ?? string.Empty
            };
        }

        /// <summary>Подпись превью: уровень, на котором показана анкета.</summary>
        public string PreviewCaption => _previewDepth > 0
            ? "Превью · " + CharacterDetailLevels.NameOf(_previewDepth).ToLowerInvariant()
            : "Превью";
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
            _depth = CharacterDetailLevels.Clamp(row.Depth);
            _spacingAfter = row.Kind == CharacterAnketaRowKind.Fields ? row.SpacingAfter : null;

            if (Kind == CharacterAnketaRowKind.Group)
                foreach (var level in CharacterDetailLevels.All)
                    DepthOptions.Add(new AnketaOptionViewModel<int>(level, CharacterDetailLevels.NameOf(level),
                        CharacterDetailLevels.HintOf(level)
                        + ". Поля группы встают на этот уровень, если у них нет своего. "
                        + CharacterDetailLevels.SymbolHint,
                        level == _depth, SetDepth)
                    {
                        GlyphLevel = level
                    });

            // В строке — только поля: пустых мест под поле больше нет.
            if (Kind == CharacterAnketaRowKind.Fields)
                foreach (var key in row.Cells)
                {
                    if (Cells.Count >= CharacterAnketaLayout.MaxColumns) break;
                    if (sheet.FieldByKey(key) is { } field)
                        Cells.Add(new AnketaLayoutCellViewModel(this, field));
                }

            Cells.CollectionChanged += (_, _) =>
            {
                this.RaisePropertyChanged(nameof(Columns));
                this.RaisePropertyChanged(nameof(LayoutColumns));
                this.RaisePropertyChanged(nameof(CanTakeMore));
                if (!CanTakeMore) IsSlotSelected = false;
            };
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

        // ── Уровень группы ───────────────────────────────────────────────

        private int _depth;

        /// <summary>Уровень детализации группы — у подзаголовка.</summary>
        public int Depth => _depth;

        public ObservableCollection<AnketaOptionViewModel<int>> DepthOptions { get; } = new();

        private void SetDepth(int level)
        {
            if (_sheet.IsReadOnly || _depth == level)
            {
                foreach (var option in DepthOptions) option.SetSelectedSilently(option.Value == _depth);
                return;
            }

            _depth = level;
            foreach (var option in DepthOptions) option.SetSelectedSilently(option.Value == level);
            this.RaisePropertyChanged(nameof(Depth));
            this.RaisePropertyChanged(nameof(DepthName));
            _sheet.OnGroupDepthEdited(this);
        }

        /// <summary>Уровень группы словом — рядом с полосками, чтобы их не расшифровывать.</summary>
        public string DepthName => CharacterDetailLevels.NameOf(_depth).ToLowerInvariant();

        private bool _isBeyondPreview;

        /// <summary>Подзаголовок глубже уровня показа листа — приглушён.</summary>
        public bool IsBeyondPreview
        {
            get => _isBeyondPreview;
            internal set => this.RaiseAndSetIfChanged(ref _isBeyondPreview, value);
        }

        /// <summary>Колонок столько, сколько полей в строке: ширина делится между ними поровну.</summary>
        public int Columns => Math.Max(1, Cells.Count);

        /// <summary>
        /// Колонок на листе: столько, сколько полей, а пока поле тащат — все
        /// три, чтобы свободные места строки были видны рядом с занятыми.
        /// </summary>
        public int LayoutColumns => _sheet.IsFieldDragging && IsFields ? CharacterAnketaLayout.MaxColumns : Columns;

        internal void RaiseLayoutColumns() => this.RaisePropertyChanged(nameof(LayoutColumns));

        /// <summary>В строке есть место ещё под одно поле — в её конце свободное место.</summary>
        public bool CanTakeMore => IsFields && Cells.Count < CharacterAnketaLayout.MaxColumns;

        private bool _isSlotSelected;

        /// <summary>Выбрано свободное место в конце строки: новое поле из ленты встанет сюда.</summary>
        public bool IsSlotSelected
        {
            get => _isSlotSelected;
            internal set => this.RaiseAndSetIfChanged(ref _isSlotSelected, value);
        }

        private bool _isSlotDropTarget;

        /// <summary>Тащимое поле над свободным местом в конце строки.</summary>
        public bool IsSlotDropTarget
        {
            get => _isSlotDropTarget;
            set => this.RaiseAndSetIfChanged(ref _isSlotDropTarget, value);
        }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

        private bool _isDragSource;
        public bool IsDragSource { get => _isDragSource; set => this.RaiseAndSetIfChanged(ref _isDragSource, value); }

        public CharacterAnketaRow ToRow() => new()
        {
            Kind = Kind,
            Title = _title,
            Cells = IsFields ? Cells.Where(c => c.Field != null).Select(c => c.Field!.Key).ToList() : new List<string>(),
            Depth = IsGroup ? _depth : CharacterDetailLevels.Brief,
            SpacingAfter = IsFields ? _spacingAfter : null
        };

        // ── Отступ под строкой ───────────────────────────────────────────

        private double? _spacingAfter;

        /// <summary>Свой отступ под строкой, пт; null — как у анкеты.</summary>
        public double? SpacingAfter
        {
            get => _spacingAfter;
            internal set
            {
                if (_spacingAfter == value) return;
                _spacingAfter = value;
                RaiseRowMargin();
            }
        }

        /// <summary>Поля строки на листе: обычный зазор плюс отступ строки или анкеты.</summary>
        public Avalonia.Thickness RowMargin
        {
            get
            {
                var extra = IsFields ? (_spacingAfter ?? _sheet.Look?.RowSpacing ?? 0) : 0;
                return new Avalonia.Thickness(0, 1, 0, 1 + Math.Max(0, extra));
            }
        }

        internal void RaiseRowMargin() => this.RaisePropertyChanged(nameof(RowMargin));
    }

    /// <summary>Ячейка строки на листе — место одного поля.</summary>
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
            }
        }

        public bool HasField => _field != null;

        private bool _isDropTarget;

        /// <summary>Тащимое поле над этим: отпущенное, поменяется с ним местами.</summary>
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set => this.RaiseAndSetIfChanged(ref _isDropTarget, value);
        }
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
