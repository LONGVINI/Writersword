using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.ViewModels.Reading;

namespace Writersword.Mobile.Views
{
    /// <summary>
    /// Худ читалки: то же, что лента чтения на большом экране, разложенное по
    /// подвкладкам.
    ///
    /// Своей логики здесь нет. Все правки идут через ReadingRibbonViewModel —
    /// ту самую, на которой висит настольная лента. Значит пределы, ступени и
    /// разбор на «пересобрать раскладку» и «просто перерисовать» одни и те же, и
    /// разъехаться двум читалкам неоткуда.
    ///
    /// Худ наложен поверх книги и ничего у неё не отнимает. Панель, занимающая
    /// место внизу, меняла бы высоту вьюпорта, а по нему считается масштаб,
    /// которым лист вписывается в экран: развернул панель — буквы поехали.
    /// </summary>
    public partial class ReaderHud : UserControl
    {
        private ReadingRibbonViewModel? _vm;
        private bool _syncing;

        /// <summary>Нажата «Открыть» с выбранной книгой.</summary>
        public event Action<string>? OpenRequested;

        /// <summary>Переключена «Правка»: true — черновик, false — чтение.</summary>
        public event Action<bool>? EditModeChanged;

        public ReaderHud()
        {
            InitializeComponent();

            ToggleButton.IsCheckedChanged += OnToggleChanged;
            OpenButton.Click += OnOpenClicked;
            EditButton.IsCheckedChanged += OnEditChanged;

            FormatBox.SelectionChanged += OnFormatChanged;
            ThemeBox.SelectionChanged += OnThemeChanged;
            FontBox.SelectionChanged += OnFontChanged;

            NumbersCheck.IsCheckedChanged += OnNumbersChanged;
            ScaleCheck.IsCheckedChanged += OnScaleChanged;

            BrightnessSlider.PropertyChanged += OnSliderChanged;
            ContrastSlider.PropertyChanged += OnSliderChanged;
            WarmthSlider.PropertyChanged += OnSliderChanged;

            SetEnabled(false);
        }

        // ── Список книг ───────────────────────────────────────────────────

        public void SetBooks(IReadOnlyList<string> names, string? selected)
        {
            BookBox.ItemsSource = names;

            if (names.Count == 0)
                return;

            BookBox.SelectedItem = selected is not null && names.Contains(selected)
                ? selected
                : names[0];
        }

        private void OnOpenClicked(object? sender, RoutedEventArgs e)
        {
            if (BookBox.SelectedItem is string name && !string.IsNullOrWhiteSpace(name))
                OpenRequested?.Invoke(name);
        }

        private void OnEditChanged(object? sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            EditModeChanged?.Invoke(EditButton.IsChecked == true);
        }

        /// <summary>
        /// Разрешена ли правка. Запрещённая снимается и гаснет: держать нажатой
        /// кнопку, которая ничего не делает, значит врать о состоянии книги.
        /// </summary>
        public void SetEditAvailable(bool available)
        {
            EditButton.IsEnabled = available;

            if (available || EditButton.IsChecked != true)
                return;

            _syncing = true;
            EditButton.IsChecked = false;
            _syncing = false;
        }

        /// <summary>Снять «Правку», не поднимая события.</summary>
        public void ClearEditMode()
        {
            if (EditButton.IsChecked != true) return;

            _syncing = true;
            EditButton.IsChecked = false;
            _syncing = false;
        }

        // ── Привязка к ленте чтения ───────────────────────────────────────

        public void Attach(ReadingRibbonViewModel vm)
        {
            Detach();

            _vm = vm;
            vm.PropertyChanged += OnViewModelChanged;

            ColumnButton.Command = vm.SetFlowColumnCommand;
            SingleButton.Command = vm.SetFlowSingleCommand;

            FirstButton.Command = vm.FirstPageCommand;
            PrevButton.Command = vm.PrevPageCommand;
            NextButton.Command = vm.NextPageCommand;
            LastButton.Command = vm.LastPageCommand;

            ZoomOutButton.Command = vm.ZoomOutCommand;
            ZoomInButton.Command = vm.ZoomInCommand;

            SmallerButton.Command = vm.FontSmallerCommand;
            BiggerButton.Command = vm.FontBiggerCommand;

            ResetLightButton.Command = vm.ResetLightCommand;

            _syncing = true;
            FormatBox.ItemsSource = vm.FormatOptions;
            ThemeBox.ItemsSource = vm.ThemeItems;
            FontBox.ItemsSource = vm.AvailableFonts;
            _syncing = false;

            SetEnabled(true);
            Refresh();
        }

