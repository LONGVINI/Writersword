using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using Writersword.Resources.Localization;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для баннера восстановления версий
    /// Позволяет переключаться между кешем и сохранённой версией
    /// </summary>
    public class RecoveryBannerViewModel : ViewModelBase
    {
        private readonly ILogger<RecoveryBannerViewModel> _logger;
        private bool _isViewingCache;

        /// <summary>Дата создания кеша (автосохранение)</summary>
        public DateTime CacheDate { get; set; }

        /// <summary>Дата последнего сохранения файла</summary>
        public DateTime SaveDate { get; set; }

        /// <summary>
        /// Подпись верхней строки дат. По умолчанию «Автосохранение».
        /// В режиме сравнения двух файлов сюда попадает имя второго файла.
        /// </summary>
        public string LeftLabel { get; set; } = Strings.AutoSave_Time_AutoSave;

        /// <summary>
        /// Подпись нижней строки дат. По умолчанию «Сохранено».
        /// В режиме сравнения двух файлов — имя текущего проекта.
        /// </summary>
        public string RightLabel { get; set; } = Strings.AutoSave_Time_Saved;

        /// <summary>Заголовок, когда выбрана верхняя версия.</summary>
        public string LeftVersionText { get; set; } = Strings.Recovery_Banner_ViewingCache;

        /// <summary>Заголовок, когда выбрана нижняя версия.</summary>
        public string RightVersionText { get; set; } = Strings.Recovery_Banner_ViewingSaved;

        /// <summary>Надпись на кнопке выхода из режима.</summary>
        public string DiscardButtonText { get; set; } = Strings.Recovery_Button_DeleteCache;

        /// <summary>Просматриваем ли кеш (true) или сохранённую версию (false)</summary>
        public bool IsViewingCache
        {
            get => _isViewingCache;
            set
            {
                this.RaiseAndSetIfChanged(ref _isViewingCache, value);
                this.RaisePropertyChanged(nameof(CurrentVersionText));
                this.RaisePropertyChanged(nameof(CurrentVersionColor));
                this.RaisePropertyChanged(nameof(CacheFontWeight));
                this.RaisePropertyChanged(nameof(SavedFontWeight));
                this.RaisePropertyChanged(nameof(IsViewingSaved));
            }
        }

        /// <summary>Показывать ли стрелку у сохранённой версии</summary>
        public bool IsViewingSaved => !IsViewingCache;

        /// <summary>Текст текущей версии (локализованный)</summary>
        public string CurrentVersionText => IsViewingCache
            ? LeftVersionText
            : RightVersionText;

        /// <summary>Цвет текста текущей версии</summary>
        public string CurrentVersionColor => IsViewingCache
            ? "#FFA500"
            : "#00C853";

        /// <summary>Жирность шрифта для строки с кешем</summary>
        public string CacheFontWeight => IsViewingCache ? "Bold" : "Normal";

        /// <summary>Жирность шрифта для строки с сохранённой версией</summary>
        public string SavedFontWeight => IsViewingCache ? "Normal" : "Bold";

        /// <summary>Команда переключения между версиями</summary>
        public ReactiveCommand<Unit, Unit> SwitchVersionCommand { get; }

        /// <summary>Команда сохранения текущей версии</summary>
        public ReactiveCommand<Unit, Unit> SaveCommand { get; }

        /// <summary>Команда удаления кеша</summary>
        public ReactiveCommand<Unit, Unit> DiscardCommand { get; }

        /// <summary>
        /// Конструктор
        /// </summary>
        /// <param name="onSwitchVersion">Callback переключения версии</param>
        /// <param name="onSave">Callback сохранения</param>
        /// <param name="onDiscard">Callback удаления кеша</param>
        public RecoveryBannerViewModel(
            Func<Task> onSwitchVersion,
            Func<Task> onSave,
            Func<Task> onDiscard)
        {
            _logger = App.Services.GetService<ILogger<RecoveryBannerViewModel>>()!;

            SwitchVersionCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                _logger.LogDebug("SwitchVersion command executed");
                await onSwitchVersion();
            });

            SaveCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                _logger.LogDebug("Save command executed");
                await onSave();
            });

            DiscardCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                _logger.LogDebug("Discard command executed");
                await onDiscard();
            });

            _logger.LogDebug("Initialized");
        }
    }
}