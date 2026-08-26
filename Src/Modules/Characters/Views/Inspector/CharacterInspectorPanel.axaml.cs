using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Views.Avatars;
using Writersword.Modules.Characters.Views.Tabs;
using Writersword.Modules.Characters.ViewModels.Avatars;
using Writersword.Modules.Characters.ViewModels.Inspector;

namespace Writersword.Modules.Characters.Views.Inspector
{
    /// <summary>
    /// Боковая панель оформления карточек. Разметка привязана к
    /// CharacterInspectorViewModel; здесь живёт только то, что привязкой не
    /// выражается: сворачивание секций, фиксация имени, фиксация толщины
    /// рамки в конце жеста, лента быстрых аватарок во флаауте и правка меток
    /// через тот же редактор, что и вкладка «Основное».
    /// </summary>
    public partial class CharacterInspectorPanel : UserControl
    {
        // Раскрытые секции. Состояние держится в самой панели: оно относится к
        // тому, как человек привык смотреть, а не к персонажу, и уезжать в
        // проект ему незачем.
        private readonly Dictionary<string, bool> _sectionOpen = new()
        {
            ["Avatar"] = true,
            ["Person"] = true,
            ["Color"] = true,
            ["Outline"] = true,
            ["Labels"] = false
        };

        public CharacterInspectorPanel()
        {
            InitializeComponent();
            ApplySections();
            DataContextChanged += OnPanelDataContextChanged;
        }

