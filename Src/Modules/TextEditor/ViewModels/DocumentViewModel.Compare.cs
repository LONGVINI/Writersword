using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using DocumentFormat.OpenXml.Packaging;
using ReactiveUI;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Services;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Resources;
using Writersword.Modules.TextEditor.Services;

namespace Writersword.Modules.TextEditor.ViewModels
{
    /// <summary>
    /// «Сравнить с файлом» на вкладке «Рецензирование» — как «Сравнить документы» в
    /// Word.
    ///
    /// Рукопись отдали, её поправили без записи исправлений и вернули файлом. Файл
    /// разбирается, сверяется с открытой рукописью (<see cref="FileCompare"/>), и все
    /// различия ложатся в рукопись правками, подписанными автором файла. Дальше с
    /// ними работают как с обычными правками: принять, отклонить, по одной или все
    /// сразу. Сравнение — один шаг отмены.
    /// </summary>
    public sealed partial class DocumentViewModel
    {
        /// <summary>Название шага отмены сравнения.</summary>
        public const string CompareUndoDescription = "Сравнение с файлом";

        /// <summary>Выбрать файл и разметить различия с ним правками.</summary>
        public void CompareWithFile() => _ = CompareWithFileAsync();

        private async Task CompareWithFileAsync()
        {
            var notifications = CoreServices.GetService<INotificationService>();

            if (IsReadOnly)
            {
                notifications?.ShowWarning("Документ открыт только для чтения: разметить различия в нём нельзя");
                return;
            }

            // Сравнение принимает непринятые правки рукописи — под защитой это запрещено.
            if (IsRevisionLocked)
            {
                NotifyRevisionLocked();
                return;
            }

            var window = (Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (window?.StorageProvider is null) return;

            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Сравнить с файлом",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(TextEditorStrings.FileType_Import)
                    {
                        Patterns = new[] { "*.docx", "*.txt" }
                    },
                    new FilePickerFileType(TextEditorStrings.FileType_Docx)
                    {
                        Patterns = new[] { "*.docx" }
                    },
                    new FilePickerFileType(TextEditorStrings.FileType_Txt)
                    {
                        Patterns = new[] { "*.txt" }
                    }
                }
            });

