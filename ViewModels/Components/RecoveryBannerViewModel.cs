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
        private bool _isViewingCache;

        /// <summary>Дата создания кеша (автосохранение)</summary>
        public DateTime CacheDate { get; set; }

        /// <summary>Дата последнего сохранения файла</summary>
        public DateTime SaveDate { get; set; }

        /// <summary>Просматриваем ли кеш (true) или сохранённую версию (false)</summary>
        public bool IsViewingCache
        {
            get => _isViewingCache;
            set
            {
                this.RaiseAndSetIfChanged(ref _isViewingCache, value);

                // Обновляем все зависимые свойства для UI
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
            ? Strings.Recovery_Banner_ViewingCache
            : Strings.Recovery_Banner_ViewingSaved;

        /// <summary>Цвет текста текущей версии</summary>
        public string CurrentVersionColor => IsViewingCache
            ? "#FFA500"  // Оранжевый для кеша
            : "#00C853"; // Зелёный для сохранённой версии

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
            SwitchVersionCommand = ReactiveCommand.CreateFromTask(onSwitchVersion);
            SaveCommand = ReactiveCommand.CreateFromTask(onSave);
            DiscardCommand = ReactiveCommand.CreateFromTask(onDiscard);
        }
    }
}