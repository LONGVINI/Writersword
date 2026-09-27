using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Settings;
using Writersword.Modules.TextEditor.ViewModels.Toolbar;
using Writersword.Styles.UserControls;

namespace Writersword.Modules.TextEditor.Views.Toolbar.Tabs
{
    public partial class RibbonAppearanceTab : UserControl
    {
        private RibbonScrollContainer? _scrollContainer;

        public RibbonAppearanceTab()
        {
            InitializeComponent();
            SizeChanged += OnSizeChanged;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>Нажали «Настроить виды» — список видов больше не нужен.</summary>
        private void OnConfigureThemesClick(object? sender, RoutedEventArgs e)
        {
            // Список закрывается следующим тактом, а не сейчас.
            //
            // Закрытый сразу, он уносит с собой и нажатую кнопку: она уходит из
            // дерева до того, как кнопка дойдёт до своей команды, и «Настроить виды»
            // переставало открывать окно вовсе.
            var source = sender;
            Dispatcher.UIThread.Post(() => CloseHostFlyout(source), DispatcherPriority.Background);
        }

        /// <summary>
        /// Закрывает всплывающий список, из которого нажали кнопку.
        ///
        /// Список видов живёт во флайауте и сам по себе не закрывается: кнопка
        /// «Настроить виды» открывает поверх него окно, а список остаётся висеть у
        /// верхнего края и уходит только от первого нажатия мимо. Человек уже видит
        /// открытое окно, и висящий над ним список читается как подвисшая панель.
        /// </summary>
        private static void CloseHostFlyout(object? sender)
        {
            if (sender is not Control control) return;
            if (TopLevel.GetTopLevel(control) is not PopupRoot root) return;
            if (root.Parent is not Popup popup) return;

            // Через сам флайаут, а не через окно: тогда кнопка-владелец узнаёт, что
            // список закрыт, и не остаётся в нажатом виде.
            if (popup.PlacementTarget is Button { Flyout: { } flyout })
            {
                flyout.Hide();
                return;
            }

            popup.Close();
        }

        /// <summary>
        /// Загрузка картинки фона прямо из ленты — так же, как в ленте чтения.
        /// Выбранные файлы укладываются в хранилище так же, как из окна видов: в архив
        /// проекта, когда он открыт, и в данные программы, когда нет. Путь к файлу на
        /// диске вид не пережил бы — ни переезда папки, ни передачи проекта.
        ///
        /// Нажатие из свёрнутой группы приходит из флайаута: он закрывается до
        /// открытия окна выбора, иначе висел бы над ним.
        /// </summary>
        private async void OnLoadBackdropImageClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not RibbonAppearanceTabViewModel vm) return;
            if (!vm.IsThemeApplied) return;

            var source = sender;
            Dispatcher.UIThread.Post(() => CloseHostFlyout(source), DispatcherPriority.Background);

            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is not { } storage) return;

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Картинки фона",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" }
                    }
                }
            });

            if (files.Count == 0) return;

            var stored = new List<string>();
            foreach (var file in files)
            {
                string? path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path)) continue;

                string reference = StoreBackdropImage(path!);
                if (!stored.Contains(reference)) stored.Add(reference);
            }

            vm.SetBackdropImages(stored);
        }

        /// <summary>
        /// Уложить файл в хранилище вида и вернуть его адрес. Не уложилось —
        /// остаётся прежний путь: он хотя бы работает здесь и сейчас.
        /// </summary>
        private static string StoreBackdropImage(string path)
        {
            var stored = ReadingAssets.EnsureInProject(path);
            if (ReadingAssets.IsProjectRef(stored)) return stored!;

            return ReadingAssets.EnsureInAppStore(path) ?? path;
        }


        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _scrollContainer = this.FindControl<RibbonScrollContainer>("ScrollContainer");
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is RibbonAppearanceTabViewModel vm)
            {
                vm.UpdateLayout(e.NewSize.Width);
                if (_scrollContainer is not null)
                {
                    _scrollContainer.ArrowsVisible = true;
                    _scrollContainer.NotifySizeChanged();
                }
            }
        }
    }
}
