using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Core.Enums;
using Writersword.Core.Interfaces.Services;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Interfaces.Services.UI;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Models.Backup;
using Writersword.Core.Models.Settings;
using Writersword.Resources.Localization;

namespace Writersword.ViewModels.Settings
{
    /// <summary>
    /// ViewModel вкладки «Резервные копии».
    /// Управляет историей версий: включение, папка хранения, лимит точек,
    /// список точек восстановления активного проекта и откат к выбранной.
    /// Настройки применяются немедленно — без кнопки «Применить».
    /// </summary>
    public class BackupSettingsViewModel : ReactiveObject
    {
        private readonly ILogger<BackupSettingsViewModel> _logger;
        private readonly ISettingsService _settingsService;
        private readonly IBackupService _backupService;
        private readonly IDialogService _dialogService;

        /// <summary>Ключ настроек истории версий в ISettingsService.</summary>
        private const string SettingsKey = "backups";

        private bool _enabled;
        private bool _onManualSave;
        private bool _onAppClose;
        private bool _onTimer;
        private bool _thinning;
        private decimal _intervalValue;
        private IntervalUnit _selectedIntervalUnit;
        private int _maxSnapshots;
        private string _maxSnapshotsText;
        private string _storagePath;
        private UserPointRetentionOption _selectedUserPointRetention;
        private decimal _userPointValue;
        private bool _isProjectScope;
        private string _projectStoragePath = string.Empty;
        private string _storageSizeText = string.Empty;
        private bool _isBusy;

        /// <summary>Путь к активному проекту. Пусто — проект не открыт.</summary>
        private readonly string _projectPath;

        public ObservableCollection<BackupPointItem> Points { get; } = new();

        /// <summary>
        /// Готовые значения лимита. Последним идёт «Без ограничения» —
        /// хранится как ноль и отключает потолок, оставляя только прореживание.
        /// </summary>
        public IReadOnlyList<string> MaxSnapshotPresets { get; } = new[]
        {
            "20", "50", "100", Strings.Settings_Backups_Unlimited
        };

        // ── Настройки ─────────────────────────────────────────────────────

