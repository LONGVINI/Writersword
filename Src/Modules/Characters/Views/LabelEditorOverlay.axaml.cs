using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Serilog;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Views
{
    /// <summary>Вариант значка в наборе редактора метки.</summary>
    public sealed class LabelIconOption : ReactiveObject
    {
        public string Key { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        /// <summary>
        /// Метка-образец для показа этого варианта. Набор рисуется тем же
        /// контролом, что и сама метка на карточках: иначе значок в наборе и
        /// значок на карточке снова разъедутся, как это уже было.
        ///
        /// Подложки у образца нет — она к выбору фигуры отношения не имеет
        /// и только мешала бы разглядеть очертание.
        /// </summary>
        public CharacterLabel Sample { get; } = new();

        public LabelIconOption(string key)
        {
            Key = key;
            Sample.Icon = key;
            Sample.ShowBackdrop = false;
        }

        /// <summary>Показать набор в текущем цвете фигуры.</summary>
        public void SetColor(string color)
        {
            Sample.IconColor = color;
            this.RaisePropertyChanged(nameof(Sample));
        }
    }

    /// <summary>
    /// Черновик метки: правки живут здесь и применяются к персонажу только
    /// по OK. «Отмена»/крестик закрывают окно без изменений.
    /// </summary>
    public sealed class LabelEditorDraft : ReactiveObject
    {
        private string _name = string.Empty;
        public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }

        private string _icon = CharacterLabelIcons.Dot;
        public string Icon
        {
            get => _icon;
            set
            {
                this.RaiseAndSetIfChanged(ref _icon, value);
                RaisePreviewChanged();
            }
        }

        private string _color = "#607D8B";
        public string Color
        {
            get => _color;
            set
            {
                this.RaiseAndSetIfChanged(ref _color, value);
                RaisePreviewChanged();
            }
        }

        /// <summary>
        /// Цвет фигуры. В черновике всегда непустой — редактору цвета
        /// нечего показывать в пустом поле, — а в метку белый уезжает
        /// пустотой: см. IconColorOrEmpty.
        /// </summary>
        private string _iconColor = DefaultIconColor;
        public string IconColor
        {
            get => _iconColor;
            set
            {
                this.RaiseAndSetIfChanged(ref _iconColor, value);
                // Набор встроенных значков показывается без кружка, в цвете
                // фигуры: выбирают там именно очертание.
                foreach (var option in Icons) option.SetColor(value);
                RaisePreviewChanged();
            }
        }

        /// <summary>Рисовать кружок под фигурой.</summary>
        private bool _showBackdrop = true;
        public bool ShowBackdrop
        {
            get => _showBackdrop;
            set
            {
                this.RaiseAndSetIfChanged(ref _showBackdrop, value);
                RaisePreviewChanged();
            }
        }

        public const string DefaultIconColor = "#FFFFFF";

        /// <summary>
        /// Белый — вид по умолчанию, и в метке он хранится пустотой: иначе
        /// метка, у которой цвет фигуры никто не трогал, отличалась бы от
        /// метки, где белый выбрали руками.
        /// </summary>
        public string? IconColorOrEmpty =>
            string.Equals(_iconColor, DefaultIconColor, StringComparison.OrdinalIgnoreCase)
                ? null
                : _iconColor;

        private bool _dim;
        public bool Dim { get => _dim; set => this.RaiseAndSetIfChanged(ref _dim, value); }

        private bool _showOnCard = true;
        public bool ShowOnCard { get => _showOnCard; set => this.RaiseAndSetIfChanged(ref _showOnCard, value); }

        private string _description = string.Empty;
        public string Description { get => _description; set => this.RaiseAndSetIfChanged(ref _description, value); }

        /// <summary>
        /// Своя картинка вместо встроенного значка. Ключ значка при этом
        /// не теряется: убрал картинку — вернулся прежний значок.
        /// </summary>
        private string? _iconImage;
        public string? IconImage
        {
            get => _iconImage;
            set
            {
                this.RaiseAndSetIfChanged(ref _iconImage, value);
                this.RaisePropertyChanged(nameof(HasCustomIcon));
                RaisePreviewChanged();
            }
        }

        public bool HasCustomIcon => !string.IsNullOrWhiteSpace(_iconImage);

        public ObservableCollection<LabelIconOption> Icons { get; } = new();

        /// <summary>
        /// Метка в текущем состоянии черновика — для превью. Превью рисуется
        /// тем же контролом, что и значок на карточке, поэтому показывает
        /// ровно то, что получится: и цвет фигуры, и подложку, и свою
        /// картинку, включая перекрашенный вектор.
        /// </summary>
        public CharacterLabel Preview => new()
        {
            Name = Name,
            Icon = Icon,
            Color = Color,
            IconImage = IconImage,
            IconColor = IconColorOrEmpty,
            ShowBackdrop = ShowBackdrop
        };

        private void RaisePreviewChanged() => this.RaisePropertyChanged(nameof(Preview));
    }

    /// <summary>
    /// Окно создания/редактирования метки персонажа: по центру модуля, со
    /// скримом — как окно настроек карточки. Применение — колбэком, чтобы
    /// окно не знало о вьюмоделях вкладок.
    /// </summary>
    public partial class LabelEditorOverlay : UserControl
    {
        private static readonly string[] IconSet =
        {
            CharacterLabelIcons.Dot,
            CharacterLabelIcons.Cross,
            CharacterLabelIcons.Skull,
            CharacterLabelIcons.Drop,
            CharacterLabelIcons.Star,
            CharacterLabelIcons.Crown,
            CharacterLabelIcons.Heart,
            CharacterLabelIcons.Flag,
            CharacterLabelIcons.Lock,
            CharacterLabelIcons.Bolt,
            CharacterLabelIcons.Eye,
            CharacterLabelIcons.Shield,
            CharacterLabelIcons.Moon,
            CharacterLabelIcons.Check
        };

        // Растровые форматы плюс вектор. Вектор перекрашивается в цвет метки,
        // растр идёт как есть: перекрашивать чужой герб программа не берётся.
        private static readonly FilePickerFileType IconFileType = new("Картинки значка")
        {
            Patterns = new[]
            {
                "*.png", "*.jpg", "*.jpeg", "*.webp",
                "*.bmp", "*.gif", "*.ico", "*.svg"
            }
        };

        private static readonly ILogger _logger = Log.ForContext<LabelEditorOverlay>();

        private LabelEditorDraft? _draft;
        private CharacterLabel? _original;
        private Action<CharacterLabel>? _apply;

        public LabelEditorOverlay()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Показать редактор. label == null — создание новой метки;
        /// иначе — правка существующей (Id и Order сохраняются).
        /// Колбэк apply вызывается по OK с готовой меткой.
        ///
        /// Признака «сделать вид общим» больше нет: вид метки общий всегда.
        /// Галка, которая им управляла, называлась «применить ко всем
        /// персонажам» и читалась как «повесить метку на всех» — чего она не
        /// делала никогда.
        /// </summary>
        public void ShowFor(CharacterLabel? label, Action<CharacterLabel> apply, string? initialName = null)
        {
            _original = label;
            _apply = apply;
            _draft = new LabelEditorDraft
            {
                // Имя, набранное в быстром выборе и не нашедшее метки, уезжает
                // сюда: раз его набрали и не нашли, метку заводят именно с ним.
                Name = label?.Name ?? initialName?.Trim() ?? string.Empty,
                Icon = label?.Icon ?? CharacterLabelIcons.Dot,
                Color = label?.Color ?? "#607D8B",
                Dim = label?.Effect == CharacterLabelEffect.Dim,
                ShowOnCard = label?.ShowOnCard ?? true,
                Description = label?.Description ?? string.Empty,
                IconImage = label?.IconImage,
                IconColor = string.IsNullOrWhiteSpace(label?.IconColor)
                    ? LabelEditorDraft.DefaultIconColor
                    : label!.IconColor!,
                ShowBackdrop = label?.ShowBackdrop ?? true
            };

            foreach (var key in IconSet)
            {
                var option = new LabelIconOption(key) { IsSelected = key == _draft.Icon };
                option.SetColor(_draft.IconColor);
                _draft.Icons.Add(option);
            }

            DataContext = _draft;
            IsVisible = true;

            // «Ещё» закрывается при каждом открытии: раскрытым его оставляют
            // на один раз, а видеть короткое окно нужно всегда.
            SetMoreOpen(false);

            // Красная рамка от прошлого захода сюда не тянется.
            ClearNameRequired();


            // Курсор сразу в имени: у новой метки это первое и часто
            // единственное, что вписывают.
            Dispatcher.UIThread.Post(() =>
            {
                var box = this.FindControl<TextBox>("NameBox");
                box?.Focus();
                box?.SelectAll();
            }, DispatcherPriority.Background);
        }

        // ── «Ещё» ─────────────────────────────────────────────────────────

        private bool _moreOpen;

        private void OnToggleMoreClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            SetMoreOpen(!_moreOpen);
        }

        private void SetMoreOpen(bool open)
        {
            _moreOpen = open;

            var body = this.FindControl<StackPanel>("MoreBody");
            if (body != null) body.IsVisible = open;

            // Стрелка смотрит вправо, пока закрыто, и вниз, когда раскрыто —
            // как у разделов инспектора.
            var chev = this.FindControl<Avalonia.Controls.Shapes.Path>("MoreChev");
            if (chev != null)
                chev.RenderTransform = new RotateTransform(open ? 90 : 0);
        }

        // Enter в имени — то же, что ОК: метка это имя и значок, и тянуться
        // мышью к кнопке ради подтверждения незачем.
        private void OnNameKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            OnOkClick(sender, e);
        }

        // ── Имя обязательно ───────────────────────────────────────────────

        /// <summary>
        /// Сказать, что без имени метку не завести: поле краснеет, курсор
        /// встаёт в него. Окно при этом остаётся открытым — всё подобранное
        /// в нём никуда не девается.
        ///
        /// Строки «впишите имя» под полем нет намеренно. Красная рамка и
        /// прыгнувший внутрь курсор показывают и место, и требование; фраза
        /// рядом с ними только пересказывает словами уже показанное и на
        /// секунду удлиняет окно, сдвигая всё под ней.
        /// </summary>
        private void ShowNameRequired()
        {
            var box = this.FindControl<TextBox>("NameBox");
            if (box == null) return;

            if (!box.Classes.Contains("invalid")) box.Classes.Add("invalid");
            box.Focus();
        }

        /// <summary>
        /// Убрать красную рамку. Зовётся на первую же букву: человек уже
        /// делает то, о чём его попросили, и рамка под рукой становится
        /// придиркой.
        /// </summary>
        private void ClearNameRequired()
        {
            this.FindControl<TextBox>("NameBox")?.Classes.Remove("invalid");
        }

        /// <summary>
        /// Первая же буква снимает красную рамку: человек уже делает то, о чём
        /// его попросили.
        /// </summary>
        private void OnNameChanged(object? sender, TextChangedEventArgs e)
        {
            if (_draft != null && !string.IsNullOrWhiteSpace(_draft.Name)) ClearNameRequired();
        }

        private void CloseOverlay()
        {
            IsVisible = false;
            DataContext = null;
            _draft = null;
            _original = null;
            _apply = null;
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            if (_draft is null || _apply is null)
            {
                CloseOverlay();
                return;
            }

            // Без имени метки не бывает — она им и опознаётся в списках, в
            // поиске и в реестре проекта. Раньше окно на пустом имени просто
            // закрывалось, унося с собой и подобранную картинку, и цвета, и
            // выбранный значок: со стороны это выглядело так, будто ОК ничего
            // не делает. Теперь окно остаётся открытым и говорит, чего не
            // хватает.
            if (string.IsNullOrWhiteSpace(_draft.Name))
            {
                ShowNameRequired();
                return;
            }

            var result = new CharacterLabel
            {
                Id = _original?.Id ?? Guid.NewGuid().ToString(),
                Name = _draft.Name.Trim(),
                Icon = _draft.Icon,
                Color = _draft.Color,
                Effect = _draft.Dim ? CharacterLabelEffect.Dim : CharacterLabelEffect.None,
                ShowOnCard = _draft.ShowOnCard,
                Order = _original?.Order ?? int.MaxValue,
                Description = _draft.Description.Trim(),
                IconImage = _draft.IconImage,
                IconColor = _draft.IconColorOrEmpty,
                ShowBackdrop = _draft.ShowBackdrop
            };

            _apply(result);
            CloseOverlay();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => CloseOverlay();

        /// <summary>
        /// Сервис хранения картинок. Ставится модулем при инициализации —
        /// окно живёт поверх модуля и своих зависимостей не получает.
        /// </summary>
        public static Interfaces.ICharacterAvatarService? AvatarService { get; set; }

        // Своя картинка вместо встроенного значка: герб дома, эмблема клуба,
        // нарисованный автором знак. Встроенный набор из тридцати иконок всё
        // равно не покроет чужую выдумку.
        //
        // Когда картинка уже стоит, та же плитка открывает обрезку заново:
        // подобрать кусок с первого раза выходит не всегда, а снимать ради
        // этого картинку и выбирать файл заново — лишний круг.
        private async void OnPickIconImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_draft == null || AvatarService == null) return;

            if (_draft.HasCustomIcon)
            {
                _draft.IconImage = await CropIconAsync(_draft.IconImage!);
                return;
            }

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Картинка метки",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { IconFileType }
                });

                if (files == null || files.Count == 0) return;

                await using var stream = await files[0].OpenReadAsync();
                using var buffer = new System.IO.MemoryStream();
                await stream.CopyToAsync(buffer);

                // Значок сохраняется отдельным методом от аватара: у значков
                // шире список форматов, и вектор проходит только здесь.
                var imageRef = await AvatarService.SaveIconToProjectAsync(buffer.ToArray(), files[0].Name);
                if (!string.IsNullOrEmpty(imageRef))
                    _draft.IconImage = await CropIconAsync(imageRef!);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Label icon pick failed");
            }
        }

        // ── Кадр значка ───────────────────────────────────────────────────
        //
        // Та же обрезка, что у аватарок, и то же окно: значок метки — это
        // круглая картинка размером в полтора десятка точек, и что именно из
        // принесённого файла в неё попадёт, решать должен автор. Без выбора
        // кадра лист со спрайтами превращался в неразличимую кашу, а портрет —
        // в кусок плеча.
        //
        // Кадр живёт в самой ссылке (CharacterAvatarRef), поэтому показу
        // ничего объяснять не нужно: загрузчик картинки применяет его сам —
        // тем же путём, что и у аватарки персонажа.

        private Avatars.CharacterAvatarCropOverlay? FindCropOverlay()
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            return host?.FindControl<Avatars.CharacterAvatarCropOverlay>("AvatarCropOverlayControl");
        }

        /// <summary>
        /// Спросить кадр для картинки значка и вернуть ссылку с ним. Отказ от
        /// обрезки возвращает ссылку как есть: картинка показывается целиком,
        /// и это законный выбор, а не отмена всей затеи с картинкой.
        /// </summary>
        private async System.Threading.Tasks.Task<string?> CropIconAsync(string imageRef)
        {
            if (string.IsNullOrWhiteSpace(imageRef)) return imageRef;

            // Вектор рисуется целиком и кадра не знает: обрезать SVG значило бы
            // показать окно, чей результат некуда применить.
            if (Services.LabelIconImages.IsVector(imageRef)) return imageRef;

            var overlay = FindCropOverlay();
            if (overlay == null || AvatarService == null) return imageRef;

            var bytes = AvatarService.LoadAvatarBytes(CharacterAvatarRef.BaseOf(imageRef));
            if (bytes == null) return imageRef;

            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            try
            {
                using var stream = new System.IO.MemoryStream(bytes);
                bitmap = new Avalonia.Media.Imaging.Bitmap(stream);

                var crops = await overlay.ShowAsync(
                    bitmap,
                    CharacterAvatarRef.CropOf(imageRef),
                    "Кадр значка метки");

                if (crops == null) return imageRef;

                // Метке нужен только кружковый кадр: полоски у значка нет.
                return CharacterAvatarRef.WithCrop(imageRef, crops.Circle);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Label icon crop failed for {Ref}", imageRef);
                return imageRef;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        // Убрать картинку — вернётся встроенный значок, ключ которого всё это
        // время сохранялся.
        private void OnClearIconImageClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_draft != null) _draft.IconImage = null;
        }

        // ── Картинка броском в окно ───────────────────────────────────────
        //
        // Файл значка чаще всего уже лежит в открытой рядом папке, и путь
        // «кнопка → диалог → найти в диалоге тот же файл» здесь лишний. Приём
        // висит на всей панели, а не на плитке: попасть броском в плитку в
        // тридцать шесть точек тяжело.

        private void OnIconDragOver(object? sender, DragEventArgs e)
        {
            var accepts = _draft != null && e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropHint(accepts);
            e.Handled = true;
        }

        private void OnIconDragLeave(object? sender, DragEventArgs e)
        {
            SetDropHint(false);
            e.Handled = true;
        }

        private async void OnIconDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            SetDropHint(false);

            if (_draft == null || AvatarService == null) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            // Берётся первый подходящий файл: значок у метки один, и класть
            // «последний из брошенных» значило бы зависеть от того, в каком
            // порядке их отдала система.
            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;
                if (!IsIconFile(storageFile.Name)) continue;

                try
                {
                    await using var stream = await storageFile.OpenReadAsync();
                    using var buffer = new System.IO.MemoryStream();
                    await stream.CopyToAsync(buffer);

                    var imageRef = await AvatarService.SaveIconToProjectAsync(
                        buffer.ToArray(), storageFile.Name);

                    // Брошенная картинка идёт тем же путём, что и выбранная в
                    // диалоге: сразу за приёмом — выбор кадра.
                    if (!string.IsNullOrEmpty(imageRef))
                        _draft.IconImage = await CropIconAsync(imageRef!);
                }
                catch (Exception ex)
                {
                    // Бросить могут что угодно — папку, ярлык, недоступный файл.
                    _logger.Error(ex, "Label icon drop failed: {Name}", storageFile.Name);
                }

                return;
            }
        }

        /// <summary>Расширение годится в значок метки. Список тот же, что в диалоге выбора.</summary>
        private static bool IsIconFile(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();

            return ext is ".png" or ".jpg" or ".jpeg" or ".webp"
                       or ".bmp" or ".gif" or ".ico" or ".svg";
        }

        private void SetDropHint(bool value)
        {
            var hint = this.FindControl<Border>("DropHint");
            if (hint != null) hint.IsVisible = value;
        }

        // Скрим блокирует модуль, но окно не закрывает — как в остальных
        // оверлеях модуля.
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        // Выбор значка: черновик запоминает ключ, выделение перерисовывается.
        private void OnIconClick(object? sender, RoutedEventArgs e)
        {
            if (_draft is null) return;
            if (sender is not Control c || c.DataContext is not LabelIconOption option) return;

            _draft.Icon = option.Key;
            foreach (var icon in _draft.Icons)
                icon.IsSelected = icon.Key == option.Key;
            e.Handled = true;
        }
    }
}
