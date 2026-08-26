using Avalonia.Media.Imaging;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.ViewModels.Avatars
{
    /// <summary>
    /// Плитка аватарки в пикере.
    ///
    /// Удаление устроено по-разному в зависимости от того, где плитка стоит.
    /// В «Недавних» список только помнит обращения и ничего не хранит, поэтому
    /// крестик убирает запись сразу и молча: картинка остаётся на месте, и
    /// персонажи, которые её носят, остаются с ней. В остальных разделах
    /// крестик удаляет сам файл, и плитка сначала переспрашивает — отменить
    /// это уже нечем.
    /// </summary>
    public class CharacterAvatarPickerItemViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerItemViewModel>();

        public string AvatarRef { get; }
        public string FileName { get; }
        public string ToolTip => System.IO.Path.GetFileNameWithoutExtension(FileName);
        public bool IsProjectAvatar { get; }

        /// <summary>Плитка стоит в разделе «Недавние».</summary>
        public bool IsRecent { get; }

        /// <summary>Крестик у плитки вообще есть.</summary>
        public bool CanDelete { get; }

        // Крестик значит разное в разных разделах, и подсказка обязана это
        // разделять: в «Недавних» он убирает запись списка, в остальных —
        // сам файл.
        public string DeleteTip => IsRecent
            ? "Убрать из недавних"
            : "Удалить картинку";

        public string DeleteTipDescription => IsRecent
            ? "Из списка уйдёт только запись. Картинка останется на месте, и персонажи с этой аватаркой её не потеряют"
            : "Файл удаляется насовсем. Персонажи с этой аватаркой вернутся к заглушке";

        /// <summary>Область хранения — по ней раздел решает, что можно.</summary>
        public CharacterAvatarPackScope Scope { get; }

        private Bitmap? _thumbnail;
        public Bitmap? Thumbnail { get => _thumbnail; private set => this.RaiseAndSetIfChanged(ref _thumbnail, value); }

        private bool _isConfirmingDelete;
        /// <summary>Плитка переспрашивает про удаление и показывает «Да/Нет».</summary>
        public bool IsConfirmingDelete
        {
            get => _isConfirmingDelete;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isConfirmingDelete, value);
                this.RaisePropertyChanged(nameof(IsNotConfirmingDelete));
                this.RaisePropertyChanged(nameof(ShowDeleteButton));
            }
        }

        public bool IsNotConfirmingDelete => !_isConfirmingDelete;

        /// <summary>
        /// Крестик виден. Условий два: удалять вообще есть что и плитка сейчас
        /// не переспрашивает. Раньше их складывала общая панель обеих кнопок —
        /// теперь кнопки стоят по разным углам, и своё условие нужно каждой.
        /// </summary>
        public bool ShowDeleteButton => CanDelete && !_isConfirmingDelete;

        public ReactiveCommand<Unit, Unit> SelectCommand { get; }
        public ReactiveCommand<Unit, Unit> CopyToLibraryCommand { get; }
        public ReactiveCommand<Unit, Unit> CropCommand { get; }
        public ReactiveCommand<Unit, Unit> RequestDeleteCommand { get; }
        public ReactiveCommand<Unit, Unit> ConfirmDeleteCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelDeleteCommand { get; }

        public CharacterAvatarPickerItemViewModel(
            CharacterAvatarItem item,
            ICharacterAvatarService svc,
            Action<string> onSelect,
            Action<string> onDelete,
            Func<string, Task>? onCopyToLibrary = null,
            Func<string, Task>? onCrop = null,
            bool isRecent = false)
        {
            AvatarRef = item.AvatarRef;
            FileName = item.FileName;
            IsProjectAvatar = item.Source == CharacterAvatarSource.Project;
            IsRecent = isRecent;
            Scope = item.Scope;

            // Из «Недавних» убирается запись, из прочих разделов — файл.
            // Встроенные паки лежат в ресурсах сборки, оттуда убрать нечего.
            CanDelete = isRecent || item.CanDelete;

            SelectCommand = ReactiveCommand.Create(() => onSelect(AvatarRef));

            CopyToLibraryCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (onCopyToLibrary != null) await onCopyToLibrary(AvatarRef);
            });

            CropCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (onCrop != null) await onCrop(AvatarRef);
            });

            RequestDeleteCommand = ReactiveCommand.Create(() =>
            {
                if (!CanDelete) return;

                // «Недавние» не переспрашивают: удалять там нечего, запись
                // всегда можно нажить заново, поставив аватарку ещё раз.
                if (IsRecent) { onDelete(AvatarRef); return; }

                IsConfirmingDelete = true;
            });

            ConfirmDeleteCommand = ReactiveCommand.Create(() =>
            {
                IsConfirmingDelete = false;
                if (CanDelete) onDelete(AvatarRef);
            });

            // Тело в скобках, а не выражением: присваивание как выражение
            // отдаёт значение, и команда собралась бы как ReactiveCommand
            // с результатом bool вместо Unit.
            CancelDeleteCommand = ReactiveCommand.Create(() => { IsConfirmingDelete = false; });

            try { Thumbnail = svc.LoadBitmap(AvatarRef); }
            catch (Exception ex) { _logger.Error(ex, "Thumbnail failed {Ref}", AvatarRef); }
        }

        public bool MatchesSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            return ToolTip.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() => _thumbnail?.Dispose();
    }

    /// <summary>
    /// Раздел пикера: «Недавние», аватарки проекта или один пак.
    ///
    /// Область хранения показывается подписью у заголовка — так же, как у
    /// палитр цветов: локальное уезжает вместе с проектом, глобальное живёт в
    /// настройках приложения и видно во всех проектах.
    /// </summary>
    public class CharacterAvatarPackSectionViewModel : ReactiveObject
    {
        public string PackId { get; }
        public string DisplayName { get; }
        public bool IsBuiltIn { get; }
        public bool IsRecents { get; }
        public CharacterAvatarPackScope Scope { get; }
        public ObservableCollection<CharacterAvatarPickerItemViewModel> Items { get; } = new();

        private Bitmap? _iconBitmap;
        public Bitmap? IconBitmap { get => _iconBitmap; private set => this.RaiseAndSetIfChanged(ref _iconBitmap, value); }

        public bool HasItems => Items.Any();
        public bool HasNoItems => !Items.Any();

        /// <summary>
        /// Подпись области у заголовка раздела. У «Недавних» её нет: список
        /// не хранилище, и говорить о его области нечего.
        /// </summary>
        public string ScopeLabel
        {
            get
            {
                if (IsRecents) return string.Empty;
                if (IsBuiltIn) return CharacterAvatarScopeText.BuiltIn;
                return CharacterAvatarScopeText.Label(Scope);
            }
        }

        public bool HasScopeLabel => !string.IsNullOrEmpty(ScopeLabel);

        // Цвет значка области задаётся классом, а не кистью из модели: кисти
        // в разметке уже разложены по темам, и вести их второй раз отсюда
        // значило бы держать два списка цветов вместо одного.
        public bool IsLocalScope =>
            !IsRecents && !IsBuiltIn && Scope == CharacterAvatarPackScope.Local;

        public bool IsGlobalScope =>
            !IsRecents && !IsBuiltIn && Scope == CharacterAvatarPackScope.Global;

        public CharacterAvatarPackSectionViewModel(
            CharacterAvatarPackInfo pack,
            ICharacterAvatarService svc,
            Action<string> onSelect,
            Action<string> onDelete,
            Func<string, Task>? onCopyToLibrary = null,
            Func<string, Task>? onCrop = null)
        {
            PackId = pack.Id;
            IsBuiltIn = pack.Source == CharacterAvatarPackSource.BuiltIn;
            IsRecents = false;
            Scope = pack.Scope;

            // Встроенные: локализация через CharactersStrings
            // Пользовательские: Name из pack.json
            DisplayName = ResolveDisplayName(pack);

            if (!string.IsNullOrEmpty(pack.IconRef))
                try { IconBitmap = svc.LoadBitmap(pack.IconRef); } catch { }

            foreach (var item in pack.Items)
                Items.Add(new CharacterAvatarPickerItemViewModel(
                    item, svc, onSelect, onDelete, onCopyToLibrary, onCrop));
        }

        private static string ResolveDisplayName(CharacterAvatarPackInfo pack)
        {
            if (pack.Source == CharacterAvatarPackSource.BuiltIn)
            {
                // Ключ в CharactersStrings: AvatarPack_people_minimalism
                var localized = CharactersStrings.ResourceManager
                    .GetString(pack.LocalizationKey, CharactersStrings.Culture);
                return localized ?? pack.Id;
            }

            // Пользовательский: имя из pack.json
            if (pack.Id == "__library__")
            {
                return CharactersStrings.ResourceManager
                    .GetString("AvatarPack_library", CharactersStrings.Culture)
                    ?? "Мои аватарки";
            }

            return pack.Name ?? pack.Id;
        }

        // Подписи областей собраны в CharacterAvatarScopeText: их показывают и
        // пикер, и менеджер, и списки переноса, и разъехавшиеся формулировки
        // читались бы как разные вещи.
        internal static string Localized(string key, string fallback)
            => CharacterAvatarScopeText.Localized(key, fallback);

        public bool MatchesSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            return DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Items.Any(i => i.ToolTip.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            _iconBitmap?.Dispose();
            foreach (var i in Items) i.Dispose();
        }
    }

    public class CharacterAvatarPickerViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPickerViewModel>();

        private readonly ICharacterAvatarService _avatarService;
        public string CharacterId { get; }

        public ObservableCollection<CharacterAvatarPickerItemViewModel> RecentAvatars { get; } = new();
        public ObservableCollection<CharacterAvatarPickerItemViewModel> VisibleRecentAvatars { get; } = new();
        public ObservableCollection<CharacterAvatarPickerItemViewModel> ProjectAvatars { get; } = new();
        public ObservableCollection<CharacterAvatarPickerItemViewModel> VisibleProjectAvatars { get; } = new();
        public ObservableCollection<CharacterAvatarPackSectionViewModel> Packs { get; } = new();
        public ObservableCollection<CharacterAvatarPackSectionViewModel> VisiblePacks { get; } = new();

        public bool HasRecentAvatars => VisibleRecentAvatars.Any();
        public bool HasProjectAvatars => VisibleProjectAvatars.Any();
        public bool HasNoProjectAvatars => !VisibleProjectAvatars.Any();
        public bool HasPacks => Packs.Any();

        public string RecentsTitle => CharacterAvatarPackSectionViewModel
            .Localized("AvatarPicker_Recents", "Недавние");

        public event Action<string>? AvatarSelected;
        public event Action? CloseRequested;
        public event Action? OpenManagerRequested;

        private string _searchQuery = string.Empty;
        public string SearchQuery
        {
            get => _searchQuery;
            set { this.RaiseAndSetIfChanged(ref _searchQuery, value); ApplySearch(); }
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private bool _isDropTarget;
        /// <summary>
        /// Над окном тянут файл. Подсветка идёт по всей панели: приёмником
        /// объявлено окно целиком, и рамка должна показывать именно это, а не
        /// какой-то один раздел внутри.
        /// </summary>
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set => this.RaiseAndSetIfChanged(ref _isDropTarget, value);
        }

        public ReactiveCommand<Unit, Unit> UploadCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenManagerCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearRecentsCommand { get; }

        public Func<Task<(byte[] data, string name)?>>? RequestFilePicker { get; set; }

        /// <summary>
        /// Показать окно обрезки для ещё не сохранённой картинки. Возвращает
        /// кадр или null, если обрезку отменили — тогда картинка не попадает
        /// в хранилище вовсе.
        /// </summary>
        public Func<byte[], CharacterAvatarCrop?, Task<CharacterAvatarCropPair?>>? RequestCropForBytes { get; set; }

        /// <summary>Показать окно обрезки для уже сохранённой аватарки.</summary>
        public Func<string, Task<CharacterAvatarCropPair?>>? RequestCropForRef { get; set; }

        public CharacterAvatarPickerViewModel(ICharacterAvatarService avatarService, string characterId)
        {
            _avatarService = avatarService;
            CharacterId = characterId;

            UploadCommand = ReactiveCommand.CreateFromTask(UploadAsync);
            CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
            OpenManagerCommand = ReactiveCommand.Create(() => OpenManagerRequested?.Invoke());
            ClearRecentsCommand = ReactiveCommand.Create(() =>
            {
                _avatarService.ClearRecentAvatars();
                Refresh();
            });

            Refresh();
        }

        public void Refresh()
        {
            foreach (var p in Packs) p.Dispose();
            foreach (var i in ProjectAvatars) i.Dispose();
            foreach (var i in RecentAvatars) i.Dispose();

            RecentAvatars.Clear();
            VisibleRecentAvatars.Clear();
            ProjectAvatars.Clear();
            VisibleProjectAvatars.Clear();
            Packs.Clear();
            VisiblePacks.Clear();

            foreach (var item in _avatarService.GetRecentAvatars())
                RecentAvatars.Add(new CharacterAvatarPickerItemViewModel(
                    item, _avatarService,
                    onSelect: SelectAvatar,
                    onDelete: RemoveFromRecents,
                    onCopyToLibrary: null,
                    onCrop: CropStoredAsync,
                    isRecent: true));

            foreach (var item in _avatarService.GetProjectAvatars())
                ProjectAvatars.Add(new CharacterAvatarPickerItemViewModel(
                    item, _avatarService,
                    onSelect: SelectAvatar,
                    onDelete: DeleteStored,
                    onCopyToLibrary: CopyToLibraryAsync,
                    onCrop: CropStoredAsync));

            foreach (var pack in _avatarService.GetAllPacks())
            {
                var section = new CharacterAvatarPackSectionViewModel(
                    pack, _avatarService,
                    onSelect: SelectAvatar,
                    onDelete: DeleteStored,
                    onCopyToLibrary: null,
                    onCrop: CropStoredAsync);
                Packs.Add(section);
            }

            ApplySearch();
            RaiseSectionFlags();
        }

        private void RaiseSectionFlags()
        {
            this.RaisePropertyChanged(nameof(HasRecentAvatars));
            this.RaisePropertyChanged(nameof(HasProjectAvatars));
            this.RaisePropertyChanged(nameof(HasNoProjectAvatars));
            this.RaisePropertyChanged(nameof(HasPacks));
        }

        /// <summary>
        /// Аватарка выбрана.
        ///
        /// Картинка из библиотеки или глобального пака ложится в проект прямо
        /// здесь, а персонаж получает ссылку на уложенную копию. Иначе проект
        /// оказывался бы наполовину чужим: на карточке аватарка есть, а лежит
        /// она в %AppData% автора, и у того, кому проект передали, на её месте
        /// пустой кружок без единого слова о том, чего не хватает.
        ///
        /// Исходник при этом никуда не девается: глобальное хранилище — это
        /// библиотека, из которой берут, а не место, на которое ссылаются.
        ///
        /// В «Недавних» остаётся именно исходная ссылка, а не проектная копия:
        /// список общий для всех проектов, и запись, ведущая в архив соседнего
        /// проекта, в этом не поможет никому.
        /// </summary>
        private async void SelectAvatar(string avatarRef)
        {
            var chosen = avatarRef;

            try
            {
                chosen = await _avatarService.EnsureInProjectAsync(avatarRef) ?? avatarRef;
            }
            catch (Exception ex)
            {
                // Уложить не вышло — персонаж получает исходную ссылку. Она
                // работает у автора, а о том, что картинка осталась снаружи,
                // скажет проверка перед передачей проекта.
                _logger.Error(ex, "SelectAvatar: cannot store {Ref} in project", avatarRef);
            }

            _avatarService.AddRecentAvatar(avatarRef);
            AvatarSelected?.Invoke(chosen);
            CloseRequested?.Invoke();
        }

        /// <summary>
        /// Убрать запись из «Недавних». Ни файл, ни персонажи не затрагиваются —
        /// список только помнит порядок обращений.
        /// </summary>
        private void RemoveFromRecents(string avatarRef)
        {
            _avatarService.RemoveRecentAvatar(avatarRef);
            Refresh();
        }

        /// <summary>
        /// Удалить саму картинку из хранилища. Персонажи, у которых она стоит,
        /// вернутся к заглушке — подтверждение спрашивает плитка до вызова.
        /// </summary>
        private void DeleteStored(string avatarRef)
        {
            _avatarService.DeleteAvatar(avatarRef);
            _avatarService.RemoveRecentAvatar(avatarRef);
            StatusMessage = CharacterAvatarPackSectionViewModel
                .Localized("AvatarPicker_Deleted", "Аватарка удалена.");
            Refresh();
        }

        /// <summary>
        /// Переснять кадр у уже сохранённой картинки и поставить результат
        /// персонажу. Файл при этом не копируется: кадр живёт в ссылке.
        /// </summary>
        private async Task CropStoredAsync(string avatarRef)
        {
            if (RequestCropForRef == null) { SelectAvatar(avatarRef); return; }

            var crops = await RequestCropForRef(avatarRef);
            if (crops == null) return;

            var combined = CharacterAvatarRef.Combine(avatarRef, crops.Circle, crops.Strip);
            if (combined != null) SelectAvatar(combined);
        }

        private async Task CopyToLibraryAsync(string projectRef)
        {
            var libRef = await _avatarService.CopyProjectAvatarToLibraryAsync(projectRef);
            if (libRef != null)
            {
                StatusMessage = CharactersStrings.ResourceManager
                    .GetString("AvatarPicker_SavedToLibrary") ?? "Сохранено в библиотеку.";
                Refresh();
            }
        }

        /// <summary>
        /// Принять новую картинку: из окна выбора файла или брошенную на окно.
        ///
        /// Порядок шагов важен. Сначала ищется уже сохранённая копия — одна и
        /// та же фотография не должна ложиться в проект дважды. Затем идёт
        /// обрезка: отменённая обрезка не должна оставлять за собой файл, а
        /// значит сохранение может идти только после неё.
        /// </summary>
        public async Task HandleImageBytesAsync(byte[] imageData, string fileName)
        {
            if (imageData == null || imageData.Length == 0) return;

            string? baseRef;
            try { baseRef = _avatarService.FindStoredByContent(imageData); }
            catch (Exception ex)
            {
                _logger.Error(ex, "FindStoredByContent failed");
                baseRef = null;
            }

            var reused = baseRef != null;

            var crops = await RequestCropAsync(imageData, null);
            if (crops == null) return;

            if (!reused)
            {
                StatusMessage = CharactersStrings.ResourceManager
                    .GetString("AvatarPicker_Saving") ?? "Сохранение…";
                baseRef = await _avatarService.SaveToProjectAsync(imageData, fileName);
                if (baseRef == null)
                {
                    StatusMessage = CharactersStrings.ResourceManager
                        .GetString("AvatarPicker_SaveFailed") ?? "Не удалось сохранить.";
                    return;
                }
            }
            else
            {
                StatusMessage = CharacterAvatarPackSectionViewModel
                    .Localized("AvatarPicker_Reused", "Такая картинка уже есть — взята она.");
            }

            var combined = CharacterAvatarRef.Combine(baseRef, crops.Circle, crops.Strip);
            if (combined != null) SelectAvatar(combined);
        }

        /// <summary>
        /// Обрезка спрашивается всегда. Если окно обрезки не подключено —
        /// картинка берётся целиком: остаться без аватарки из-за неподключённого
        /// окна хуже, чем взять её неподрезанной.
        /// </summary>
        private async Task<CharacterAvatarCropPair?> RequestCropAsync(byte[] imageData, CharacterAvatarCrop? initial)
        {
            if (RequestCropForBytes == null)
                return new CharacterAvatarCropPair(CharacterAvatarCrop.Full, null);
            return await RequestCropForBytes(imageData, initial);
        }

        private async Task UploadAsync()
        {
            if (RequestFilePicker == null) return;
            var result = await RequestFilePicker();
            if (result != null) await HandleImageBytesAsync(result.Value.data, result.Value.name);
        }

        private void ApplySearch()
        {
            VisibleRecentAvatars.Clear();
            foreach (var item in RecentAvatars)
                if (item.MatchesSearch(_searchQuery))
                    VisibleRecentAvatars.Add(item);

            VisibleProjectAvatars.Clear();
            foreach (var item in ProjectAvatars)
                if (item.MatchesSearch(_searchQuery))
                    VisibleProjectAvatars.Add(item);

            VisiblePacks.Clear();
            foreach (var pack in Packs)
                if (pack.MatchesSearch(_searchQuery))
                    VisiblePacks.Add(pack);

            RaiseSectionFlags();
        }
    }
}
