using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Writersword.Modules.Characters.Models;
using Writersword.Modules.Characters.ViewModels.Templates;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharactersTemplatesView : UserControl
    {
        public CharactersTemplatesView() => InitializeComponent();

        /// <summary>
        /// Открыть конструктор набора. Окно хостится в CharactersModuleView
        /// поверх содержимого — как редактор метки и настройки карточки.
        /// </summary>
        private void OnEditCustomTemplateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control control) return;
            if (control.DataContext is not TemplateItemViewModel item) return;
            if (DataContext is not CharactersTemplatesViewModel vm) return;

            var host = this.FindAncestorOfType<CharactersModuleView>();
            var overlay = host?.FindControl<AnketaEditorOverlay>("AnketaEditorOverlayControl");
            if (overlay == null) return;

            var anketa = vm.GetAnketa(item.AnketaId);
            if (anketa == null) return;

            overlay.ShowFor(anketa, updated => vm.SaveCustomTemplate(updated), vm.GetKnownFields());
        }

        private static readonly ILogger _logger = Log.ForContext<CharactersTemplatesView>();

        // Обмен наборами: набор — это определение полей, оно самодостаточно
        // и переносится между проектами и людьми одним файлом. Отсюда и берётся
        // сравнимость карточек с чужими: общий набор даёт общие идентификаторы
        // полей, тогда как одинаковые по смыслу имена сами по себе ничего
        // не значат.
        private static readonly FilePickerFileType SetFileType = new("Набор полей")
        {
            Patterns = new[] { "*.wsset", "*.json" }
        };

        private async void OnExportTemplateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not Control control) return;
            if (control.DataContext is not TemplateItemViewModel item) return;
            if (DataContext is not CharactersTemplatesViewModel vm) return;

            var anketa = vm.GetAnketa(item.AnketaId);
            if (anketa == null) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            try
            {
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Сохранить набор",
                    SuggestedFileName = anketa.Name,
                    DefaultExtension = "wsset",
                    FileTypeChoices = new List<FilePickerFileType> { SetFileType }
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

        private async void OnImportTemplateClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (DataContext is not CharactersTemplatesViewModel vm) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Открыть набор",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType> { SetFileType }
                });

                if (files == null || files.Count == 0) return;

                await using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();

                var anketa = JsonConvert.DeserializeObject<CharacterAnketa>(json);
                if (anketa == null)
                {
                    _logger.Warning("Anketa import: file is not a field set");
                    return;
                }

                vm.ImportAnketa(anketa);
            }
            catch (Exception ex)
            {
                // Чужой файл может оказаться чем угодно: битым, не тем форматом,
                // просто картинкой. Это не повод ронять вкладку.
                _logger.Error(ex, "Anketa import failed");
            }
        }
    }
}
