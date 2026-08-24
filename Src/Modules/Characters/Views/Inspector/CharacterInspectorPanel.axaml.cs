using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using Writersword.Modules.Characters.Views.Avatars;
using Writersword.Modules.Characters.Views.Tabs;
using Writersword.Modules.Characters.ViewModels.Inspector;

namespace Writersword.Modules.Characters.Views.Inspector
{
    /// <summary>
    /// Боковая панель оформления карточек. Разметка привязана к
    /// CharacterInspectorViewModel; здесь живёт только то, что привязкой не
    /// выражается: сворачивание секций, фиксация имени и фиксация толщины
    /// рамки в конце жеста.
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

        // ── Толщина рамки ─────────────────────────────────────────────────
        //
        // Ползунок пишет в предпросмотр на каждое движение, а запись случается
        // здесь — когда его отпустили, потеряли захват указателя или отпустили
        // клавишу. Три события вместо одного потому, что ползунок двигают и
        // мышью, и с клавиатуры, а захват может сорваться и без отпускания.

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
    }
}