            if (files.Count == 0) return;

            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                notifications?.ShowError(TextEditorStrings.Import_FileNotFound);
                return;
            }

            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".docx" && extension != ".txt")
            {
                notifications?.ShowWarning(string.Format(TextEditorStrings.Import_Unsupported, extension));
                return;
            }

            // Сравнивается текст, каким он стал: непринятые правки рукописи сначала
            // принимаются — так же поступает Word. Человек решает, согласен ли.
            bool hadRevisions = RevisionService.CountRevisions(_document) > 0;
            if (hadRevisions)
            {
                var dialogs = CoreServices.GetService<IDialogService>();
                if (dialogs is not null)
                {
                    var answer = await dialogs.ShowMessageAsync(
                        "Непринятые правки",
                        "В рукописи есть непринятые правки. Сравнение ведётся с текстом, каким он станет после их " +
                        "принятия, поэтому перед сравнением они будут приняты. Вернуть всё как было можно одной " +
                        "отменой (Ctrl+Z). Продолжить?",
                        Writersword.Core.Enums.MessageBoxType.Question,
                        Writersword.Core.Enums.MessageBoxButtons.YesNo);

                    if (answer != Writersword.Core.Enums.MessageBoxResult.Yes) return;
                }
            }

            // Правка рукописи, пока файл разбирается и сверяется, ушла бы при замене
            // содержимого: такое сравнение не применяется.
            bool changedMeanwhile = false;
            void OnChangedMeanwhile() => changedMeanwhile = true;
            ContentModified += OnChangedMeanwhile;

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                ImportResult imported;
                try
                {
                    var importer = new ImportService();
                    imported = extension == ".docx"
                        ? await importer.ImportFromDocxAsync(path)
                        : await importer.ImportFromTxtAsync(path);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[COMPARE] Не удалось разобрать {Path}", path);
                    notifications?.ShowError(string.Format(TextEditorStrings.Import_Failed, ex.Message));
                    return;
                }

                if (!imported.Success || imported.Document is null)
                {
                    _log.Warning("[COMPARE] Файл не разобран: {Error}", imported.ErrorMessage);
                    notifications?.ShowError(string.Format(TextEditorStrings.Import_Failed, imported.ErrorMessage ?? string.Empty));
                    return;
                }

                long parseMs = stopwatch.ElapsedMilliseconds;

                var revised = imported.Document;
                var revisedImages = imported.ExtractedImages;
                var info = await Task.Run(() => FileRevisionInfo(path));

                // Картинки рукописи читаются здесь, в потоке UI: хранилище проекта для
                // параллельного чтения не рассчитано. Сверка — в фоне, над копией.
                var currentImages = CollectDocumentImages();
                var working = DocumentCloner.Clone(_document);

                var compared = await Task.Run(() =>
                {
                    var currentKeys = HashImages(currentImages);
                    var revisedKeys = HashImages(revisedImages);

                    FileCompare.AcceptAll(working);
                    FileCompare.AcceptAll(revised);

                    return FileCompare.Apply(
                        working,
                        revised,
                        info,
                        image => KeyOf(currentKeys, image),
                        image => KeyOf(revisedKeys, image));
                });

                if (changedMeanwhile)
                {
                    notifications?.ShowWarning("Рукопись менялась, пока шло сравнение, — различия не размечены. Повторите сравнение");
                    return;
                }

                string fileName = System.IO.Path.GetFileName(path);

                _log.Information(
                    "[COMPARE] {File}: разбор {Parse} мс, сверка {Compare} мс; вставок {Ins}, удалений {Del}, оформления {Fmt}, пропущено {Skip}",
                    fileName, parseMs, stopwatch.ElapsedMilliseconds - parseMs,
                    compared.InsertedPlaces, compared.DeletedPlaces, compared.FormatPlaces, compared.SkippedPlaces);

                if (compared.Total == 0 && compared.CommentsAdded == 0)
                {
                    notifications?.ShowInfo(compared.SkippedPlaces > 0
                        ? $"Текст рукописи и «{fileName}» совпадает. Различий в объектах, которых в Word не бывает: {compared.SkippedPlaces}"
                        : $"Рукопись и «{fileName}» совпадают — различий нет");
                    return;
                }

                // Картинки из файла, вошедшие в рукопись, — в проект фоном, как при
                // импорте; пока запись идёт, лист берёт их из буфера.
                if (compared.AdoptedImageFiles.Count > 0)
                {
                    var adopted = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in compared.AdoptedImageFiles)
                        if (revisedImages.TryGetValue(name, out var bytes))
                            adopted[name] = bytes;

                    var ctx = CoreServices.GetService<ITabCollection>()?.ActiveTab?.Context;
                    if (ctx is null)
                    {
                        notifications?.ShowWarning(TextEditorStrings.Import_ImagesNotSaved);
                    }
                    else if (adopted.Count > 0)
                    {
                        PendingProjectImages.AddRange(adopted);
                        WriteImportedImagesInBackground(ctx, adopted, notifications);
                    }
                }

                ApplyComparedDocument(working);

                notifications?.ShowSuccess(
                    $"Сравнение с «{fileName}»: вставок {compared.InsertedPlaces}, удалений {compared.DeletedPlaces}, " +
                    $"смен оформления {compared.FormatPlaces}" +
                    (compared.CommentsAdded > 0 ? $", примечаний {compared.CommentsAdded}" : string.Empty) +
                    $". Правки подписаны «{info.Author}»");

                if (compared.SkippedPlaces > 0)
                    notifications?.ShowWarning(
                        $"Не размечено различий: {compared.SkippedPlaces} — в объектах, которых в файле Word не бывает");
            }
            finally
            {
                ContentModified -= OnChangedMeanwhile;
            }
        }

        /// <summary>
        /// Переносит размеченное содержимое в рукопись одним шагом отмены. Сам объект
        /// документа не подменяется: на него ссылаются вкладка, полотно и сохранение.
        /// </summary>
        private void ApplyComparedDocument(DocumentModel compared)
        {
            AutoTrackingSuppressed = true;
            try
            {
                BeginEditDelegate?.Invoke(CompareUndoDescription);

                _document.Sections.Clear();
                foreach (var section in compared.Sections)
                    _document.Sections.Add(section);

                // Примечания редактора из файла приходят вместе с текстом.
                _document.Comments.Clear();
                foreach (var comment in compared.Comments)
                    _document.Comments.Add(comment);

                if (_document.Sections.Count == 0)
                {
                    var section = new SectionModel();
                    section.Blocks.Add(new ParagraphBlock());
                    _document.Sections.Add(section);
                }

                CommitEditDelegate?.Invoke();
            }
            finally
            {
                AutoTrackingSuppressed = false;
            }

            _activeParagraph = null;
            TableActiveCellParagraph = null;

            RebuildParagraphViewModels();
            FireParagraphFormatChanged();

            // Пометки видны только при показе всех исправлений.
            if (_document.RevisionView != RevisionView.AllMarkup)
            {
                _document.RevisionView = RevisionView.AllMarkup;
                this.RaisePropertyChanged(nameof(RevisionView));
                RevisionViewChanged?.Invoke();
            }

            StructureChanged?.Invoke();
            DocumentRestored?.Invoke();
            ContentModified?.Invoke();
            RevisionsChanged?.Invoke();
        }

        /// <summary>
        /// Подпись правок сравнения: кто последним правил файл (свойства документа
        /// Word), а если это неизвестно — имя файла; время — когда файл сохранён.
        /// </summary>
        private static RevisionInfo FileRevisionInfo(string path)
        {
            string? author = null;

            if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var package = WordprocessingDocument.Open(path, false);
                    var properties = package.PackageProperties;
                    author = !string.IsNullOrWhiteSpace(properties.LastModifiedBy)
                        ? properties.LastModifiedBy
                        : properties.Creator;
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "[COMPARE] Свойства документа {Path} не прочитаны", path);
                }
            }

            if (string.IsNullOrWhiteSpace(author))
                author = System.IO.Path.GetFileNameWithoutExtension(path);

            DateTime saved;
            try
            {
                saved = System.IO.File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                saved = DateTime.UtcNow;
            }

            return new RevisionInfo
            {
                Author = author!.Trim(),
                Date = new DateTime(saved.Year, saved.Month, saved.Day, saved.Hour, saved.Minute, 0, DateTimeKind.Utc)
            };
        }

        /// <summary>Отпечатки картинок по именам файлов: одинаковые байты — одна картинка.</summary>
        private static Dictionary<string, string> HashImages(Dictionary<string, byte[]> images)
        {
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var image in images)
            {
                if (image.Value is null || image.Value.Length == 0) continue;
                keys[image.Key] = Convert.ToHexString(SHA256.HashData(image.Value));
            }
            return keys;
        }

        private static string? KeyOf(Dictionary<string, string> keys, ImageBlock image)
            => !string.IsNullOrWhiteSpace(image.ImageFileName) && keys.TryGetValue(image.ImageFileName, out var key)
                ? key
                : null;
    }
}
