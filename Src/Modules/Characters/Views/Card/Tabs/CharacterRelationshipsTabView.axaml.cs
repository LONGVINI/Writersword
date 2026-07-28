using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Globalization;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Modules.Characters.ViewModels.Tabs;
using Writersword.Modules.Characters.Views;

namespace Writersword.Modules.Characters.Views.Card.Tabs
{
    /// <summary>
    /// Code-behind вкладки «Связи»: эго-граф персонажа.
    /// Связь создаётся перетаскиванием персонажа из бокового списка на
    /// полотно (или двойным щелчком по нему), правится в панели справа.
    /// </summary>
    public partial class CharacterRelationshipsTabView : UserControl
    {
        public CharacterRelationshipsTabView()
        {
            InitializeComponent();

            // Обработчики перетаскивания вешаются здесь, а не в разметке:
            // это присоединённые события, и в коде их привязка однозначна.
            AddHandler(DragDrop.DragOverEvent, OnCanvasDragOver);
            AddHandler(DragDrop.DropEvent, OnCanvasDrop);
            DragDrop.SetAllowDrop(this, true);
        }

        private CharacterRelationshipsTabViewModel? ViewModel =>
            DataContext as CharacterRelationshipsTabViewModel;

        // ── Создание связи ───────────────────────────────────────────────
        // Источник перетаскивания — боковой список персонажей в редакторе
        // (CharacterEditView). Своего списка вкладка не держит: он там уже есть.

        private void OnCanvasDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = e.DataTransfer.Contains(CharacterDragFormats.CharacterId)
                ? DragDropEffects.Link
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnCanvasDrop(object? sender, DragEventArgs e)
        {
            var id = e.DataTransfer.TryGetValue(CharacterDragFormats.CharacterId);
            if (string.IsNullOrEmpty(id)) return;

            ViewModel?.AddRelationshipTo(id);
            e.Handled = true;
        }

        // ── Выбор связи на полотне ───────────────────────────────────────

        private void OnNodePressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not CharacterRelationshipItemViewModel item) return;
            ViewModel?.Select(item);
            e.Handled = true;
        }

        private void OnRemoveSelectedClick(object? sender, RoutedEventArgs e)
        {
            var vm = ViewModel;
            var selected = vm?.Selected;
            if (vm is null || selected is null) return;

            vm.RemoveRelationshipCommand.Execute(selected.RelationshipId).Subscribe();
            e.Handled = true;
        }

        // ── Фильтры полотна ──────────────────────────────────────────────

        private void OnFilterAllClick(object? sender, RoutedEventArgs e)
        {
            ViewModel?.ResetFilters();
            e.Handled = true;
        }

        private void OnFilterPositiveClick(object? sender, RoutedEventArgs e)
        {
            Toggle(CharacterRelationshipEmotion.Positive);
            e.Handled = true;
        }

        private void OnFilterNegativeClick(object? sender, RoutedEventArgs e)
        {
            Toggle(CharacterRelationshipEmotion.Negative);
            e.Handled = true;
        }

        // Повторное нажатие снимает фильтр — иначе из него не выйти иначе,
        // как через «Все», а это лишний шаг.
        private void Toggle(CharacterRelationshipEmotion emotion)
        {
            var vm = ViewModel;
            if (vm is null) return;
            vm.FilterEmotion = vm.FilterEmotion == emotion ? null : emotion;
        }

        // ── Редактор связи ───────────────────────────────────────────────

        private void OnEmotionNeutralClick(object? sender, RoutedEventArgs e)
            => SetEmotion(CharacterRelationshipEmotion.Neutral, e);

        private void OnEmotionPositiveClick(object? sender, RoutedEventArgs e)
            => SetEmotion(CharacterRelationshipEmotion.Positive, e);

        private void OnEmotionNegativeClick(object? sender, RoutedEventArgs e)
            => SetEmotion(CharacterRelationshipEmotion.Negative, e);

        private void OnEmotionAmbivalentClick(object? sender, RoutedEventArgs e)
            => SetEmotion(CharacterRelationshipEmotion.Ambivalent, e);

        private void SetEmotion(CharacterRelationshipEmotion emotion, RoutedEventArgs e)
        {
            var selected = ViewModel?.Selected;
            if (selected != null) selected.Emotion = emotion;
            e.Handled = true;
        }

        private void OnStrengthClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            var selected = ViewModel?.Selected;
            if (selected is null) return;
            if (sender is not Control c) return;
            if (c.Tag is not string raw) return;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)) return;

            selected.StrengthLevel = level;
        }

        // ── «Кто как называет» ───────────────────────────────────────────

        private void OnCallsAsKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            if (sender is not TextBox box) return;
            var selected = ViewModel?.Selected;
            if (selected is null) return;

            // Повод вводится в той же строке через тире: «Алинусик — нежно».
            // Разбор и защита от дублей — во вьюмодели связи.
            selected.AddAddressForm(box.Text ?? string.Empty);
            box.Text = string.Empty;
        }

        private void OnCallsAsRemoveClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control c ||
                c.DataContext is not Writersword.Modules.Characters.Models.CharacterAddressForm form) return;

            ViewModel?.Selected?.RemoveAddressForm(form.Id);
        }
    }
}
