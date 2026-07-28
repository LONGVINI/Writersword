using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Modules.Characters.ViewModels.Templates;

namespace Writersword.Modules.Characters.Views
{
    /// <summary>
    /// Конструктор набора полей: имя, описание и список полей с типами,
    /// подсказками и подписями делений шкалы.
    ///
    /// До него пользовательский набор можно было создать, продублировать
    /// и удалить, но не наполнить — «Мой шаблон» оставался пустым, и своих
    /// наборов фактически не существовало.
    ///
    /// Правки живут в черновике и применяются по «Готово»; отмена и крестик
    /// закрывают без изменений. Применение — колбэком, чтобы окно не знало
    /// о вьюмоделях вкладок.
    /// </summary>
    public partial class AnketaEditorOverlay : UserControl
    {
        private AnketaEditorDraft? _draft;
        private CharacterAnketa? _original;
        private Action<CharacterAnketa>? _apply;

        public AnketaEditorOverlay()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Открыть конструктор для набора. Колбэк вызывается по «Готово»
        /// с наполненным набором.
        /// </summary>
        /// <param name="knownFields">
        /// Поля, уже существующие в проекте. Подсказываются при вводе имени:
        /// выбрал существующее — новое поле получает его идентификатор,
        /// и значения встают в один ряд с уже введёнными в карточках.
        /// </param>
        public void ShowFor(CharacterAnketa anketa, Action<CharacterAnketa> apply,
            System.Collections.Generic.IEnumerable<CharacterAnketaField>? knownFields = null)
        {
            if (anketa == null) return;

            _original = anketa;
            _apply = apply;
            _draft = new AnketaEditorDraft(anketa, knownFields);

            DataContext = _draft;
            IsVisible = true;
        }

        private void CloseOverlay()
        {
            IsVisible = false;
            DataContext = null;
            _draft = null;
            _original = null;
            _apply = null;
        }

        // Скрим перехватывает нажатия, чтобы модуль под окном не реагировал,
        // но сам не закрывает: случайный клик мимо не должен терять правки.
        private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            CloseOverlay();
            e.Handled = true;
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e)
        {
            if (_draft != null && _original != null && _apply != null)
                _apply(_draft.ToAnketa(_original));

            CloseOverlay();
            e.Handled = true;
        }

        private void OnAddScaleClick(object? sender, RoutedEventArgs e)
        {
            _draft?.AddField(CharacterParameterType.Numeric);
            e.Handled = true;
        }

        private void OnAddTextClick(object? sender, RoutedEventArgs e)
        {
            _draft?.AddField(CharacterParameterType.Text);
            e.Handled = true;
        }

        private void OnAddChoiceClick(object? sender, RoutedEventArgs e)
        {
            _draft?.AddField(CharacterParameterType.StateList);
            e.Handled = true;
        }

        private void OnAddBooleanClick(object? sender, RoutedEventArgs e)
        {
            _draft?.AddField(CharacterParameterType.Boolean);
            e.Handled = true;
        }

        private void OnRemoveFieldClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is AnketaFieldDraft field)
                _draft?.RemoveField(field);
            e.Handled = true;
        }

        private void OnMoveFieldUpClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is AnketaFieldDraft field)
                _draft?.MoveField(field, -1);
            e.Handled = true;
        }

        private void OnMoveFieldDownClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is AnketaFieldDraft field)
                _draft?.MoveField(field, +1);
            e.Handled = true;
        }
    }
}
