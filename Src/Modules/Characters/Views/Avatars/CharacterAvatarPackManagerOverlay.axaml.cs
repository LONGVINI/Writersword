using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Writersword.Modules.Characters.ViewModels.Avatars;
using Writersword.Src.Modules.Characters.Resources;

namespace Writersword.Modules.Characters.Views.Avatars
{
    /// <summary>
    /// Папки с аватарками. Живёт на уровне модуля, показывается поверх выбора
    /// аватарки и несёт свой скрим.
    ///
    /// Размеры панели ограничиваются под размер модуля тем же способом, что у
    /// редактора цвета: наблюдатель Bounds ставит панели MaxWidth и MaxHeight,
    /// а середина окна прокручивается.
    ///
    /// Окно принимает брошенные картинки — они ложатся в выбранную папку, — а
    /// кнопка приёма архива принимает брошенный ZIP.
    /// </summary>
    public partial class CharacterAvatarPackManagerOverlay : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharacterAvatarPackManagerOverlay>();

        private static readonly FilePickerFileType ZipFileType =
            new("ZIP") { Patterns = new[] { "*.zip" } };

        private static readonly FilePickerFileType ImageFileType =
            new(CharactersStrings.FilePicker_ImagesFilter)
            {
                Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" },
                MimeTypes = new[] { "image/jpeg", "image/png", "image/webp" }
            };