        /// <summary>Сохранять ли историю версий при сохранении проекта.</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                this.RaiseAndSetIfChanged(ref _enabled, value);
                SaveSettings();
            }
        }

        /// <summary>Снимать точку при сохранении пользователем (Ctrl+S).</summary>
        public bool OnManualSave
        {
            get => _onManualSave;
            set
            {
                this.RaiseAndSetIfChanged(ref _onManualSave, value);
                SaveSettings();
            }
        }

        /// <summary>Снимать точку при закрытии программы.</summary>
        public bool OnAppClose
        {
            get => _onAppClose;
            set
            {
                this.RaiseAndSetIfChanged(ref _onAppClose, value);
                SaveSettings();
            }
        }

        /// <summary>Снимать точки во время работы, с оглядкой на минимальный интервал.</summary>
        public bool OnTimer
        {
            get => _onTimer;
            set
            {
                this.RaiseAndSetIfChanged(ref _onTimer, value);
                SaveSettings();
            }
        }

        /// <summary>
        /// Число в поле интервала. Единица измерения выбирается отдельно,
        /// поэтому здесь просто количество: «90» может означать и минуты, и часы.
        /// </summary>
        public decimal IntervalValue
        {
            get => _intervalValue;
            set
            {
                // Ноль и отрицательные значения смысла не имеют: интервал
                // отключается не нулём, а снятием галочек с поводов.
                var clamped = value < 1 ? 1 : value;
                this.RaiseAndSetIfChanged(ref _intervalValue, clamped);
                SaveSettings();
            }
        }

        /// <summary>
        /// Текущая единица измерения интервала. Меняется щелчком по кнопке,
        /// которая гоняет её по кругу: минуты → часы → дни → недели → месяцы.
        /// </summary>
        public IntervalUnit SelectedIntervalUnit
        {
            get => _selectedIntervalUnit;
            set
            {
                if (value == null) return;
                this.RaiseAndSetIfChanged(ref _selectedIntervalUnit, value);
                this.RaisePropertyChanged(nameof(IntervalUnitText));
                SaveSettings();
            }
        }

        /// <summary>Подпись на кнопке-переключателе единиц.</summary>
        public string IntervalUnitText => _selectedIntervalUnit?.DisplayName ?? string.Empty;

        /// <summary>Следующая единица по кругу.</summary>
        public ReactiveCommand<Unit, Unit> CycleIntervalUnitCommand { get; }

        /// <summary>
        /// Правило для точек, поставленных кнопкой. Отдельное от общего:
        /// такие точки ставятся осознанно, и сгребать их прореживанием заодно
        /// с автоматическими неправильно.
        /// </summary>
        public UserPointRetentionOption SelectedUserPointRetention
        {
            get => _selectedUserPointRetention;
            set
            {
                if (value == null) return;
                this.RaiseAndSetIfChanged(ref _selectedUserPointRetention, value);
                this.RaisePropertyChanged(nameof(ShowUserPointValue));
                this.RaisePropertyChanged(nameof(UserPointValueLabel));
                SaveSettings();
            }
        }

        /// <summary>Число для выбранного правила: дни или количество точек.</summary>
        public decimal UserPointValue
        {
            get => _userPointValue;
            set
            {
                var clamped = value < 1 ? 1 : value;
                this.RaiseAndSetIfChanged(ref _userPointValue, clamped);
                SaveSettings();
            }
        }

        /// <summary>Правилам «не удалять» и «по общему лимиту» число не нужно.</summary>
        public bool ShowUserPointValue =>
            _selectedUserPointRetention?.Mode is UserPointRetention.AfterAge or UserPointRetention.KeepLast;

        /// <summary>Подпись поля меняется вместе с правилом: дни или штуки.</summary>
        public string UserPointValueLabel => _selectedUserPointRetention?.Mode switch
        {
            UserPointRetention.AfterAge => Strings.Settings_Backups_UserPoints_Days,
            UserPointRetention.KeepLast => Strings.Settings_Backups_UserPoints_Count,
            _ => string.Empty
        };

        /// <summary>Доступные правила для ручных точек.</summary>
        public IReadOnlyList<UserPointRetentionOption> UserPointRetentionOptions { get; } = new[]
        {
            new UserPointRetentionOption(Strings.Settings_Backups_UserPoints_Never, UserPointRetention.Never),
            new UserPointRetentionOption(Strings.Settings_Backups_UserPoints_AfterAge, UserPointRetention.AfterAge),
            new UserPointRetentionOption(Strings.Settings_Backups_UserPoints_KeepLast, UserPointRetention.KeepLast),
            new UserPointRetentionOption(Strings.Settings_Backups_UserPoints_WithLimit, UserPointRetention.WithLimit)
        };

        /// <summary>Сворачивать старые точки по дням и неделям.</summary>
        public bool Thinning
        {
            get => _thinning;
            set
            {
                this.RaiseAndSetIfChanged(ref _thinning, value);
                SaveSettings();
            }
        }

        /// <summary>
        /// Единицы измерения интервала по возрастанию. Значение в настройках
        /// всегда в минутах, единица нужна только для показа: 120 минут удобнее
        /// видеть как 2 часа. Месяц считается тридцатью сутками — календарной
        /// точности здесь не требуется, это порог частоты, а не дата.
        /// </summary>
        public IReadOnlyList<IntervalUnit> IntervalUnits { get; } = new[]
        {
            new IntervalUnit(Strings.Settings_Backups_Unit_Minutes, 1),
            new IntervalUnit(Strings.Settings_Backups_Unit_Hours, 60),
            new IntervalUnit(Strings.Settings_Backups_Unit_Days, 1440),
            new IntervalUnit(Strings.Settings_Backups_Unit_Weeks, 10080),
            new IntervalUnit(Strings.Settings_Backups_Unit_Months, 43200)
        };

        /// <summary>
        /// Предельное число точек как текст: в списке лежат готовые значения
        /// и «Без ограничения», но можно вписать своё число.
        /// Нечитаемый ввод не применяется — поле возвращается к прежнему значению.
        /// </summary>
        public string MaxSnapshotsText
        {
            get => _maxSnapshotsText;
            set
            {
                var parsed = ParseMaxSnapshots(value);

                if (parsed is null)
                {
                    // Возврат прежнего текста: без этого в поле оставался мусор,
                    // а в настройки уходило непонятно что.
                    this.RaisePropertyChanged(nameof(MaxSnapshotsText));
                    return;
                }

                _maxSnapshots = parsed.Value;
                this.RaiseAndSetIfChanged(ref _maxSnapshotsText, FormatMaxSnapshots(parsed.Value));
                SaveSettings();
            }
        }

        /// <summary>
        /// Папка хранения. Пустая строка означает «рядом с проектом».
        /// </summary>
        public string StoragePath
        {
            get => _storagePath;
            set
            {
                this.RaiseAndSetIfChanged(ref _storagePath, value);
                this.RaisePropertyChanged(nameof(EffectiveStoragePath));
                SaveSettings();
            }
        }

        /// <summary>
        /// Какую папку сейчас правит поле пути: общую для всех проектов или
        /// личную для открытого. Переключатель не меняет настройки, а только
        /// показывает ту или другую — так видно обе, не теряя ни одной.
        /// </summary>
        public bool IsProjectScope
        {
            get => _isProjectScope;
            set
            {
                this.RaiseAndSetIfChanged(ref _isProjectScope, value);
                this.RaisePropertyChanged(nameof(IsGlobalScope));
                this.RaisePropertyChanged(nameof(CurrentPath));
                this.RaisePropertyChanged(nameof(CurrentPathWatermark));
            }
        }

        /// <summary>Обратная сторона переключателя — для привязки радиокнопки.</summary>
        public bool IsGlobalScope
        {
            get => !_isProjectScope;
            set
            {
                if (value) IsProjectScope = false;
            }
        }

        /// <summary>
        /// Путь выбранной области. Пусто в личной области означает, что
        /// проект следует общей настройке; пусто в общей — папка профиля.
        /// </summary>
        public string CurrentPath
        {
            get => _isProjectScope ? _projectStoragePath : _storagePath;
            set
            {
                if (_isProjectScope)
                {
                    _projectStoragePath = value ?? string.Empty;
                    this.RaisePropertyChanged(nameof(CurrentPath));
                    _ = ApplyProjectOverrideAsync();
                }
                else
                {
                    _storagePath = value ?? string.Empty;
                    this.RaisePropertyChanged(nameof(CurrentPath));
                    this.RaisePropertyChanged(nameof(EffectiveStoragePath));
                    SaveSettings();
                    _ = RefreshAsync();
                }
            }
        }

        /// <summary>Подсказка в пустом поле объясняет, что будет при пустом значении.</summary>
        public string CurrentPathWatermark => _isProjectScope
            ? Strings.Settings_Backups_Scope_ProjectEmpty
            : Strings.Settings_Backups_UseDefault;

        /// <summary>Переключение на личную область имеет смысл только с открытым проектом.</summary>
        public bool CanUseProjectScope => HasProject;

        /// <summary>Куда реально ляжет история активного проекта — показывается под полем.</summary>
        public string EffectiveStoragePath => string.IsNullOrEmpty(_projectPath)
            ? string.Empty
            : _backupService.GetStoragePath(_projectPath);

        /// <summary>Суммарный размер хранилища на диске в читаемом виде.</summary>
        public string StorageSizeText
        {
            get => _storageSizeText;
            private set => this.RaiseAndSetIfChanged(ref _storageSizeText, value);
        }

        /// <summary>Идёт длительная операция — кнопки списка заблокированы.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isBusy, value);
                this.RaisePropertyChanged(nameof(IsNotBusy));
            }
        }

        public bool IsNotBusy => !_isBusy;

        /// <summary>Открыт ли проект — без него списка точек нет.</summary>
        public bool HasProject => !string.IsNullOrEmpty(_projectPath);

        public bool ShowNoProject => !HasProject;

        /// <summary>Проект открыт, но точек пока не создано.</summary>
        public bool ShowEmpty => HasProject && Points.Count == 0;

        // ── Команды ───────────────────────────────────────────────────────

        /// <summary>Выбрать папку хранения через системный диалог.</summary>
        public ReactiveCommand<Unit, Unit> BrowseCommand { get; }


        /// <summary>Вернуть хранение рядом с проектом.</summary>
        public ReactiveCommand<Unit, Unit> UseDefaultCommand { get; }

        /// <summary>Откатить проект к выбранной точке.</summary>
        public ReactiveCommand<BackupPointItem, Unit> RestoreCommand { get; }

        /// <summary>Создать точку восстановления прямо сейчас.</summary>
        public ReactiveCommand<Unit, Unit> CreatePointCommand { get; }

        /// <summary>Удалить выбранную точку вместе с её объектами.</summary>
        public ReactiveCommand<BackupPointItem, Unit> DeletePointCommand { get; }

        /// <summary>Открыть папку хранилища в проводнике.</summary>
        public ReactiveCommand<Unit, Unit> OpenFolderCommand { get; }

        /// <summary>
        /// Открыть точку в режиме сравнения: обе версии показываются в самом
        /// редакторе с переключателем, ничего не перезаписывая.
        /// </summary>
        public ReactiveCommand<BackupPointItem, Unit> CompareCommand { get; }

        public BackupSettingsViewModel()
        {
            _logger = App.Services.GetService<ILogger<BackupSettingsViewModel>>()!;
            _settingsService = App.Services.GetRequiredService<ISettingsService>();
            _backupService = App.Services.GetRequiredService<IBackupService>();
            _dialogService = App.Services.GetRequiredService<IDialogService>();

            var tabCollection = App.Services.GetRequiredService<ITabCollection>();
            _projectPath = tabCollection.ActiveTab?.FilePath ?? string.Empty;

            var settings = _settingsService.GetModuleSettings<BackupSettings>(SettingsKey)
                           ?? new BackupSettings();

            _enabled = settings.Enabled;
            _onManualSave = settings.OnManualSave;
            _onAppClose = settings.OnAppClose;
            _onTimer = settings.OnTimer;
            _thinning = settings.Thinning;
            _storagePath = settings.StoragePath;

            // Берём самую крупную единицу, в которую значение укладывается без
            // остатка: 120 минут показываются как «2 часа», 10080 — как
            // «1 недель», а 90 остаются минутами.
            int minutes = settings.MinIntervalMinutes > 0 ? settings.MinIntervalMinutes : 60;

            _selectedIntervalUnit = IntervalUnits[0];
            _intervalValue = minutes;

            for (int i = IntervalUnits.Count - 1; i >= 0; i--)
            {
                var unit = IntervalUnits[i];

                if (minutes >= unit.Minutes && minutes % unit.Minutes == 0)
                {
                    _selectedIntervalUnit = unit;
                    _intervalValue = minutes / unit.Minutes;
                    break;
                }
            }

            _maxSnapshots = settings.MaxSnapshots < 0 ? 0 : settings.MaxSnapshots;
            _maxSnapshotsText = FormatMaxSnapshots(_maxSnapshots);

            _selectedUserPointRetention =
                UserPointRetentionOptions.FirstOrDefault(o => o.Mode == settings.UserPointRetention)
                ?? UserPointRetentionOptions[0];

            // Число хранится отдельно для каждого правила, но в поле показывается
            // то, которое относится к выбранному.
            _userPointValue = settings.UserPointRetention == UserPointRetention.KeepLast
                ? Math.Max(1, settings.UserPointKeepLast)
                : Math.Max(1, settings.UserPointMaxAgeDays);

            // Переопределение читается из самого проекта — оно едет вместе с файлом.
            if (HasProject)
            {
                var projectOverride = _backupService.GetProjectStorageOverride(_projectPath);
                _projectStoragePath = projectOverride ?? string.Empty;

                // Если у проекта уже есть свой путь, открываем сразу на нём:
                // иначе настройка выглядела бы потерянной.
                _isProjectScope = !string.IsNullOrWhiteSpace(projectOverride);
            }

            BrowseCommand = ReactiveCommand.CreateFromTask(BrowseAsync);
            // Сброс очищает ту область, что открыта сейчас: общая возвращается
            // к папке профиля, личная — к общей настройке.
            UseDefaultCommand = ReactiveCommand.Create(() => { CurrentPath = string.Empty; });
            RestoreCommand = ReactiveCommand.CreateFromTask<BackupPointItem>(RestoreAsync);
            CreatePointCommand = ReactiveCommand.CreateFromTask(CreatePointAsync);
            CycleIntervalUnitCommand = ReactiveCommand.Create(CycleIntervalUnit);
            DeletePointCommand = ReactiveCommand.CreateFromTask<BackupPointItem>(DeletePointAsync);
            OpenFolderCommand = ReactiveCommand.Create(OpenFolder);
            CompareCommand = ReactiveCommand.CreateFromTask<BackupPointItem>(CompareAsync);

            _ = RefreshAsync();
        }

        // ── Список точек ──────────────────────────────────────────────────

        /// <summary>Перечитать список точек и размер хранилища с диска.</summary>
        private async Task RefreshAsync()
        {
            if (!HasProject)
            {
                this.RaisePropertyChanged(nameof(ShowEmpty));
                return;
            }

            try
            {
                var points = await _backupService.GetSnapshotsAsync(_projectPath);
                var size = await _backupService.GetStorageSizeAsync(_projectPath);
                var current = await _backupService.GetCurrentModuleSizesAsync(_projectPath);

                Points.Clear();

                DateTime? previousDay = null;

                for (int i = 0; i < points.Count; i++)
                {
                    // Разница считается с точкой, которая идёт следом по списку,
                    // то есть с предыдущей по времени: так видно, что выросло,
                    // а что обвалилось именно к этому моменту.
                    var older = i + 1 < points.Count ? points[i + 1] : null;

                    var item = new BackupPointItem(points[i], older, current);

                    var day = points[i].CreatedAt.LocalDateTime.Date;
                    if (previousDay != day)
                    {
                        item.ShowGroupHeader = true;
                        previousDay = day;
                    }

                    Points.Add(item);
                }

                StorageSizeText = FormatSize(size);

                this.RaisePropertyChanged(nameof(ShowEmpty));
                this.RaisePropertyChanged(nameof(EffectiveStoragePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh backup points");
            }
        }

        // ── Восстановление ────────────────────────────────────────────────

        /// <summary>
        /// Откат к выбранной точке. Перед заменой снимается точка текущего
        /// состояния, поэтому сам откат тоже обратим.
        /// </summary>
        private async Task RestoreAsync(BackupPointItem? item)
        {
            if (item == null || !HasProject || IsBusy) return;

            // Несохранённая работа живёт в памяти, а точка «перед откатом»
            // снимается с файла на диске — она такие правки не спасёт.
            // Поэтому предлагаем сохранить их до подмены файла.
            if (!await EnsureWorkSavedAsync())
                return;

            var message = await BuildRestoreWarningAsync(item);

            var confirm = await _dialogService.ShowMessageAsync(
                Strings.Settings_Backups_Confirm_Title,
                message,
                MessageBoxType.Warning,
                MessageBoxButtons.YesNo);

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;

            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var tab = tabCollection.ActiveTab;

                // Точка текущего состояния — откат должен быть обратим.
                await _backupService.CreateSnapshotAsync(_projectPath, BackupTrigger.BeforeRestore);

                // Открытый ZIP держит файл: замена через File.Move иначе упрётся
                // в «file is being used by another process».
                tab?.Context?.CloseZipStorage();

                bool ok;
                try
                {
                    ok = await _backupService.RestoreSnapshotAsync(_projectPath, item.Id, _projectPath);
                }
                finally
                {
                    tab?.Context?.ReopenZipStorage();
                }

                // Файл на диске уже другой, а живые модули держат прежнее
                // состояние — перечитываем проект во вкладку, чтобы результат
                // был виден сразу, без закрытия и повторного открытия.
                if (ok && tab != null)
                {
                    var workflow = App.Services.GetRequiredService<IProjectWorkflow>();
                    await workflow.ReloadFromDiskAsync(tab);
                }

                await _dialogService.ShowMessageAsync(
                    Strings.Settings_Backups_Confirm_Title,
                    ok ? Strings.Settings_Backups_Restored : Strings.Settings_Backups_RestoreFailed,
                    ok ? MessageBoxType.Info : MessageBoxType.Error,
                    MessageBoxButtons.OK);

                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Restore failed for point {Id}", item.Id);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Показать точку рядом с текущей версией в режиме сравнения.
        /// Окно настроек при этом закрывается: сравнение живёт в редакторе,
        /// за модальным окном его не видно.
        /// </summary>
        private async Task CompareAsync(BackupPointItem? item)
        {
            if (item == null || !HasProject || IsBusy) return;

            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var tab = tabCollection.ActiveTab;

                if (tab == null) return;

                var workflow = App.Services.GetRequiredService<IProjectWorkflow>();

                // Закрытие окна откладывается на следующий проход диспетчера:
                // команда вызывается изнутри обработки ввода, и если закрыть
                // окно прямо здесь, открытые попапы (выпадающий список, тултип)
                // остаются без логического родителя — Avalonia падает с
                // "AttachedToLogicalTreeCore called for 'Panel'".
                await Dispatcher.UIThread.InvokeAsync(CloseSettingsWindow, DispatcherPriority.Background);

                await workflow.CompareWithSnapshotAsync(tab, item.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compare with point {Id} failed", item.Id);
            }
        }

        /// <summary>
        /// Закрыть окно настроек штатным путём — через его же команду закрытия,
        /// чтобы изменения настроек применились как при обычном закрытии.
        /// </summary>
        private static void CloseSettingsWindow()
        {
            if (Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            foreach (var window in desktop.Windows.ToList())
            {
                if (window.DataContext is SettingsViewModel settingsVm)
                {
                    settingsVm.CloseCommand.Execute().Subscribe();
                    break;
                }
            }
        }

        /// <summary>
        /// Удалить точку по требованию пользователя.
        ///
        /// Отменить это нельзя: манифест стирается, а вместе с ним и объекты,
        /// на которые больше никто не ссылается. Поэтому спрашиваем прямо,
        /// с датой точки и составом данных в тексте вопроса.
        /// </summary>
        private async Task DeletePointAsync(BackupPointItem? item)
        {
            if (item == null || !HasProject || IsBusy) return;

            var message = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Settings_Backups_Delete_Confirm,
                item.DisplayDate);

            if (!string.IsNullOrEmpty(item.ModulesSummary))
                message += Environment.NewLine + Environment.NewLine + item.ModulesSummary;

            var confirm = await _dialogService.ShowMessageAsync(
                Strings.Settings_Backups_Delete,
                message,
                MessageBoxType.Warning,
                MessageBoxButtons.YesNo);

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;

            try
            {
                bool ok = await _backupService.DeleteSnapshotAsync(_projectPath, item.Id);

                if (!ok)
                {
                    await _dialogService.ShowMessageAsync(
                        Strings.Settings_Backups_Delete,
                        Strings.Settings_Backups_Delete_Failed,
                        MessageBoxType.Error, MessageBoxButtons.OK);
                }

                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete point {Id}", item.Id);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Проверить несохранённую работу перед откатом и дать её сохранить.
        ///
        /// Точка «перед откатом» снимается с файла на диске: всё, что набрано
        /// после последнего сохранения, живёт только в памяти и при подмене
        /// файла исчезнет без следа. Ровно этот сценарий и стоил потери текста,
        /// поэтому здесь он перекрыт явным вопросом.
        ///
        /// Возвращает false, если откат нужно отменить.
        /// </summary>
        private async Task<bool> EnsureWorkSavedAsync()
        {
            try
            {
                var tabCollection = App.Services.GetRequiredService<ITabCollection>();
                var tab = tabCollection.ActiveTab;

                if (tab == null) return true;

                var workflow = App.Services.GetRequiredService<IProjectWorkflow>();

                if (!await workflow.HasUnsavedChanges(tab))
                    return true;

                var answer = await _dialogService.ShowMessageAsync(
                    Strings.Settings_Backups_Confirm_Title,
                    Strings.Settings_Backups_Unsaved_Message,
                    MessageBoxType.Warning,
                    MessageBoxButtons.YesNoCancel);

                if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None)
                    return false;

                if (answer == MessageBoxResult.Yes)
                {
                    bool saved = await workflow.SaveDocumentAsync(tab, showNotification: false);

                    if (!saved)
                    {
                        _logger.LogWarning("Restore cancelled: saving current work failed");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                // Не смогли проверить — откат не начинаем: цена ошибки здесь
                // выше, чем неудобство от лишнего отказа.
                _logger.LogError(ex, "Unsaved work check failed before restore");
                return false;
            }
        }

        /// <summary>
        /// Текст предупреждения перед откатом: дата точки, построчное сравнение
        /// данных модулей «сейчас → станет» и отдельная строка, если откат
        /// уменьшает данные. Именно этой строки не хватало, когда документ
        /// схлопнулся молча.
        /// </summary>
        private async Task<string> BuildRestoreWarningAsync(BackupPointItem item)
        {
            var text = new StringBuilder();
            text.AppendLine(string.Format(
                CultureInfo.CurrentCulture, Strings.Settings_Backups_Confirm_Message, item.DisplayDate));

            try
            {
                var current = await _backupService.GetCurrentModuleSizesAsync(_projectPath);

                if (current.Count > 0 || item.ModuleSizes.Count > 0)
                {
                    text.AppendLine();
                    text.AppendLine(Strings.Settings_Backups_Confirm_Changes);

                    bool shrinks = false;

                    var names = current.Keys.Union(item.ModuleSizes.Keys, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

                    foreach (var name in names)
                    {
                        current.TryGetValue(name, out long now);
                        item.ModuleSizes.TryGetValue(name, out long then);

                        if (then < now) shrinks = true;

                        text.AppendLine($"    {name}: {FormatSize(now)} → {FormatSize(then)}");
                    }

                    if (shrinks)
                    {
                        text.AppendLine();
                        text.AppendLine(Strings.Settings_Backups_Confirm_Shrink);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build restore warning");
            }

            return text.ToString();
        }

        /// <summary>
        /// Создать точку прямо сейчас. Такие точки не подчиняются минимальному
        /// интервалу и не удаляются прореживанием — пользователь пометил момент
        /// осознанно.
        /// </summary>
        private async Task CreatePointAsync()
        {
            if (!HasProject || IsBusy) return;

            IsBusy = true;

            try
            {
                bool ok = await _backupService.CreateSnapshotAsync(_projectPath, BackupTrigger.UserPoint);

                if (!ok)
                {
                    await _dialogService.ShowMessageAsync(
                        Strings.Settings_Backups_Confirm_Title,
                        Strings.Settings_Backups_PointSkipped,
                        MessageBoxType.Info, MessageBoxButtons.OK);
                }

                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual backup point failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Папка хранения ────────────────────────────────────────────────

        /// <summary>Показать хранилище в системном файловом менеджере.</summary>
        private void OpenFolder()
        {
            try
            {
                if (!HasProject) return;

                var path = _backupService.GetStoragePath(_projectPath);
                Directory.CreateDirectory(path);

                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open backup storage folder");
            }
        }

        /// <summary>
        /// Записать путь проекта внутрь самого проекта и обновить показ.
        /// Пустое значение убирает переопределение.
        /// </summary>
        private async Task ApplyProjectOverrideAsync()
        {
            if (!HasProject) return;

            try
            {
                var value = string.IsNullOrWhiteSpace(_projectStoragePath) ? null : _projectStoragePath;

                await _backupService.SetProjectStorageOverrideAsync(_projectPath, value);

                this.RaisePropertyChanged(nameof(EffectiveStoragePath));
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply project storage override");
            }
        }

        /// <summary>Системный выбор папки — для той области, что сейчас открыта.</summary>
        private async Task BrowseAsync()
        {
            var path = await PickFolderAsync();

            if (string.IsNullOrEmpty(path)) return;

            CurrentPath = path;
        }

        /// <summary>Системный диалог выбора папки. Пусто — отмена или ошибка.</summary>
        private async Task<string?> PickFolderAsync()
        {
            try
            {
                if (Application.Current?.ApplicationLifetime
                    is not IClassicDesktopStyleApplicationLifetime desktop
                    || desktop.MainWindow == null)
                    return null;

                var folders = await desktop.MainWindow.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions
                    {
                        Title = Strings.Settings_Backups_Location,
                        AllowMultiple = false
                    });

                return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Folder picker failed");
                return null;
            }
        }

        // ── Сохранение настроек ───────────────────────────────────────────

        private void SaveSettings()
        {
            try
            {
                var settings = new BackupSettings
                {
                    Enabled = _enabled,
                    OnManualSave = _onManualSave,
                    OnAppClose = _onAppClose,
                    OnTimer = _onTimer,
                    Thinning = _thinning,
                    MinIntervalMinutes = CurrentIntervalMinutes,
                    MaxSnapshots = _maxSnapshots,
                    StoragePath = _storagePath ?? string.Empty,
                    UserPointRetention = _selectedUserPointRetention?.Mode ?? UserPointRetention.Never,
                    UserPointMaxAgeDays = _selectedUserPointRetention?.Mode == UserPointRetention.AfterAge
                        ? (int)decimal.Truncate(_userPointValue)
                        : 90,
                    UserPointKeepLast = _selectedUserPointRetention?.Mode == UserPointRetention.KeepLast
                        ? (int)decimal.Truncate(_userPointValue)
                        : 20
                };

                _settingsService.SaveModuleSettings(SettingsKey, settings);
                _settingsService.Save();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving backup settings");
            }
        }

        // ── Хелперы ───────────────────────────────────────────────────────

        /// <summary>
        /// Переключить единицу на следующую, после последней — снова первая.
        /// Число в поле не пересчитывается: «30 минут» становится «30 часов»,
        /// то есть кнопка меняет масштаб, а не переводит одно в другое.
        /// </summary>
        private void CycleIntervalUnit()
        {
            int index = _selectedIntervalUnit == null
                ? 0
                : IntervalUnits.ToList().IndexOf(_selectedIntervalUnit);

            SelectedIntervalUnit = IntervalUnits[(index + 1) % IntervalUnits.Count];
        }

        /// <summary>
        /// Интервал в минутах из числа и выбранной единицы.
        /// Ниже пяти минут не опускается: история версий отвечает на вопрос
        /// «к чему вернуться», и точка раз в минуту делает список нечитаемым,
        /// не добавляя надёжности — от падения защищает автосохранение.
        /// </summary>
        private int CurrentIntervalMinutes
        {
            get
            {
                int perUnit = _selectedIntervalUnit?.Minutes ?? 1;
                int value = (int)decimal.Truncate(_intervalValue);
                int minutes = value < 1 ? perUnit : value * perUnit;
                return minutes < MinimumIntervalMinutes ? MinimumIntervalMinutes : minutes;
            }
        }

        /// <summary>Нижняя граница интервала между автоматическими точками.</summary>
        private const int MinimumIntervalMinutes = 5;

        /// <summary>
        /// Разбор поля лимита: число, «Без ограничения» или пустая строка.
        /// Возвращает null, если введено что-то другое — тогда значение
        /// не применяется.
        /// </summary>
        private static int? ParseMaxSnapshots(string? text)
        {
            var trimmed = text?.Trim();

            if (string.IsNullOrEmpty(trimmed))
                return null;

            if (string.Equals(trimmed, Strings.Settings_Backups_Unlimited, StringComparison.CurrentCultureIgnoreCase))
                return 0;

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed)
                || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed > 0 ? parsed : 0;
            }

            return null;
        }

        /// <summary>Обратное преобразование: ноль показывается как «Без ограничения».</summary>
        private static string FormatMaxSnapshots(int value)
            => value > 0
                ? value.ToString(CultureInfo.CurrentCulture)
                : Strings.Settings_Backups_Unlimited;

        /// <summary>Размер в байтах в читаемый вид: КБ, МБ, ГБ.</summary>
        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;

            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", size, units[unit]);
        }
    }

    /// <summary>
    /// Пункт списка правил для ручных точек.
    /// </summary>
    public class UserPointRetentionOption
    {
        public string DisplayName { get; }
        public UserPointRetention Mode { get; }

        public UserPointRetentionOption(string displayName, UserPointRetention mode)
        {
            DisplayName = displayName;
            Mode = mode;
        }
    }

    /// <summary>
    /// Единица измерения интервала: подпись и сколько в ней минут.
    /// </summary>
    public class IntervalUnit
    {
        public string DisplayName { get; }

        /// <summary>Минут в одной единице: 1 для минут, 60 для часов.</summary>
        public int Minutes { get; }

        public IntervalUnit(string displayName, int minutes)
        {
            DisplayName = displayName;
            Minutes = minutes;
        }
    }

    /// <summary>
    /// Строка списка точек восстановления.
    /// </summary>
    public class BackupPointItem
    {
        public string Id { get; }

        /// <summary>Дата и время точки в локальном формате.</summary>
        public string DisplayDate { get; }

        /// <summary>Причина создания и объём — вторая строка в списке.</summary>
        public string Details { get; }

        /// <summary>
        /// Данные модулей в точке: «Текст 45,2 KB · Персонажи 709 KB».
        /// Третья строка списка. По ней видно состояние точки без восстановления:
        /// документ, схлопнувшийся до болванки, весит единицы килобайт.
        /// </summary>
        public string ModulesSummary { get; }

        public bool HasModulesSummary => !string.IsNullOrEmpty(ModulesSummary);

        /// <summary>Размеры данных модулей в точке — для предупреждения перед откатом.</summary>
        public Dictionary<string, long> ModuleSizes { get; }

        /// <summary>Заголовок дня над карточкой: «Сегодня», «Вчера» или дата.</summary>
        public string GroupHeader { get; }

        /// <summary>Показывать ли заголовок — он ставится только у первой точки дня.</summary>
        public bool ShowGroupHeader { get; set; }

        /// <summary>Время без даты: дата уже вынесена в заголовок группы.</summary>
        public string DisplayTime { get; }

        /// <summary>«15 мин назад», «3 ч назад» — рядом с точным временем.</summary>
        public string RelativeTime { get; }

        /// <summary>
        /// Цвет метки причины. Приглушённые оттенки намеренно: синий и янтарный
        /// в настройках заняты под глобальные и проектные секции, и повторять их
        /// здесь нельзя — метки читались бы как принадлежность к секции.
        /// </summary>
        public string TriggerColor { get; }

        /// <summary>
        /// Что изменилось относительно предыдущей точки: «Characters +56 KB,
        /// TextEditor −340 KB». Пусто, если сравнивать не с чем или размеры равны.
        /// </summary>
        public string DeltaSummary { get; }

        public bool HasDelta => !string.IsNullOrEmpty(DeltaSummary);

        /// <summary>Точка совпадает по составу с тем, что сейчас лежит в проекте.</summary>
        public bool IsCurrent { get; }

        public BackupPointItem(
            BackupSnapshotInfo info,
            BackupSnapshotInfo? older = null,
            Dictionary<string, long>? currentSizes = null)
        {
            Id = info.Id;
            ModuleSizes = info.ModuleSizes;

            var created = info.CreatedAt.LocalDateTime;

            DisplayDate = created.ToString("dd.MM.yyyy  HH:mm", CultureInfo.CurrentCulture);
            DisplayTime = created.ToString("HH:mm", CultureInfo.CurrentCulture);
            GroupHeader = BuildGroupHeader(created);
            RelativeTime = BuildRelativeTime(created);

            var trigger = info.Trigger switch
            {
                BackupTrigger.AutoSave => Strings.Settings_Backups_Trigger_Auto,
                BackupTrigger.BeforeRestore => Strings.Settings_Backups_Trigger_BeforeRestore,
                BackupTrigger.AppClose => Strings.Settings_Backups_Trigger_AppClose,
                BackupTrigger.UserPoint => Strings.Settings_Backups_Trigger_UserPoint,
                _ => Strings.Settings_Backups_Trigger_Manual
            };

            TriggerColor = info.Trigger switch
            {
                BackupTrigger.UserPoint => "#6B9E78",      // приглушённый зелёный
                BackupTrigger.BeforeRestore => "#A86A5B",  // терракот
                BackupTrigger.AppClose => "#7C6F9B",       // лиловый
                BackupTrigger.AutoSave => "#5F6B78",       // серо-синий
                _ => "#8A8A8F"                             // нейтральный серый
            };

            Details = $"{trigger} · {BackupSettingsViewModel.FormatSize(info.TotalLength)}";

            ModulesSummary = string.Join(
                " · ",
                info.ModuleSizes
                    .OrderByDescending(kvp => kvp.Value)
                    .Select(kvp => $"{kvp.Key} {BackupSettingsViewModel.FormatSize(kvp.Value)}"));

            DeltaSummary = BuildDelta(info.ModuleSizes, older?.ModuleSizes);
            IsCurrent = currentSizes != null && SameSizes(info.ModuleSizes, currentSizes);
        }

        private static string BuildGroupHeader(DateTime created)
        {
            var today = DateTime.Today;

            if (created.Date == today)
                return Strings.Settings_Backups_Group_Today;

            if (created.Date == today.AddDays(-1))
                return Strings.Settings_Backups_Group_Yesterday;

            return created.ToString("dd MMMM yyyy", CultureInfo.CurrentCulture);
        }

        private static string BuildRelativeTime(DateTime created)
        {
            var passed = DateTime.Now - created;

            if (passed.TotalMinutes < 1)
                return Strings.Settings_Backups_JustNow;

            if (passed.TotalHours < 1)
                return string.Format(CultureInfo.CurrentCulture,
                    Strings.Settings_Backups_MinutesAgo, (int)passed.TotalMinutes);

            if (passed.TotalDays < 1)
                return string.Format(CultureInfo.CurrentCulture,
                    Strings.Settings_Backups_HoursAgo, (int)passed.TotalHours);

            return string.Format(CultureInfo.CurrentCulture,
                Strings.Settings_Backups_DaysAgo, (int)passed.TotalDays);
        }

        /// <summary>
        /// Прирост и убыль по модулям относительно предыдущей точки.
        /// Именно по этой строке выбирают, к чему откатываться: видно, где
        /// работа прибавилась, а где данные обвалились.
        /// </summary>
        private static string BuildDelta(Dictionary<string, long> now, Dictionary<string, long>? before)
        {
            if (before == null || before.Count == 0)
                return string.Empty;

            var parts = new List<string>();

            foreach (var name in now.Keys.Union(before.Keys, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                now.TryGetValue(name, out long a);
                before.TryGetValue(name, out long b);

                long diff = a - b;
                if (diff == 0) continue;

                var sign = diff > 0 ? "+" : "−";
                parts.Add($"{name} {sign}{BackupSettingsViewModel.FormatSize(Math.Abs(diff))}");
            }

            return string.Join("  ", parts);
        }

        private static bool SameSizes(Dictionary<string, long> a, Dictionary<string, long> b)
        {
            if (a.Count != b.Count) return false;

            foreach (var kvp in a)
            {
                if (!b.TryGetValue(kvp.Key, out long other) || other != kvp.Value)
                    return false;
            }

            return true;
        }
    }
}
