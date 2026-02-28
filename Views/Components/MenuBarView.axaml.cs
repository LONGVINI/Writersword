using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using Writersword.Src.Core.Interfaces.Services.Input;
using Writersword.ViewModels.Components;

namespace Writersword.Views.Components
{
    /// <summary>
    /// Code-behind главного меню.
    /// Автоматически обновляет отображение жестов у MenuItem
    /// чьё x:Name совпадает с ID зарегистрированной горячей клавиши в HotKeyService.
    /// Соглашение: x:Name="HotKey_File_New" -> ищет HotKey с Id="HotKey_File_New".
    /// Отображает первый жест мульти-бинда. Последовательности форматирует как "Ctrl+K -> Ctrl+C".
    /// </summary>
    public partial class MenuBarView : UserControl
    {
        private readonly ILogger<MenuBarView> _logger;
        private readonly IHotKeyService _hotKeyService;

        public MenuBarView()
        {
            _logger = App.Services.GetService<ILogger<MenuBarView>>()!;
            _hotKeyService = App.Services.GetRequiredService<IHotKeyService>();
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            _hotKeyService.HotKeysChanged += OnHotKeysChanged;
            _logger.LogDebug("MenuBarView created");
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is MenuBarViewModel vm)
            {
                vm.OpenRecentProjectCommand.Subscribe(_ =>
                {
                    Dispatcher.UIThread.Post(() => MainMenu.Close());
                });
            }

            UpdateAllGestures();
        }

        /// <summary>
        /// Вызывается при изменении любой горячей клавиши.
        /// Маршалим на UI поток так как HotKeysChanged может прийти из любого потока.
        /// </summary>
        private void OnHotKeysChanged()
        {
            Dispatcher.UIThread.Post(UpdateAllGestures);
        }

        /// <summary>
        /// Обходит всё дерево меню и для каждого MenuItem у которого Name
        /// начинается с "HotKey_" ищет соответствующий жест в HotKeyService.
        /// Обновляет TextBlock с именем "GestureHint" внутри хедера MenuItem.
        /// </summary>
        private void UpdateAllGestures()
        {
            UpdateGesturesRecursive(MainMenu);
        }

        /// <summary>
        /// Рекурсивный обход дерева MenuItem.
        /// Обрабатывает вложенные подменю.
        /// </summary>
        private void UpdateGesturesRecursive(ItemsControl parent)
        {
            foreach (var item in parent.Items)
            {
                if (item is not MenuItem menuItem) continue;

                if (!string.IsNullOrEmpty(menuItem.Name) &&
                    menuItem.Name.StartsWith("HotKey_", StringComparison.Ordinal))
                {
                    var gestureHint = menuItem.FindControl<TextBlock>("GestureHint");
                    if (gestureHint != null)
                    {
                        gestureHint.Text = BuildGestureString(menuItem.Name);
                    }
                }

                if (menuItem.Items.Count > 0)
                    UpdateGesturesRecursive(menuItem);
            }
        }

        /// <summary>
        /// Формирует строку отображения жеста для указанного ID.
        /// Берёт первый жест из ActiveGestures.
        /// Одиночный: "Ctrl+S". Последовательность: "Ctrl+K -> Ctrl+C".
        /// Если жест не назначен — возвращает пустую строку.
        /// </summary>
        private string BuildGestureString(string hotKeyId)
        {
            var hotKey = _hotKeyService.GetHotKey(hotKeyId);
            if (hotKey == null || hotKey.ActiveGestures.Count == 0)
                return string.Empty;

            var first = hotKey.ActiveGestures[0];

            if (first.IsSingle)
                return first.FirstStep.ToString();

            // Последовательность — форматируем шаги через " -> "
            return string.Join(" -> ", first.Steps);
        }
    }
}