        public CharacterAvatarPackManagerOverlay()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            // Перетаскивание картинок ловится туннелем на корне окна — так же,
            // как перетаскивание карточек в списке персонажей: обработчик должен
            // отработать раньше прокрутки и раньше кнопок на самой плитке.
            AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnTilePointerMoved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, OnTilePointerReleased, RoutingStrategies.Tunnel);

            // Закрыть окно можно не только крестиком, поэтому доводим убранное
            // до хранилища по самому факту скрытия: пока окно на экране, ни один
            // файл не тронут, а как только оно ушло — отменять уже нечем.
            this.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (!visible && DataContext is CharacterAvatarPackManagerViewModel vm)
                    vm.ApplyChanges();
            });

            // Панель не должна вылезать за модуль: при сжатом окне она иначе
            // обрезается по краям вместе с кнопками.
            this.GetObservable(BoundsProperty).Subscribe(b =>
            {
                if (b.Width <= 0) return;
                ApplyPanelMetrics(b.Width, b.Height);
            });
        }

        // Ctrl+Z / Ctrl+Y окна. Своя история, отдельная от истории модуля:
        // убранные картинки и папки возвращаются, пока окно открыто. Обработчик
        // висит на окне туннелем, как у редактора цвета, и пока менеджер
        // показан, событие гасится всегда — иначе отмена проваливается в модуль
        // под ним и незаметно откатывает операции там.
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            TopLevel.GetTopLevel(this)?.AddHandler(
                KeyDownEvent, OnManagerKeyDown, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnManagerKeyDown);
            base.OnDetachedFromVisualTree(e);
        }

        private void OnManagerKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var undo = e.Key == Key.Z && !shift;
            var redo = e.Key == Key.Y || (e.Key == Key.Z && shift);
            if (!undo && !redo) return;

            // В полях ввода самого окна (имя папки) работает обычная текстовая
            // отмена — событие не трогаем. Поле снаружи гасим: правки в окне
            // отменяться не должны чужой историей.
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focused is TextBox box && box.IsEffectivelyVisible)
            {
                if (!this.IsVisualAncestorOf(box)) e.Handled = true;
                return;
            }

            e.Handled = true;

            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;
            if (undo) vm.Undo(); else vm.Redo();
        }

        private void ApplyPanelMetrics(double width, double height)
        {
            var panel = this.FindControl<Border>("ManagerPanel");
            if (panel is null) return;

            // Верхний предел подобран под девять столбцов плиток: 744 минус
            // рамка, поля прокрутки и дорожка самой полосы прокрутки оставляют
            // ровно 9 × 76 с небольшой щелью. Ниже предела окно сжимается
            // вместе с модулем, а раскладка сама уменьшает число столбцов —
            // плитки не обрезаются и не наезжают на полосу.
            const double maxPanelWidth = 744.0;

            panel.MaxHeight = Math.Max(260, height - 48);
            panel.MaxWidth = Math.Min(maxPanelWidth, Math.Max(160, width - 48));
            panel.Width = Math.Min(maxPanelWidth, Math.Max(160, width - 48));
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;

            vm.RequestZipImportPicker = async () =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                var files = await window.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "Выберите ZIP-архив с папкой аватарок",
                        AllowMultiple = false,
                        FileTypeFilter = new[] { ZipFileType }
                    });
                return files.Count > 0 ? files[0].Path.LocalPath : null;
            };

            vm.RequestZipExportPicker = async (packName) =>
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window == null) return null;
                var file = await window.StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Сохранить папку аватарок как ZIP",
                        SuggestedFileName = $"{packName}.zip",
                        FileTypeChoices = new[] { ZipFileType }
                    });
                return file?.Path.LocalPath;
            };

            vm.RequestImagePicker = async () =>
            {
                var result = new List<CharacterPickedImage>();

                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return result;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = CharactersStrings.FilePicker_SelectImageTitle,
                        AllowMultiple = true,
                        FileTypeFilter = new[] { ImageFileType }
                    });

                // Отдаём не содержимое, а способ его прочитать: сотни выбранных
                // фотографий иначе оказались бы в памяти все разом.
                foreach (var file in files)
                {
                    var picked = file;
                    result.Add(new CharacterPickedImage(picked.Name, async () =>
                    {
                        try
                        {
                            await using var stream = await picked.OpenReadAsync();
                            using var buffer = new MemoryStream();
                            await stream.CopyToAsync(buffer);
                            return buffer.ToArray();
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Reading picked image failed: {Name}", picked.Name);
                            return null;
                        }
                    }));
                }

                return result;
            };
        }


        // ── Перестановка картинок перетаскиванием ─────────────────────────
        //
        // Устроено как перетаскивание карточек персонажей: тот же порог сдвига,
        // та же пауза удержания (быстрый щелчок остаётся щелчком и доходит до
        // кнопок плитки), тот же призрак под курсором и та же автопрокрутка у
        // краёв.
        //
        // Место вставки показывает сама плитка: она остаётся в ленте и едет по
        // ней вслед за курсором, а картинку с неё на это время убирают.
        // Отдельного пустого места, как у карточек, тут не заводится — плитки
        // одинаковые и стоят плотной сеткой, и лишний пустой квадрат читался бы
        // как ещё одна картинка.
        //
        // Новый порядок ложится в сеанс окна: пока окно открыто, в хранилище
        // ничего не уходит, Ctrl+Z возвращает, закрытие записывает.

        private const double DragThreshold = 8.0;
        private const long DragHoldDelayMs = 90;
        private const double GhostSize = 68.0;

        // Пересчёт места вставки не чаще ~16 раз в секунду. Призрак летит за
        // мышью каждый кадр — это дёшево, двигается одна координата, — а
        // перестановка в коллекции влечёт полный проход раскладки всей ленты.
        private const long ReorderThrottleMs = 60;

        private CharacterAvatarPackManagerItemViewModel? _dragCandidate;
        private CharacterAvatarPackManagerPackViewModel? _dragPack;
        private List<string> _orderBefore = new();
        private Panel? _pickedTile;
        private Point _dragStartPoint;
        private long _pressTick;
        private long _lastReorderTick;
        private bool _isDragging;
        private bool _hasPointerCapture;
        private Point _lastDragPos;

        private Canvas? _ghostCanvas;
        private Border? _ghostBorder;
        private Image? _ghostImage;

        private DispatcherTimer? _autoScrollTimer;
        private double _autoScrollVel;

        private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            ClearPicked();
            _dragCandidate = null;
            _dragPack = null;
            _isDragging = false;
            _hasPointerCapture = false;

            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var pack = vm.SelectedPack;
            // Встроенные паки лежат в ресурсах сборки: порядок им записывать
            // некуда, и тянуть плитку значило бы обещать перестановку, которой
            // не будет.
            if (pack?.CanReorder != true) return;

            var tile = FindTile(e.Source as Visual);
            if (tile?.DataContext is not CharacterAvatarPackManagerItemViewModel item) return;
            if (!pack.Items.Contains(item)) return;

            _dragCandidate = item;
            _dragPack = pack;
            _dragStartPoint = e.GetPosition(this);
            _pressTick = Environment.TickCount64;

            // Указатель здесь не захватываем: при простом щелчке захват на окне
            // подавил бы Click кнопок плитки. Захват берётся в момент реального
            // старта перетаскивания.
            SetPicked(tile);
        }

        private void OnTilePointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragCandidate is null) return;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndDrag(e.Pointer);
                return;
            }

            var pos = e.GetPosition(this);

            if (!_isDragging)
            {
                var delta = pos - _dragStartPoint;
                var moved = Math.Abs(delta.X) >= DragThreshold || Math.Abs(delta.Y) >= DragThreshold;

                // Движение раньше паузы удержания — это щелчок или прокрутка:
                // отпускаем кандидата, чтобы нажатие дошло до кнопок плитки.
                if (moved && Environment.TickCount64 - _pressTick < DragHoldDelayMs)
                {
                    ClearPicked();
                    _dragCandidate = null;
                    _dragPack = null;
                    return;
                }

                if (!moved) return;

                _isDragging = true;
                e.Pointer.Capture(this);
                _hasPointerCapture = true;
                ClearPicked();

                _orderBefore = _dragPack?.Items.Select(i => i.FileName).ToList() ?? new List<string>();
                _dragCandidate.IsDragging = true;

                ShowGhost(_dragCandidate, pos);
                StartAutoScroll();
                _lastDragPos = pos;
                _lastReorderTick = Environment.TickCount64;
            }
            else
            {
                _lastDragPos = pos;
                MoveGhost(pos);
                UpdateAutoScrollVelocity(pos);

                var now = Environment.TickCount64;
                if (now - _lastReorderTick >= ReorderThrottleMs)
                {
                    _lastReorderTick = now;
                    UpdateOrder(pos);
                }
            }
        }

        private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_dragCandidate is null) return;

            if (!_isDragging)
            {
                ClearPicked();
                _dragCandidate = null;
                _dragPack = null;
                return;
            }

            EndDrag(e.Pointer);

            // Отпускание после перетаскивания не должно доходить до кнопок под
            // курсором: плитку положили на место, а не нажали на неё.
            e.Handled = true;
        }

        private void EndDrag(IPointer? pointer)
        {
            var item = _dragCandidate;
            var pack = _dragPack;
            var wasDragging = _isDragging;

            if (_hasPointerCapture) pointer?.Capture(null);
            _hasPointerCapture = false;
            _isDragging = false;
            _dragCandidate = null;
            _dragPack = null;

            ClearPicked();
            HideGhost();
            StopAutoScroll();

            if (item is not null) item.IsDragging = false;
            if (!wasDragging || pack is null) return;
            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;

            vm.CommitItemOrder(
                pack.PackId,
                _orderBefore,
                pack.Items.Select(i => i.FileName).ToList());
        }

        /// <summary>
        /// Подвинуть перетаскиваемую плитку туда, где сейчас курсор. Двигаем
        /// саму плитку в коллекции: лента перестраивается, и порядок на экране
        /// в любой момент равен тому, который запишется при отпускании.
        /// </summary>
        private void UpdateOrder(Point pos)
        {
            if (_dragPack is null || _dragCandidate is null) return;

            var from = _dragPack.Items.IndexOf(_dragCandidate);
            if (from < 0) return;

            var target = TargetIndexAt(pos, from);
            if (target < 0 || target == from) return;

            _dragPack.Items.Move(from, target);
        }

        private int TargetIndexAt(Point pos, int from)
        {
            if (_dragPack is null) return -1;

            var host = this.FindControl<ItemsRepeater>("PackItemsHost");
            if (host is null) return -1;

            var lastBottom = double.MinValue;

            foreach (var tile in host.GetVisualDescendants().OfType<Panel>())
            {
                if (!tile.Classes.Contains("packItem")) continue;
                if (tile.DataContext is not CharacterAvatarPackManagerItemViewModel item) continue;

                var topLeft = tile.TranslatePoint(new Point(0, 0), this);
                if (topLeft is null) continue;

                var rect = new Rect(topLeft.Value, tile.Bounds.Size);
                if (rect.Bottom > lastBottom) lastBottom = rect.Bottom;

                if (!rect.Contains(pos)) continue;

                var index = _dragPack.Items.IndexOf(item);
                if (index < 0) return -1;

                // Правая половина плитки означает «встать после неё». Без этого
                // соседнюю плитку было бы не обойти: чтобы уехать вправо на одну
                // позицию, курсор пришлось бы заводить за середину следующей.
                if (pos.X > rect.Center.X) index++;
                if (index > from) index--;

                return Math.Clamp(index, 0, _dragPack.Items.Count - 1);
            }

            // Ниже последнего ряда — в конец. Промежутки между плитками
            // концом не считаются: там курсор оказывается по дороге, и лента
            // прыгала бы на каждом переходе между рядами.
            if (lastBottom > double.MinValue && pos.Y > lastBottom)
                return _dragPack.Items.Count - 1;

            return -1;
        }

        private static Panel? FindTile(Visual? source)
            => source?.GetSelfAndVisualAncestors()
                .OfType<Panel>()
                .FirstOrDefault(p => p.Classes.Contains("packItem"));

        private void SetPicked(Panel tile)
        {
            ClearPicked();
            _pickedTile = tile;
            if (!tile.Classes.Contains("picked")) tile.Classes.Add("picked");
        }

        private void ClearPicked()
        {
            if (_pickedTile is null) return;
            _pickedTile.Classes.Remove("picked");
            _pickedTile = null;
        }

        private void ShowGhost(CharacterAvatarPackManagerItemViewModel item, Point pos)
        {
            EnsureGhost();
            if (_ghostCanvas is null) return;

            if (_ghostImage is not null) _ghostImage.Source = item.Thumbnail;

            MoveGhost(pos);
            _ghostCanvas.IsVisible = true;
        }

        private void MoveGhost(Point pos)
        {
            if (_ghostBorder is null) return;
            Canvas.SetLeft(_ghostBorder, pos.X - GhostSize / 2.0);
            Canvas.SetTop(_ghostBorder, pos.Y - GhostSize / 2.0);
        }

        private void HideGhost()
        {
            if (_ghostCanvas is not null) _ghostCanvas.IsVisible = false;
            if (_ghostImage is not null) _ghostImage.Source = null;
        }

        private void EnsureGhost()
        {
            _ghostCanvas ??= this.FindControl<Canvas>("DragGhostCanvas");
            _ghostBorder ??= this.FindControl<Border>("DragGhostBorder");
            _ghostImage ??= this.FindControl<Image>("DragGhostImage");
        }

        // ── Автопрокрутка у краёв ─────────────────────────────────────────
        //
        // Лента картинок длиннее окна, и без прокрутки картинку с последнего
        // ряда нельзя было бы утащить в первый.

        private void StartAutoScroll()
        {
            if (_autoScrollTimer is null)
            {
                _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _autoScrollTimer.Tick += OnAutoScrollTick;
            }
            _autoScrollVel = 0;
            _autoScrollTimer.Start();
        }

        private void StopAutoScroll()
        {
            _autoScrollTimer?.Stop();
            _autoScrollVel = 0;
        }

        private void UpdateAutoScrollVelocity(Point pos)
        {
            _autoScrollVel = 0;

            var scroll = this.FindControl<ScrollViewer>("PackItemsScroll");
            if (scroll is null) return;

            var topLeft = scroll.TranslatePoint(new Point(0, 0), this);
            if (topLeft is null) return;

            var top = topLeft.Value.Y;
            var bottom = top + scroll.Bounds.Height;

            const double zone = 40.0;
            const double maxSpeed = 18.0;

            if (pos.Y < top + zone)
                _autoScrollVel = -maxSpeed * Math.Clamp((top + zone - pos.Y) / zone, 0, 1);
            else if (pos.Y > bottom - zone)
                _autoScrollVel = maxSpeed * Math.Clamp((pos.Y - (bottom - zone)) / zone, 0, 1);
        }

        private void OnAutoScrollTick(object? sender, EventArgs e)
        {
            if (!_isDragging || Math.Abs(_autoScrollVel) < 0.5) return;

            var scroll = this.FindControl<ScrollViewer>("PackItemsScroll");
            if (scroll is null) return;

            var offset = scroll.Offset;
            var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            var newY = Math.Clamp(offset.Y + _autoScrollVel, 0, maxY);
            if (Math.Abs(newY - offset.Y) < 0.1) return;

            scroll.Offset = new Vector(offset.X, newY);

            // Лента уехала — под курсором теперь другая плитка.
            MoveGhost(_lastDragPos);

            var now = Environment.TickCount64;
            if (now - _lastReorderTick >= ReorderThrottleMs)
            {
                _lastReorderTick = now;
                UpdateOrder(_lastDragPos);
            }
        }

        // ── Приём картинок всем окном ─────────────────────────────────────

        private void OnManagerDragOver(object? sender, DragEventArgs e)
        {
            var accepts = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropTarget(accepts);
            e.Handled = true;
        }

        private void OnManagerDragLeave(object? sender, DragEventArgs e)
        {
            SetDropTarget(false);
            e.Handled = true;
        }

        private async void OnManagerDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            SetDropTarget(false);

            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            // В папку кладут запас, поэтому берётся вся брошенная пачка, а не
            // одна картинка: здесь у выбора нет единственного результата.
            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;
                if (!CharacterAvatarPickerOverlay.IsDroppableImage(storageFile.Name)) continue;

                try
                {
                    await using var stream = await storageFile.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);

                    await vm.HandleDroppedImageAsync(buffer.ToArray(), storageFile.Name);
                }
                catch (Exception ex)
                {
                    // Бросить могут что угодно — папку, ярлык, недоступный файл.
                    _logger.Error(ex, "Pack manager drop failed: {Name}", storageFile.Name);
                }
            }
        }

        // ── Приём архива кнопкой ──────────────────────────────────────────
        //
        // Кнопка приёма ZIP объявлена приёмником сама. Событие Drop всплывает,
        // и обработчик кнопки успевает пометить его обработанным раньше, чем
        // до него доберётся обработчик всего окна — иначе архив ушёл бы в
        // общий разбор, где ждут только картинки.

        private void OnZipDragOver(object? sender, DragEventArgs e)
        {
            var accepts = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            SetZipArmed(accepts);
            SetDropTarget(false);
            e.Handled = true;
        }

        private void OnZipDragLeave(object? sender, DragEventArgs e)
        {
            SetZipArmed(false);
            e.Handled = true;
        }

        private async void OnZipDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            SetZipArmed(false);
            SetDropTarget(false);

            if (DataContext is not CharacterAvatarPackManagerViewModel vm) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;

            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;

                var path = storageFile.Path.LocalPath;
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                try { await vm.HandleDroppedZipAsync(path); }
                catch (Exception ex) { _logger.Error(ex, "Zip drop failed: {Path}", path); }

                return;
            }

            vm.StatusMessage = "На эту кнопку бросают ZIP-архив с папкой аватарок.";
        }

        private void SetZipArmed(bool value)
        {
            var button = this.FindControl<Button>("ImportZipButton");
            if (button == null) return;

            if (value)
            {
                if (!button.Classes.Contains("dropArmed")) button.Classes.Add("dropArmed");
            }
            else
            {
                button.Classes.Remove("dropArmed");
            }
        }

        private void SetDropTarget(bool value)
        {
            if (DataContext is CharacterAvatarPackManagerViewModel vm)
                vm.IsDropTarget = value;
        }

        // Скрим блокирует модуль, но окно не закрывает — как в редакторе цвета.
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    }
}