        // Крестик-кадр на плитке быстрой ленты открывает то же окно обрезки,
        // что и в полном окне выбора аватарки — вью-модель ленты просит его
        // делегатом (RequestCropForRef), потому что до самого окна, живущего
        // на уровне модуля, у неё доступа нет. Заводится один раз на смену
        // DataContext, а не на каждый Click — иначе делегат до первого щелчка
        // ещё не назначен.
        private void OnPanelDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is CharacterInspectorViewModel vm)
            {
                vm.QuickAvatars.RequestCropForRef = CropStoredAsync;
                vm.QuickAvatars.RequestScrollToFolder = ScrollToFolder;
            }
        }

        /// <summary>
        /// Щелчок по вкладке папки больше не фильтрует ленту, а долистывает
        /// единый список до нужного раздела: ищем контейнер, который
        /// ItemsControl сгенерировал для этой папки, и просим его показаться —
        /// остальное берёт на себя ближайший ScrollViewer.
        /// </summary>
        private void ScrollToFolder(CharacterQuickAvatarFolder folder)
        {
            var list = this.FindControl<ItemsControl>("QuickSectionsList");
            var container = list?.ContainerFromItem(folder);
            container?.BringIntoView();
        }

        // ── Секции ────────────────────────────────────────────────────────

        private void OnToggleSection(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            if (button.Tag is not string key) return;
            if (!_sectionOpen.ContainsKey(key)) return;

            _sectionOpen[key] = !_sectionOpen[key];
            ApplySections();
        }

        private void ApplySections()
        {
            foreach (var pair in _sectionOpen)
                ApplySection(pair.Key, pair.Value);
        }

        /// <summary>
        /// Показать или спрятать тело секции и повернуть стрелку. Стрелка
        /// рисуется путём, а не поворотом готового значка: поворот на 90
        /// градусов у мелкой фигуры со штрихом в 1.4 точки уводит её с
        /// пиксельной сетки и мылит.
        /// </summary>
        private void ApplySection(string key, bool isOpen)
        {
            var body = this.FindControl<StackPanel>(key + "Body");
            if (body is not null) body.IsVisible = isOpen;

            // Полное имя типа: System.IO.Path нужен приёму брошенного файла, и
            // короткое Path стало бы неоднозначным.
            var chevron = this.FindControl<Avalonia.Controls.Shapes.Path>(key + "Chev");
            if (chevron is not null)
            {
                chevron.Data = Avalonia.Media.Geometry.Parse(
                    isOpen ? "M1,2.5 L4,5.5 L7,2.5" : "M2.5,1 L5.5,4 L2.5,7");
            }
        }

        // ── Имя ───────────────────────────────────────────────────────────
        //
        // Пока печатают, подпись на карточке меняется, но в проект ничего не
        // уходит. Запись — по потере фокуса и по Enter: одна правка имени, один
        // шаг в истории отмены, а не один шаг на каждую букву.

        private void OnNameLostFocus(object? sender, RoutedEventArgs e)
        {
            (DataContext as CharacterInspectorViewModel)?.CommitName();
        }

        private void OnNameKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;

            (DataContext as CharacterInspectorViewModel)?.CommitName();
            e.Handled = true;
        }

        // ── Быстрая лента аватарок (флаут) ───────────────────────────────
        //
        // Лента больше не стоит в теле панели постоянно — она открывается по
        // щелчку на самой аватарке, всплывающим окошком поверх, как у
        // цветового кружка. Внутри — та же вью-модель (QuickAvatars), те же
        // привязки; изменился только контейнер.

        /// <summary>
        /// Открыть папку ленты. Команда здесь не годится: контекст данных чипа
        /// — сама папка, а команда открытия живёт у ленты, и добираться до неё
        /// привязкой пришлось бы через предка.
        /// </summary>
        private void OnQuickFolderClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control control) return;
            if (control.DataContext is not CharacterQuickAvatarFolder folder) return;

            (DataContext as CharacterInspectorViewModel)?.QuickAvatars.OpenFolder(folder);
        }

        /// <summary>
        /// Щелчок по плитке ставит аватарку (через её собственный
        /// PickCommand, привязанный в разметке) и следом закрывает флаут —
        /// иначе выбор терялся бы среди сотни таких же щелчков, а само
        /// окошко зависало бы поверх панели без причины.
        /// </summary>
        private void OnQuickAvatarTileClick(object? sender, RoutedEventArgs e)
        {
            CloseAvatarFlyout();
        }

        private void OnOpenAvatarManagerFromFlyoutClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            CloseAvatarFlyout();
            OpenManager();
        }

        private void CloseAvatarFlyout()
        {
            var trigger = this.FindControl<Button>("AvatarFlyoutTrigger");
            trigger?.Flyout?.Hide();
        }

        // ── Приём брошенной картинки ──────────────────────────────────────
        //
        // Панель принимает файл так же, как карточка: тянуть его к маленькому
        // кружку аватарки неудобно, а мимо панели промахнуться трудно. Самой
        // работы здесь нет — весь порядок (поиск уже сохранённой копии,
        // обрезка, сохранение) живёт в списке, и второй такой же порядок
        // разошёлся бы с ним при первой правке.

        private static readonly ILogger _dropLogger = Log.ForContext<CharacterInspectorPanel>();

        private void OnPanelDragOver(object? sender, DragEventArgs e)
        {
            var accepts = e.DataTransfer.Contains(DataFormat.File) && HasTarget();
            e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
            ShowDropHint(accepts);
            e.Handled = true;
        }

        private void OnPanelDragLeave(object? sender, DragEventArgs e)
        {
            ShowDropHint(false);
            e.Handled = true;
        }

        private async void OnPanelDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;
            ShowDropHint(false);

            var target = (DataContext as CharacterInspectorViewModel)?.DropTarget;
            if (target is null) return;

            var list = this.FindAncestorOfType<CharactersListView>();
            if (list is null) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files is null) return;

            foreach (var file in files)
            {
                if (file is not IStorageFile storageFile) continue;
                if (!CharacterAvatarPickerOverlay.IsDroppableImage(storageFile.Name)) continue;

                try
                {
                    await using var stream = await storageFile.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);

                    await list.ApplyDroppedAvatarAsync(target, buffer.ToArray(), storageFile.Name);
                }
                catch (Exception ex)
                {
                    // Бросить могут что угодно — папку, ярлык, недоступный файл.
                    _dropLogger.Error(ex, "Panel avatar drop failed: {Name}", storageFile.Name);
                }

                // Аватарка у персонажа одна: берётся первая подходящая
                // картинка, остальные из пачки не нужны.
                return;
            }
        }

        private bool HasTarget()
            => (DataContext as CharacterInspectorViewModel)?.DropTarget is not null;

        private void ShowDropHint(bool visible)
        {
            var hint = this.FindControl<Border>("PanelDropHint");
            if (hint is not null) hint.IsVisible = visible;
        }

        private void OnThicknessPointerReleased(object? sender, PointerReleasedEventArgs e)
            => CommitThickness();

        private void OnThicknessCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            => CommitThickness();

        private void OnThicknessKeyUp(object? sender, KeyEventArgs e)
            => CommitThickness();

        private void CommitThickness()
        {
            (DataContext as CharacterInspectorViewModel)?.CommitThickness();
        }

        // ── Папки аватарок ───────────────────────────────────────────────
        //
        // Менеджер папок живёт на уровне модуля, как и в полном окне выбора
        // аватарки (CharacterAvatarPickerOverlay) — внутри флаута панели его
        // не удержать, флаут обрезает содержимое по краю. Метод почти
        // дословно повторяет тот же метод там; общий приём вынести пока
        // некуда — оба места слишком по-разному запускают загрузку панели
        // после закрытия менеджера.

        private CharacterAvatarPackManagerOverlay? FindManagerOverlay()
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            return host?.FindControl<CharacterAvatarPackManagerOverlay>("AvatarPackManagerOverlayControl");
        }

        private void OpenManager()
        {
            var overlay = FindManagerOverlay();
            var avatarService = (DataContext as CharacterInspectorViewModel)?.QuickAvatars.AvatarService;
            if (overlay == null || overlay.IsVisible || avatarService == null) return;

            var managerVm = new CharacterAvatarPackManagerViewModel(avatarService);
            managerVm.CloseRequested += CloseManagerAndRefresh;

            overlay.DataContext = managerVm;
            overlay.IsVisible = true;
        }

        private void CloseManager()
        {
            var overlay = FindManagerOverlay();
            if (overlay == null) return;
            overlay.IsVisible = false;
            overlay.DataContext = null;
        }

        private void CloseManagerAndRefresh()
        {
            CloseManager();

            // Менеджер мог завести, удалить, переименовать или перенести папку —
            // лента панели после него собирается заново.
            (DataContext as CharacterInspectorViewModel)?.QuickAvatars.Reload(
                (DataContext as CharacterInspectorViewModel)?.QuickAvatars.AvatarService);
        }

        // ── Кадр на плитке быстрой ленты ────────────────────────────────
        //
        // Окно обрезки живёт на уровне модуля, а не внутри этой панели —
        // тот же приём и то же окно, что и у полного окна выбора аватарки
        // (CharacterAvatarPickerOverlay.CropStoredAsync), только источник
        // сервиса и ссылки другой.

        private CharacterAvatarCropOverlay? FindCropOverlay()
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            return host?.FindControl<CharacterAvatarCropOverlay>("AvatarCropOverlayControl");
        }

        private async Task<CharacterAvatarCropPair?> CropStoredAsync(string avatarRef)
        {
            var avatarService = (DataContext as CharacterInspectorViewModel)?.QuickAvatars.AvatarService;
            if (avatarService == null) return null;

            var overlay = FindCropOverlay();
            if (overlay == null) return null;

            var baseRef = CharacterAvatarRef.BaseOf(avatarRef);
            var bytes = avatarService.LoadAvatarBytes(baseRef);
            if (bytes == null) return null;

            Bitmap? bitmap = null;
            try
            {
                using var ms = new MemoryStream(bytes);
                bitmap = new Bitmap(ms);
                // Оба кадра ссылки уезжают в окно: правят один, второй всё это
                // время виден в превью и возвращается нетронутым.
                return await overlay.ShowAsync(
                    bitmap,
                    CharacterAvatarRef.CropOf(avatarRef),
                    null,
                    null,
                    CharacterAvatarRef.StripCropOf(avatarRef));
            }
            catch (Exception ex)
            {
                _dropLogger.Error(ex, "Quick avatar crop failed for {Ref}", avatarRef);
                return null;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        // ── Метки ────────────────────────────────────────────────────────
        //
        // Тот же редактор, что и на вкладке «Основное»: он хостится на уровне
        // модуля (LabelEditorOverlayControl) и не знает о вьюмоделях вкладок —
        // применение готовой метки идёт колбэком, здесь он пишет в панель.

        private LabelEditorOverlay? FindLabelEditor()
        {
            var host = this.FindAncestorOfType<CharactersModuleView>();
            return host?.FindControl<LabelEditorOverlay>("LabelEditorOverlayControl");
        }

        private void OnAddLabelClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not CharacterInspectorViewModel vm || !vm.CanEditLabels) return;

            FindLabelEditor()?.ShowFor(null, (created, applyToAll) => vm.UpsertLabel(created, applyToAll));
        }

        private void OnLabelChipClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control c || c.DataContext is not CharacterLabel label) return;
            if (DataContext is not CharacterInspectorViewModel vm || !vm.CanEditLabels) return;

            FindLabelEditor()?.ShowFor(label, (updated, applyToAll) => vm.UpsertLabel(updated, applyToAll));
        }

        private void OnLabelRemoveClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control c || c.DataContext is not CharacterLabel label) return;
            if (DataContext is not CharacterInspectorViewModel vm || !vm.CanEditLabels) return;

            vm.RemoveLabel(label.Id);
        }

        // Быстрая отметка «Мёртв»: та же встроенная метка, что и на вкладке
        // «Основное» — кнопка исчезает сама, как только метка появилась,
        // потому что дальше её показывает чип, а не два разных органа.
        private void OnMarkDeadClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is CharacterInspectorViewModel vm && vm.CanEditLabels) vm.IsDead = true;
        }

        // Enter в поле быстрого добавления метки. Совпадение по имени с уже
        // известной проекту меткой подхватывает её целиком; иначе заводится
        // новая — та же логика, что и в автодополнении вкладки «Основное».
        private void OnNewLabelKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender is not AutoCompleteBox box) return;
            if (DataContext is not CharacterInspectorViewModel vm || !vm.CanEditLabels) return;

            // Пока открыт список подсказок, Enter выбирает из него.
            if (box.IsDropDownOpen) return;

            e.Handled = true;
            vm.AddLabelCommand.Execute(box.Text ?? string.Empty).Subscribe();
            box.Text = string.Empty;
        }

        private void OnAddKnownLabelClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not CharacterInspectorViewModel vm || !vm.CanEditLabels) return;

            var box = this.FindControl<AutoCompleteBox>("NewLabelBox");
            if (box is null) return;

            vm.AddLabelCommand.Execute(box.Text ?? string.Empty).Subscribe();
            box.Text = string.Empty;
        }
    }
}
