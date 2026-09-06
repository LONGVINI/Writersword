using Avalonia.Media.Imaging;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.ViewModels.Inspector
{
    /// <summary>
    /// Плитка быстрой ленты. Щелчок ставит аватарку сразу — ни окна выбора, ни
    /// окна обрезки. Кадр берётся тот, что записан в самой ссылке: у картинки
    /// из папки его нет, и она встаёт целиком, а у недавней он уже выбран
    /// прошлым разом и повторять выбор незачем.
    /// </summary>
    public class CharacterQuickAvatarTile : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterQuickAvatarTile>();

        private readonly ICharacterAvatarService _service;
        private readonly Action<string> _pick;

        private Bitmap? _thumbnail;
        private bool _thumbnailRequested;

        public CharacterQuickAvatarTile(
            CharacterAvatarItem item,
            ICharacterAvatarService service,
            Action<string> pick,
            Func<string, Task>? onCrop = null)
        {
            AvatarRef = item.AvatarRef;
            FileName = item.FileName;
            _service = service;
            _pick = pick;

            PickCommand = ReactiveCommand.Create(() => { _pick(AvatarRef); });

            // Отдельная кнопка-коррекция на плитке, независимая от щелчка по
            // самой картинке (тот же ход, что и в полном окне выбора —
            // CharacterAvatarPickerOverlay): подобрать свой кадр этой же
            // картинки, а не ставить её как есть.
            CropCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (onCrop != null) await onCrop(AvatarRef);
            });
        }

        public string AvatarRef { get; }
        public string FileName { get; }
        public string ToolTip => System.IO.Path.GetFileNameWithoutExtension(FileName);

        public ReactiveCommand<Unit, Unit> PickCommand { get; }
        public ReactiveCommand<Unit, Unit> CropCommand { get; }

        /// <summary>
        /// Миниатюра грузится при первом показе, а не заранее для всех папок
        /// разом — так лента остаётся лёгкой, даже когда список общий и
        /// непрерывный (см. CharacterQuickAvatarsViewModel).
        /// </summary>
        public Bitmap? Thumbnail
        {
            get
            {
                if (!_thumbnailRequested)
                {
                    _thumbnailRequested = true;

                    // Уже построенную отдаём тем же кадром: иначе плитки,
                    // которые только что были на экране, мигали бы заглушкой
                    // при каждой прокрутке туда-обратно.
                    _thumbnail = _service.TryGetThumbnail(AvatarRef, 96);
                    if (_thumbnail == null) RequestThumbnail();
                }
                return _thumbnail;
            }
        }

        /// <summary>Миниатюра готова. Пока нет — плитка показывает заглушку.</summary>
        public bool HasThumbnail => _thumbnail != null;

        /// <summary>
        /// Построить миниатюру в стороне от UI-потока. Прокрутка реализует
        /// новые плитки прямо во время движения, и раскодирование на месте
        /// останавливало её на каждой новой строке.
        /// </summary>
        private async void RequestThumbnail()
        {
            Bitmap? bitmap = null;
            try { bitmap = await _service.LoadThumbnailAsync(AvatarRef, 96); }
            catch (Exception ex) { _logger.Error(ex, "Quick tile thumb failed for {Ref}", AvatarRef); }

            if (bitmap == null) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _thumbnail = bitmap;
                this.RaisePropertyChanged(nameof(Thumbnail));
                this.RaisePropertyChanged(nameof(HasThumbnail));
            });
        }
    }

    /// <summary>
    /// Раздел быстрой ленты («Недавние», «В проекте» или пак) со своими уже
    /// готовыми плитками. Список ленты общий и непрерывный, как в
    /// стикерпикере телеграма: раздел не прячет остальные, чип папки снизу
    /// только прокручивает список к своему месту (см. RequestScrollToFolder
    /// у CharacterQuickAvatarsViewModel).
    /// </summary>
    /// <summary>
    /// Что за раздел стоит в ленте. Нужен полоске папок внизу: у папки есть
    /// обложка, а у «Недавних» и «В проекте» её быть не может — это не папки, а
    /// выборки, и показывать им чужую картинку нельзя. Разбор по названию
    /// раздела сломался бы от первой же папки, названной «Недавние».
    /// </summary>
    public enum CharacterQuickAvatarFolderKind { Recent, Project, Pack }

    public class CharacterQuickAvatarFolder : ReactiveObject
    {
        public CharacterQuickAvatarFolder(
            string title,
            Bitmap? icon,
            IReadOnlyList<CharacterAvatarItem> items,
            ICharacterAvatarService service,
            Action<string> pick,
            Func<string, Task>? onCrop,
            CharacterQuickAvatarFolderKind kind = CharacterQuickAvatarFolderKind.Pack)
        {
            Title = title;
            Icon = icon;
            Kind = kind;
            Tiles = items
                .Select(item => new CharacterQuickAvatarTile(item, service, pick, onCrop))
                .ToList();
        }

        public string Title { get; }
        public Bitmap? Icon { get; }
        public CharacterQuickAvatarFolderKind Kind { get; }
        public IReadOnlyList<CharacterQuickAvatarTile> Tiles { get; }

        /// <summary>У раздела есть обложка — её и показываем в полоске папок.</summary>
        public bool HasIcon => Icon != null;

        public bool IsRecent => Kind == CharacterQuickAvatarFolderKind.Recent;

        /// <summary>
        /// Папка без обложки: показывается значком папки. Пустой кружок на её
        /// месте читался бы как непрогрузившаяся картинка.
        /// </summary>
        public bool ShowFolderGlyph => !HasIcon && !IsRecent;
    }

    /// <summary>
    /// Быстрая лента аватарок в боковой панели: один непрерывный список
    /// картинок по разделам (недавние, в проекте, паки), а не фильтр —
    /// щёлкнутая внизу папка прокручивает список к своему разделу, а не
    /// прячет остальные (как в стикерпикере телеграма). Щелчок по картинке
    /// ставит её персонажу немедленно.
    ///
    /// Своя вью-модель, а не CharacterAvatarPickerViewModel: у той поиск,
    /// удаление, переспросы, обрезка при выборе и закрытие окна по результату —
    /// всё это здесь мешало бы. Общее у них только служба и модель картинки.
    /// </summary>
    public class CharacterQuickAvatarsViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterQuickAvatarsViewModel>();

        private readonly Action<string> _pick;
        private ICharacterAvatarService? _service;

        public CharacterQuickAvatarsViewModel(Action<string> pick)
        {
            _pick = pick;
        }

        /// <summary>Показать окно обрезки для уже сохранённой аватарки —
        /// заводится снаружи (CharacterInspectorPanel.axaml.cs), у самой
        /// вью-модели нет доступа до CharacterAvatarCropOverlay, который
        /// хостится на уровне модуля.</summary>
        public Func<string, Task<CharacterAvatarCropPair?>>? RequestCropForRef { get; set; }

        /// <summary>Прокрутить общий список к разделу папки — тоже заводится
        /// снаружи: у вью-модели нет доступа к визуальному дереву, только у
        /// code-behind панели.</summary>
        public Action<CharacterQuickAvatarFolder>? RequestScrollToFolder { get; set; }

        /// <summary>
        /// Переснять кадр у уже сохранённой картинки и сразу поставить
        /// результат персонажу — тот же ход, что и «Кадрировать» в полном
        /// окне выбора (CharacterAvatarPickerViewModel.CropStoredAsync):
        /// файл не копируется, кадр живёт в самой ссылке.
        /// </summary>
        private async Task CropStoredAsync(string avatarRef)
        {
            if (RequestCropForRef == null) { PickStored(avatarRef); return; }

            var crops = await RequestCropForRef(avatarRef);
            if (crops == null) return;

            var combined = CharacterAvatarRef.Combine(avatarRef, crops.Circle, crops.Strip);
            if (combined != null) PickStored(combined);
        }

        /// <summary>
        /// Поставить аватарку персонажу, уложив её в проект.
        ///
        /// Лента показывает и библиотеку, и глобальные паки — то есть то, чего
        /// у получателя проекта не будет. Копия ложится в архив сразу при
        /// выборе, и персонаж получает ссылку на неё; исходник остаётся общим
        /// для всех проектов и никуда не девается.
        ///
        /// Тот же ход, что и в полном окне выбора: способов поставить аватарку
        /// два, а правило самодостаточности проекта одно.
        /// </summary>
        private async void PickStored(string avatarRef)
        {
            var chosen = avatarRef;

            try
            {
                if (_service != null)
                    chosen = await _service.EnsureInProjectAsync(avatarRef) ?? avatarRef;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Quick pick: cannot store {Ref} in project", avatarRef);
            }

            _pick(chosen);
        }

        /// <summary>
        /// Служба аватарок текущей загрузки — нужна панели, чтобы открыть
        /// менеджер папок из флаута ленты тем же способом, что и полное окно
        /// выбора аватарки (CharacterAvatarPickerOverlay).
        /// </summary>
        public ICharacterAvatarService? AvatarService => _service;

        public ObservableCollection<CharacterQuickAvatarFolder> Folders { get; } = new();

        public bool HasFolders => Folders.Count > 0;
        public bool HasNoFolders => Folders.Count == 0;

        /// <summary>Щелчок по чипу папки — прокрутить список к её разделу.
        /// Ничего не фильтрует и не скрывает: список общий и непрерывный.</summary>
        public void OpenFolder(CharacterQuickAvatarFolder? folder)
        {
            if (folder is null) return;
            RequestScrollToFolder?.Invoke(folder);
        }

        /// <summary>
        /// Перечитать папки. Список недавних меняется от каждой поставленной
        /// аватарки, поэтому лента пересобирается при каждой смене выделения, а
        /// не один раз при создании.
        /// </summary>
        public void Reload(ICharacterAvatarService? service)
        {
            _service = service;
            Folders.Clear();

            if (service is null)
            {
                RaiseFlags();
                return;
            }

            try
            {
                var recents = service.GetRecentAvatars();
                if (recents.Count > 0)
                    Folders.Add(new CharacterQuickAvatarFolder(
                        "Недавние", null, recents, service, PickStored, CropStoredAsync,
                        CharacterQuickAvatarFolderKind.Recent));

                var project = service.GetProjectAvatars();
                if (project.Count > 0)
                    Folders.Add(new CharacterQuickAvatarFolder(
                        "В проекте", null, project, service, PickStored, CropStoredAsync,
                        CharacterQuickAvatarFolderKind.Project));

                foreach (var pack in service.GetAllPacks())
                {
                    if (pack.Items is null || pack.Items.Count == 0) continue;

                    Bitmap? icon = null;
                    if (!string.IsNullOrEmpty(pack.IconRef))
                        try { icon = service.LoadThumbnail(pack.IconRef, 64); } catch { }

                    Folders.Add(new CharacterQuickAvatarFolder(
                        PackTitle(pack), icon, pack.Items.ToList(), service, PickStored, CropStoredAsync));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Quick avatars reload failed");
            }

            RaiseFlags();
        }

        private static string PackTitle(CharacterAvatarPackInfo pack)
        {
            if (!string.IsNullOrWhiteSpace(pack.Name)) return pack.Name!;
            if (!string.IsNullOrWhiteSpace(pack.LocalizationKey))
            {
                var localized = Src.Modules.Characters.Resources.CharactersStrings.ResourceManager
                    .GetString(pack.LocalizationKey, Src.Modules.Characters.Resources.CharactersStrings.Culture);
                if (!string.IsNullOrWhiteSpace(localized)) return localized!;
            }
            return pack.Id;
        }

        private void RaiseFlags()
        {
            this.RaisePropertyChanged(nameof(HasFolders));
            this.RaisePropertyChanged(nameof(HasNoFolders));
        }
    }
}
