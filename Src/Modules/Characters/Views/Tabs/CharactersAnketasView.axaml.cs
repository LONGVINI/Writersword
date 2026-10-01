using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Writersword.Modules.Characters.Controls;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Modules.Characters.ViewModels.Anketas;

namespace Writersword.Modules.Characters.Views.Tabs
{
    /// <summary>
    /// Редактор анкет: листы-вкладки, лента, лист анкеты и панель свойств поля.
    /// Логика правки — во вьюмодели листа; здесь указатель (выбор, перенос
    /// листов, строк и полей), всплывающие окна, клавиши и файлы.
    /// Перенос — своей обработкой указателя, а не DragDrop: за курсором едет
    /// «призрак», линия или подсветка ячейки показывает место.
    /// </summary>
    public partial class CharactersAnketasView : UserControl
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersAnketasView>();

        private const double DragThreshold = 5;

        /// <summary>Свой значок больше этого — почти наверняка не значок.</summary>
        private const long MaxGlyphFileBytes = 5 * 1024 * 1024;

        public CharactersAnketasView()
        {
            InitializeComponent();

            // Без фокуса на самой вкладке Delete после щелчка по полю ушёл бы
            // в поле ввода, где курсор стоял до этого.
            Focusable = true;

            PropertyChanged += (_, e) =>
            {
                if (e.Property != IsVisibleProperty) return;

                if (e.NewValue is false)
                {
                    CancelDrag();
                    GlyphFlyout.Hide();
                    Vm?.FlushDrafts();
                }
                else
                {
                    // Анкеты могли удалить или поменять в шаблонах, пока вкладка была скрыта.
                    Vm?.Refresh();
                }
            };

            RowsList.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        private CharactersAnketasViewModel? Vm => DataContext as CharactersAnketasViewModel;

        private AnketaSheetViewModel? Sheet => Vm?.ActiveSheet;

        private Flyout GlyphFlyout => (Flyout)Resources["GlyphFlyout"]!;

        // ── Жизненный цикл ───────────────────────────────────────────────

        private CharactersAnketasViewModel? _subscribed;
        private TopLevel? _topLevel;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_subscribed != null) _subscribed.NameFocusRequested -= OnNameFocusRequested;
            _subscribed = Vm;
            if (_subscribed != null) _subscribed.NameFocusRequested += OnNameFocusRequested;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _topLevel = TopLevel.GetTopLevel(this);
            _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);

            Vm?.Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            CancelDrag();
            GlyphFlyout.Hide();

            _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            _topLevel = null;

            Vm?.FlushDrafts();

            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Новую анкету почти всегда сразу переименовывают — курсор в название.</summary>
        private void OnNameFocusRequested(AnketaSheetViewModel sheet)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(Vm?.ActiveSheet, sheet)) return;

                CanvasScroll.Offset = new Vector(CanvasScroll.Offset.X, 0);
                SheetNameBox.Focus();
                SheetNameBox.SelectAll();
            }, DispatcherPriority.Background);
        }

        // ── Клавиши ──────────────────────────────────────────────────────
        //
        // Ctrl+Z, Ctrl+Y и Ctrl+S ловит модуль и отдаёт вьюмодели редактора;
        // здесь — только то, что касается самого листа.

        private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsEffectivelyVisible || Vm is not { } vm) return;

            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                if (_drag != null)
                {
                    CancelDrag();
                    e.Handled = true;
                    return;
                }

                if (vm.ActiveSheet is { IsPreviewOpen: true } previewed)
                {
                    previewed.IsPreviewOpen = false;
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
            {
                if (vm.ActiveSheet is not { IsEditable: true, IsPreviewOpen: false } sheet) return;
                if (sheet.SelectedField is not { } field) return;
                if (IsTextInputFocused()) return;

                sheet.DeleteField(field);
                e.Handled = true;
            }
        }

        private bool IsTextInputFocused() =>
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused &&
            IsInsideTextInput(focused);

        private static bool IsInsideTextInput(Visual visual) =>
            visual.GetSelfAndVisualAncestors().Any(v => v is TextBox);

        // ── Выбор на листе ───────────────────────────────────────────────

        /// <summary>
        /// Щелчок по ячейке выбирает её поле, по пустой ячейке или подзаголовку —
        /// строку. Событие не гасится: поля превью и ручки работают как обычно.
        /// </summary>
        private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (Sheet is not { } sheet) return;
            if (e.Source is not Visual source) return;

            var properties = e.GetCurrentPoint(RowsList).Properties;
            if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;

            foreach (var visual in source.GetSelfAndVisualAncestors())
            {
                if (ReferenceEquals(visual, RowsList)) break;
                if (visual is not Control control) continue;

                if (control.Classes.Contains("cell") && control.DataContext is AnketaLayoutCellViewModel cell)
                {
                    if (cell.HasField)
                    {
                        sheet.SelectedField = cell.Field;
                    }
                    else
                    {
                        sheet.SelectedField = null;
                        sheet.SelectedRow = cell.Row;
                    }
                    break;
                }

                if (control.Classes.Contains("row") && control.DataContext is AnketaLayoutRowViewModel row)
                {
                    // Кнопки колонок строки не должны снимать выбор с её же поля.
                    var keepField = sheet.SelectedField != null &&
                                    row.Cells.Any(c => ReferenceEquals(c.Field, sheet.SelectedField));
                    if (!keepField) sheet.SelectedField = null;
                    sheet.SelectedRow = row;
                    break;
                }
            }

            if (!IsInsideTextInput(source)) Focus(NavigationMethod.Pointer);
        }

        private void BringRowIntoView(AnketaLayoutRowViewModel? row)
        {
            if (row == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (RowsList.ContainerFromItem(row) is Control container)
                    container.BringIntoView();
            }, DispatcherPriority.Background);
        }

        private void FocusFieldName()
        {
            SecLabel.IsChecked = true;

            Dispatcher.UIThread.Post(() =>
            {
                BringRowIntoView(Sheet?.SelectedRow);
                FieldNameBox.Focus();
                FieldNameBox.SelectAll();
            }, DispatcherPriority.Background);
        }

        // ── Листы ────────────────────────────────────────────────────────

        private AnketaSheetViewModel? _closingSheet;
        private Control? _closingAnchor;

        private void OnSheetTabPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control tab || tab.DataContext is not AnketaSheetViewModel sheet || Vm is not { } vm) return;

            var point = e.GetCurrentPoint(tab);

            if (point.Properties.IsMiddleButtonPressed)
            {
                var close = tab.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(b => b.Classes.Contains("sheet-close"));
                RequestClose(sheet, close ?? tab);
                e.Handled = true;
                return;
            }

            if (!point.Properties.IsLeftButtonPressed) return;

            vm.ActiveSheet = sheet;

            _drag = new DragState
            {
                Kind = DragKind.Sheet,
                Sheet = sheet,
                Start = e.GetPosition(DragLayer)
            };
            e.Pointer.Capture(tab);
            e.Handled = true;
        }

        private void OnSheetTabMoved(object? sender, PointerEventArgs e)
        {
            if (_drag is not { Kind: DragKind.Sheet } drag) return;
            if (!Track(drag, e, out var p)) return;

            PlaceSheetMarker(drag, p);
            e.Handled = true;
        }

        private void OnSheetTabReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(e);

        private void OnSheetTabCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CancelDrag();

        private void OnCloseSheetClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control button || button.DataContext is not AnketaSheetViewModel sheet) return;
            RequestClose(sheet, button);
        }

        /// <summary>Несохранённый лист закрывается только после вопроса.</summary>
        private void RequestClose(AnketaSheetViewModel sheet, Control anchor)
        {
            if (Vm is not { } vm) return;

            if (!sheet.IsDirty || sheet.IsReadOnly)
            {
                vm.CloseSheet(sheet);
                return;
            }

            vm.ActiveSheet = sheet;
            _closingSheet = sheet;
            _closingAnchor = anchor;

            if (FlyoutBase.GetAttachedFlyout(anchor) != null)
                FlyoutBase.ShowAttachedFlyout(anchor);
        }

        private void OnCloseDiscardClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            var sheet = _closingSheet;
            HideCloseFlyout();
            if (sheet != null) Vm?.CloseSheet(sheet);
        }

        private void OnCloseSaveClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            var sheet = _closingSheet;
            HideCloseFlyout();
            if (sheet == null || Vm is not { } vm) return;

            vm.Save(sheet);
            if (!sheet.IsDirty) vm.CloseSheet(sheet);
        }

        private void HideCloseFlyout()
        {
            if (_closingAnchor != null) FlyoutBase.GetAttachedFlyout(_closingAnchor)?.Hide();
            _closingSheet = null;
            _closingAnchor = null;
        }

        // ── Открытие анкеты ──────────────────────────────────────────────

        private FlyoutBase? _pickerFlyout;

        private void OnPickerOpening(object? sender, EventArgs e)
        {
            _pickerFlyout = sender as FlyoutBase;
            Vm?.OpenPicker();
        }

        private void OnPickerClosed(object? sender, EventArgs e) => Vm?.ClosePicker();

        private void HidePicker() => _pickerFlyout?.Hide();

        private void OnPickAnketaClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is not AnketaPickItemViewModel item) return;

            HidePicker();
            Vm?.OpenAnketa(item.Id);
        }

        private void OnNewAnketaClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            HidePicker();
            Vm?.CreateAnketa();
        }

        // ── Файлы ────────────────────────────────────────────────────────
        //
        // Анкета самодостаточна: поля, раскладка и свои значки уезжают одним
        // файлом в другой проект или к другому автору.

        private static readonly FilePickerFileType AnketaFileType = new("Анкета")
        {
            Patterns = new[] { "*.wsset", "*.json" }
        };

        private static readonly FilePickerFileType GlyphFileType = new("Значок")
        {
            Patterns = new[] { "*.svg", "*.png", "*.jpg", "*.jpeg" }
        };

        private async void OnExportClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (Sheet is not { } sheet || Vm is not { } vm) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            try
            {
                var anketa = vm.BuildForExport(sheet);

                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Сохранить анкету в файл",
                    SuggestedFileName = anketa.Name,
                    DefaultExtension = "wsset",
                    FileTypeChoices = new List<FilePickerFileType> { AnketaFileType }
                });

                if (file == null) return;

                var json = JsonConvert.SerializeObject(anketa, Formatting.Indented);
                await using var stream = await file.OpenWriteAsync();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(json);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Anketa export failed");
            }
        }

        private async void OnImportClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            HidePicker();

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null || Vm is not { } vm) return;

            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Открыть анкету из файла",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType> { AnketaFileType }
                });

                if (files == null || files.Count == 0) return;

                await using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();

                var anketa = JsonConvert.DeserializeObject<CharacterAnketa>(json);
                if (anketa == null)
                {
                    _logger.Warning("Anketa import: file is not an anketa");
                    return;
                }

                vm.ImportAnketa(anketa);
            }
            catch (Exception ex)
            {
                // Чужой файл может оказаться чем угодно — это не повод ронять вкладку.
                _logger.Error(ex, "Anketa import failed");
            }
        }

        // ── Лента ────────────────────────────────────────────────────────

        private void OnUndoClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Sheet?.Undo();
        }

        private void OnRedoClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Sheet?.Redo();
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Vm?.Save(Sheet);
        }

        private void OnSaveAllClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Vm?.SaveAll();
        }

        private void OnAddFieldClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control { Tag: string tag }) return;
            if (!Enum.TryParse<CharacterParameterType>(tag, out var type)) return;
            if (Sheet is not { IsEditable: true } sheet) return;

            if (sheet.AddField(type) != null) FocusFieldName();
        }

        private void OnAddRowClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control { Tag: string tag }) return;
            if (!int.TryParse(tag, out var columns)) return;
            if (Sheet is not { IsEditable: true } sheet) return;

            sheet.AddRow(columns);
            BringRowIntoView(sheet.SelectedRow);
        }

        private void OnAddGroupClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (Sheet is not { IsEditable: true } sheet) return;
            if (sheet.AddGroup() is not { } row) return;

            // Подзаголовок сразу переименовывают: курсор в его название.
            Dispatcher.UIThread.Post(() =>
            {
                if (RowsList.ContainerFromItem(row) is not Control container) return;

                container.BringIntoView();
                var title = container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible);
                if (title == null) return;

                title.Focus();
                title.SelectAll();
            }, DispatcherPriority.Background);
        }

        private void OnHintClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet?.SelectedField == null) return;

            SecHint.IsChecked = true;

            Dispatcher.UIThread.Post(() =>
            {
                FieldHintBox.BringIntoView();
                FieldHintBox.Focus();
                FieldHintBox.CaretIndex = FieldHintBox.Text?.Length ?? 0;
            }, DispatcherPriority.Background);
        }

        private void OnDuplicateFieldClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is not { IsEditable: true } sheet || sheet.SelectedField is not { } field) return;

            if (sheet.DuplicateField(field) != null) FocusFieldName();
        }

        private void OnDeleteFieldClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is not { IsEditable: true } sheet || sheet.SelectedField is not { } field) return;

            sheet.DeleteField(field);
        }

        private void OnMakeCopyClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is not { } sheet || Vm is not { } vm) return;

            vm.DuplicateAnketa(sheet);
        }

        private void OnDeleteAnketaClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is Control button) ClosePopupOf(button);
            if (Sheet is not { IsEditable: true } sheet || Vm is not { } vm) return;

            vm.DeleteAnketa(sheet);
        }

        private void OnPreviewClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is { } sheet) sheet.IsPreviewOpen = true;
        }

        /// <summary>Закрыть всплывающее окно, внутри которого кнопка.</summary>
        private static void ClosePopupOf(Control control)
        {
            foreach (var ancestor in control.GetLogicalAncestors())
            {
                if (ancestor is Popup popup)
                {
                    popup.IsOpen = false;
                    return;
                }
            }
        }

        // ── Шапка анкеты ─────────────────────────────────────────────────

        private void OnAnketaIconClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Control button || Sheet is not { IsEditable: true } sheet) return;

            sheet.PrepareIconOptions();
            FlyoutBase.ShowAttachedFlyout(button);
        }

        // ── Строки ───────────────────────────────────────────────────────

        private void OnColumnsClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not ToggleButton { Tag: string tag } toggle) return;
            if (!int.TryParse(tag, out var columns)) return;
            if (toggle.DataContext is not AnketaLayoutRowViewModel row) return;

            row.Sheet.SetColumns(row, columns);

            // Повторный щелчок по уже выбранному числу колонок снял бы отметку:
            // привязка односторонняя, значение во вьюмодели при этом не меняется.
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, (bool?)(row.Columns == columns));
        }

        private void OnDeleteRowClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is not AnketaLayoutRowViewModel row) return;

            row.Sheet.DeleteRow(row);
        }

        private void OnRowGripPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control grip || grip.DataContext is not AnketaLayoutRowViewModel row) return;
            if (!row.Sheet.IsEditable) return;
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

            _drag = new DragState
            {
                Kind = DragKind.Row,
                Sheet = row.Sheet,
                Row = row,
                Start = e.GetPosition(DragLayer)
            };
            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        private void OnRowGripMoved(object? sender, PointerEventArgs e)
        {
            if (_drag is not { Kind: DragKind.Row } drag) return;
            if (!Track(drag, e, out var p)) return;

            PlaceRowMarker(drag, p);
            AutoScroll(e);
            e.Handled = true;
        }

        private void OnRowGripReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(e);

        private void OnRowGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CancelDrag();

        // ── Поля в ячейках ───────────────────────────────────────────────

        private void OnCellGripPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control grip || grip.DataContext is not AnketaLayoutCellViewModel cell) return;
            if (!cell.HasField || !cell.Row.Sheet.IsEditable) return;
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

            var cellVisual = grip.GetVisualAncestors()
                .OfType<Control>()
                .FirstOrDefault(c => c.Classes.Contains("cell"));

            _drag = new DragState
            {
                Kind = DragKind.Cell,
                Sheet = cell.Row.Sheet,
                Cell = cell,
                SourceVisual = cellVisual,
                Start = e.GetPosition(DragLayer)
            };
            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        private void OnCellGripMoved(object? sender, PointerEventArgs e)
        {
            if (_drag is not { Kind: DragKind.Cell } drag) return;
            if (!Track(drag, e, out _)) return;

            UpdateCellTarget(drag, e);
            AutoScroll(e);
            e.Handled = true;
        }

        private void OnCellGripReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(e);

        private void OnCellGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CancelDrag();

        // ── Вид: копировать и вставить ───────────────────────────────────

        private void OnCopyDisplayClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is not { } sheet || sheet.SelectedField is not { } field || Vm is not { } vm) return;

            sheet.CopyDisplay(field);

            // Буфер вида общий для всех листов — вставка доступна и на них.
            foreach (var other in vm.Sheets)
                other.RefreshClipboard();
        }

        private void OnPasteDisplayClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is not { IsEditable: true } sheet || sheet.SelectedField is not { } field) return;

            sheet.PasteDisplay(field);
        }

        private void OnPasteDisplayGroupClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is not { IsEditable: true } sheet || sheet.SelectedField is not { } field) return;

            sheet.PasteDisplayToGroup(field);
        }

        // ── Значки подписи и оценки ──────────────────────────────────────

        private const string GlyphTargetLabel = "label";
        private const string GlyphTargetRating = "rating";

        private string _glyphTarget = GlyphTargetLabel;

        private void OnGlyphPickClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control button) return;
            if (Sheet is not { IsEditable: true } sheet || sheet.SelectedField == null) return;

            _glyphTarget = button.Tag as string == GlyphTargetRating ? GlyphTargetRating : GlyphTargetLabel;

            var flyout = GlyphFlyout;
            if (flyout.Content is Control content)
            {
                content.DataContext = sheet;

                var clear = content.GetLogicalDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(b => b.Name == "GlyphClearButton");
                if (clear?.Content is TextBlock text)
                    text.Text = _glyphTarget == GlyphTargetRating ? "Как было" : "Без значка";
            }

            flyout.ShowAt(button);
        }

        private void OnGlyphChosen(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is not AnketaGlyphOptionViewModel option) return;

            ApplyGlyph(_glyphTarget, option.Glyph);
            GlyphFlyout.Hide();
        }

        private void OnGlyphClearClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            // У оценки пустое значение — значок по умолчанию.
            ApplyGlyph(_glyphTarget, string.Empty);
            GlyphFlyout.Hide();
        }

        private async void OnGlyphUploadClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (Sheet is not { IsEditable: true } sheet) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            var target = _glyphTarget;
            var field = sheet.SelectedField;
            GlyphFlyout.Hide();

            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Свой значок",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType> { GlyphFileType }
                });

                if (files == null || files.Count == 0) return;

                byte[] data;
                await using (var stream = await files[0].OpenReadAsync())
                using (var memory = new MemoryStream())
                {
                    await stream.CopyToAsync(memory);
                    data = memory.ToArray();
                }

                if (data.Length == 0 || data.Length > MaxGlyphFileBytes)
                {
                    _logger.Warning("Glyph import: unsupported size {Size}", data.Length);
                    return;
                }

                var glyph = CharacterGlyphs.FromFile(data, files[0].Name);
                if (glyph == null)
                {
                    _logger.Warning("Glyph import: '{Name}' is not an icon", files[0].Name);
                    return;
                }

                var added = sheet.AddAsset(glyph, Path.GetFileNameWithoutExtension(files[0].Name));
                if (added == null || field == null) return;

                if (target == GlyphTargetRating) field.RatingGlyph = added;
                else field.LabelIcon = added;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Glyph import failed");
            }
        }

        private void ApplyGlyph(string target, string glyph)
        {
            if (Sheet is not { IsEditable: true } sheet || sheet.SelectedField is not { } field) return;

            if (target == GlyphTargetRating) field.RatingGlyph = glyph;
            else field.LabelIcon = glyph;
        }

        // ── Превью ───────────────────────────────────────────────────────

        private void OnPreviewScrimPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is { } sheet) sheet.IsPreviewOpen = false;
        }

        /// <summary>Щелчок по самому окну превью не должен доходить до затемнения и закрывать его.</summary>
        private void OnPreviewPanelPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        private void OnPreviewCloseClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (Sheet is { } sheet) sheet.IsPreviewOpen = false;
        }

        // ── Перетаскивание ───────────────────────────────────────────────

        private enum DragKind { Sheet, Row, Cell }

        private sealed class DragState
        {
            public DragKind Kind;
            public Point Start;
            public bool Active;
            public AnketaSheetViewModel? Sheet;
            public AnketaLayoutRowViewModel? Row;
            public AnketaLayoutCellViewModel? Cell;
            public AnketaLayoutCellViewModel? TargetCell;
            public Control? SourceVisual;

            /// <summary>Место вставки листа или строки; -1 — бросок ничего не меняет.</summary>
            public int Index = -1;
        }

        private DragState? _drag;

        /// <summary>
        /// Перенос начинается, когда указатель ушёл дальше порога; до этого
        /// нажатие остаётся щелчком. Призрак едет за курсором.
        /// </summary>
        private bool Track(DragState drag, PointerEventArgs e, out Point p)
        {
            p = e.GetPosition(DragLayer);

            if (!drag.Active)
            {
                if (Math.Abs(p.X - drag.Start.X) < DragThreshold && Math.Abs(p.Y - drag.Start.Y) < DragThreshold)
                    return false;

                drag.Active = true;
                StartDragVisuals(drag);
            }

            Canvas.SetLeft(Ghost, p.X + 12);
            Canvas.SetTop(Ghost, p.Y + 10);
            return true;
        }

        private void StartDragVisuals(DragState drag)
        {
            switch (drag.Kind)
            {
                case DragKind.Sheet:
                    drag.Sheet!.IsDragSource = true;
                    GhostText.Text = drag.Sheet.Name;
                    break;

                case DragKind.Row:
                    drag.Row!.IsDragSource = true;
                    GhostText.Text = RowCaption(drag.Row);
                    break;

                case DragKind.Cell:
                    if (drag.SourceVisual != null) drag.SourceVisual.Opacity = 0.4;
                    GhostText.Text = drag.Cell!.Field?.DisplayName ?? string.Empty;
                    break;
            }

            Ghost.IsVisible = true;
        }

        private static string RowCaption(AnketaLayoutRowViewModel row)
        {
            if (row.IsGroup) return string.IsNullOrWhiteSpace(row.Title) ? "Группа" : row.Title;

            var names = row.Cells.Where(c => c.HasField).Select(c => c.Field!.DisplayName).ToList();
            return names.Count == 0 ? "Пустая строка" : string.Join(" · ", names);
        }

        private void EndDrag(PointerReleasedEventArgs e)
        {
            if (_drag == null) return;

            var active = _drag.Active;
            FinishDrag(commit: true);
            e.Pointer.Capture(null);
            if (active) e.Handled = true;
        }

        private void CancelDrag() => FinishDrag(commit: false);

        private void FinishDrag(bool commit)
        {
            var drag = _drag;
            _drag = null;
            if (drag == null) return;

            Ghost.IsVisible = false;
            DropLine.IsVisible = false;

            if (drag.Sheet != null) drag.Sheet.IsDragSource = false;
            if (drag.Row != null) drag.Row.IsDragSource = false;
            drag.SourceVisual?.ClearValue(OpacityProperty);
            if (drag.TargetCell != null) drag.TargetCell.IsDropTarget = false;

            if (!commit || !drag.Active || Vm is not { } vm) return;

            switch (drag.Kind)
            {
                case DragKind.Sheet when drag.Index >= 0:
                    vm.MoveSheet(drag.Sheet!, drag.Index);
                    break;

                case DragKind.Row when drag.Index >= 0:
                    drag.Sheet!.MoveRow(drag.Row!, drag.Index);
                    break;

                case DragKind.Cell when drag.TargetCell != null:
                    drag.Sheet!.MoveField(drag.Cell!, drag.TargetCell);
                    break;
            }
        }

        /// <summary>Прямоугольник элемента в координатах слоя перетаскивания.</summary>
        private Rect? RectInLayer(Control? control)
        {
            if (control == null || !control.IsEffectivelyVisible) return null;

            var topLeft = control.TranslatePoint(new Point(0, 0), DragLayer);
            return topLeft.HasValue ? new Rect(topLeft.Value, control.Bounds.Size) : null;
        }

        /// <summary>
        /// Место листа: перед первой вкладкой, левее середины которой курсор,
        /// иначе в конец. Своё же место — не перестановка.
        /// </summary>
        private void PlaceSheetMarker(DragState drag, Point p)
        {
            if (Vm is not { } vm) return;

            var sheets = vm.Sheets;
            var from = sheets.IndexOf(drag.Sheet!);
            var target = sheets.Count;
            Rect? first = null;
            Rect? last = null;
            double lineX = 0;

            for (int i = 0; i < sheets.Count; i++)
            {
                if (RectInLayer(SheetTabs.ContainerFromIndex(i) as Control) is not { } r) continue;
                first ??= r;
                last = r;

                if (target == sheets.Count && p.X < r.X + r.Width / 2)
                {
                    target = i;
                    lineX = r.X - 1;
                }
            }

            if (last == null || first == null || target == from || target == from + 1)
            {
                drag.Index = -1;
                DropLine.IsVisible = false;
                return;
            }

            if (target == sheets.Count) lineX = last.Value.Right - 1;

            drag.Index = target;
            DropLine.Width = 2;
            DropLine.Height = first.Value.Height;
            Canvas.SetLeft(DropLine, lineX);
            Canvas.SetTop(DropLine, first.Value.Y);
            DropLine.IsVisible = true;
        }

        /// <summary>
        /// Место строки: перед первой строкой, выше середины которой курсор,
        /// иначе в конец. Своё же место — не перестановка.
        /// </summary>
        private void PlaceRowMarker(DragState drag, Point p)
        {
            var rows = drag.Sheet!.Rows;
            var from = rows.IndexOf(drag.Row!);
            var target = rows.Count;
            Rect? last = null;
            double lineY = 0;

            for (int i = 0; i < rows.Count; i++)
            {
                if (RectInLayer(RowsList.ContainerFromIndex(i) as Control) is not { } r) continue;
                last = r;

                if (target == rows.Count && p.Y < r.Y + r.Height / 2)
                {
                    target = i;
                    lineY = r.Y - 2;
                }
            }

            if (last == null || target == from || target == from + 1)
            {
                drag.Index = -1;
                DropLine.IsVisible = false;
                return;
            }

            if (target == rows.Count) lineY = last.Value.Bottom;

            drag.Index = target;
            DropLine.Width = Math.Max(0, last.Value.Width);
            DropLine.Height = 3;
            Canvas.SetLeft(DropLine, last.Value.X);
            Canvas.SetTop(DropLine, lineY - 0.5);
            DropLine.IsVisible = true;
        }

        /// <summary>Ячейка того же листа под курсором — туда встанет поле, её поле — на его место.</summary>
        private void UpdateCellTarget(DragState drag, PointerEventArgs e)
        {
            AnketaLayoutCellViewModel? target = null;

            if (this.InputHitTest(e.GetPosition(this)) is Visual hit)
            {
                foreach (var visual in hit.GetSelfAndVisualAncestors())
                {
                    if (visual is not Control control) continue;
                    if (!control.Classes.Contains("cell")) continue;

                    if (control.DataContext is AnketaLayoutCellViewModel cell &&
                        !ReferenceEquals(cell, drag.Cell) &&
                        ReferenceEquals(cell.Row.Sheet, drag.Sheet))
                        target = cell;
                    break;
                }
            }

            if (ReferenceEquals(drag.TargetCell, target)) return;

            if (drag.TargetCell != null) drag.TargetCell.IsDropTarget = false;
            drag.TargetCell = target;
            if (target != null) target.IsDropTarget = true;
        }

        /// <summary>У краёв листа он прокручивается сам — дотащить можно до любой строки.</summary>
        private void AutoScroll(PointerEventArgs e)
        {
            const double edge = 40;
            const double step = 14;

            var p = e.GetPosition(CanvasScroll);
            if (p.X < 0 || p.X > CanvasScroll.Bounds.Width) return;

            var offset = CanvasScroll.Offset;
            if (p.Y > -edge && p.Y < edge && offset.Y > 0)
                CanvasScroll.Offset = offset.WithY(Math.Max(0, offset.Y - step));
            else if (p.Y > CanvasScroll.Bounds.Height - edge && p.Y < CanvasScroll.Bounds.Height + edge)
                CanvasScroll.Offset = offset.WithY(offset.Y + step);
        }
    }
}
