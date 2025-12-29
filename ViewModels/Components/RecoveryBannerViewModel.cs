using System;
using System.Reactive;
using ReactiveUI;
using Writersword.Resources.Localization;

namespace Writersword.ViewModels
{
    /// <summary>
    /// ViewModel для баннера восстановления версий
    /// Управляет отображением и переключением между сохранённой версией и автосохранением
    /// </summary>
    public class RecoveryBannerViewModel : ViewModelBase
    {
        private bool _isViewingCache;
        private DateTime _cacheDate;
        private DateTime _saveDate;

        /// <summary>Просматривается ли версия из кеша</summary>
        public bool IsViewingCache
        {
            get => _isViewingCache;
            set => this.RaiseAndSetIfChanged(ref _isViewingCache, value);
        }

        /// <summary>Дата автосохранения</summary>
        public DateTime CacheDate
        {
            get => _cacheDate;
            set => this.RaiseAndSetIfChanged(ref _cacheDate, value);
        }

        /// <summary>Дата сохранения основного файла</summary>
        public DateTime SaveDate
        {
            get => _saveDate;
            set => this.RaiseAndSetIfChanged(ref _saveDate, value);
        }

        /// <summary>Текст версии для отображения</summary>
        public string VersionText => IsViewingCache
            ? $"{Strings.AutoSave_Banner_ViewingCache} ({CacheDate:HH:mm:ss})"
            : $"{Strings.AutoSave_Banner_ViewingSaved} ({SaveDate:HH:mm:ss})";

        /// <summary>Текст кнопки переключения</summary>
        public string SwitchButtonText => IsViewingCache
            ? Strings.AutoSave_Button_SwitchToSaved
            : Strings.AutoSave_Button_SwitchToCache;

        /// <summary>Команда переключения версий</summary>
        public ReactiveCommand<Unit, Unit> SwitchVersionCommand { get; set; }

        /// <summary>Команда сохранения</summary>
        public ReactiveCommand<Unit, Unit> SaveCommand { get; set; }

        /// <summary>Команда удаления автосохранения</summary>
        public ReactiveCommand<Unit, Unit> DiscardCommand { get; set; }

        public RecoveryBannerViewModel()
        {
            SwitchVersionCommand = ReactiveCommand.Create(() => { });
            SaveCommand = ReactiveCommand.Create(() => { });
            DiscardCommand = ReactiveCommand.Create(() => { });

            // Обновляем текст при изменении IsViewingCache
            this.WhenAnyValue(x => x.IsViewingCache)
                .Subscribe(_ =>
                {
                    this.RaisePropertyChanged(nameof(VersionText));
                    this.RaisePropertyChanged(nameof(SwitchButtonText));
                });
        }
    }
}