        public void Detach()
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnViewModelChanged;

            _vm = null;
            SetEnabled(false);
        }

        private void SetEnabled(bool on)
        {
            EditButton.IsEnabled = on;

            if (!on)
            {
                _syncing = true;
                EditButton.IsChecked = false;
                _syncing = false;
            }

            // Вкладка «Книга» живёт своей жизнью: пока книга не открыта, только
            // она и нужна.
            for (int i = 1; i < Tabs.ItemCount; i++)
                if (Tabs.ContainerFromIndex(i) is TabItem item)
                    item.IsEnabled = on;
        }

        private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        /// <summary>
        /// Подтягивает показанное к состоянию ленты. Зовётся и на её извещения, и
        /// после листания: номер страницы лента узнаёт от канваса, а не сама.
        /// </summary>
        public void Refresh()
        {
            if (_vm is null)
                return;

            _syncing = true;
            try
            {
                bool paged = _vm.IsPaged;

                ColumnButton.IsChecked = !paged;
                SingleButton.IsChecked = paged;

                PagerRow.IsVisible = paged;
                FormatRow.IsVisible = paged;
                ZoomRow.IsVisible = paged;

                PageBlock.Text = _vm.PageLabel;
                ZoomBlock.Text = _vm.ZoomLabel;
                FontStepBlock.Text = _vm.FontStepLabel;

                FormatBox.SelectedItem = _vm.SelectedFormat;
                ThemeBox.SelectedItem = _vm.SelectedThemeItem;
                FontBox.SelectedItem = _vm.SelectedFont;

                NumbersCheck.IsChecked = _vm.ShowPageNumbers;
                ScaleCheck.IsChecked = _vm.ScaleContent;

                BrightnessSlider.Value = _vm.Brightness;
                ContrastSlider.Value = _vm.Contrast;
                WarmthSlider.Value = _vm.Warmth;

                // Величины уже в процентах — умножать нечего.
                BrightnessBlock.Text = Percent(_vm.Brightness);
                ContrastBlock.Text = Percent(_vm.Contrast);
                WarmthBlock.Text = Percent(_vm.Warmth);
            }
            finally
            {
                _syncing = false;
            }
        }

        private static string Percent(double value) => Math.Round(value) + "%";

        // ── Правки из худа ────────────────────────────────────────────────

        private void OnFormatChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _vm is null) return;
            if (FormatBox.SelectedItem is ReadingOption<ReadingSheetFormat> option)
                _vm.SelectedFormat = option;
        }

        private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _vm is null) return;
            if (ThemeBox.SelectedItem is ReadingThemeItem item)
                _vm.SelectedThemeItem = item;
        }

        private void OnFontChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _vm is null) return;
            if (FontBox.SelectedItem is string font)
                _vm.SelectedFont = font;
        }

        private void OnNumbersChanged(object? sender, RoutedEventArgs e)
        {
            if (_syncing || _vm is null) return;
            _vm.ShowPageNumbers = NumbersCheck.IsChecked == true;
        }

        private void OnScaleChanged(object? sender, RoutedEventArgs e)
        {
            if (_syncing || _vm is null) return;
            _vm.ScaleContent = ScaleCheck.IsChecked == true;
        }

        private void OnSliderChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        {
            if (_syncing || _vm is null) return;
            if (e.Property != Slider.ValueProperty) return;

            if (ReferenceEquals(sender, BrightnessSlider)) _vm.Brightness = BrightnessSlider.Value;
            else if (ReferenceEquals(sender, ContrastSlider)) _vm.Contrast = ContrastSlider.Value;
            else if (ReferenceEquals(sender, WarmthSlider)) _vm.Warmth = WarmthSlider.Value;
        }

        // ── Свёртывание ───────────────────────────────────────────────────

        private void OnToggleChanged(object? sender, RoutedEventArgs e)
        {
            Panel.IsVisible = ToggleButton.IsChecked == true;
        }

        /// <summary>Развернуть худ на нужной подвкладке.</summary>
        public void Open(int tabIndex)
        {
            Tabs.SelectedIndex = Math.Clamp(tabIndex, 0, Math.Max(0, Tabs.ItemCount - 1));
            ToggleButton.IsChecked = true;
        }
    }
}
