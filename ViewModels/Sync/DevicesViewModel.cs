using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using Writersword.Core.Interfaces.WorkFlows;
using Writersword.Core.Services.Sync;

namespace Writersword.ViewModels.Sync
{
    /// <summary>
    /// Кто сейчас работает с книгами — здесь и на других устройствах.
    ///
    /// Окно смотрит на снимок, который ведёт координатор, и ничего не спрашивает
    /// у сервера само. Спрашивает координатор, раз в пятнадцать секунд, с
    /// условием по версии: неизменившийся файл отметок сервер не отдаёт вовсе.
    /// Заведи окно свой опрос — походов стало бы вдвое больше ради тех же
    /// сведений, и они бы ещё и расходились между собой.
    ///
    /// Постоянного соединения здесь нет и быть не может: хранилище — обычный
    /// файловый сервер, он умеет отдать файл и принять файл, но не умеет сказать
    /// первым. Появится свой хост — за тем же снимком встанет сокет, и это окно
    /// не изменится ни строкой.
    /// </summary>
    public sealed class DevicesViewModel : ReactiveObject, IDisposable
    {
        /// <summary>
        /// Как часто перерисовывать список.
        ///
        /// Чаще, чем ходит опрос: снимок обновляется не по часам, а по ответу
        /// сервера, и показывать его с запаздыванием в целый опрос незачем.
        /// </summary>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

        private readonly SyncCoordinator _coordinator;
        private readonly ITabCollection _tabs;
        private readonly DispatcherTimer _timer;

        private string _deviceName = string.Empty;

        public DevicesViewModel()
            : this(App.Services.GetRequiredService<SyncCoordinator>(),
                   App.Services.GetRequiredService<ITabCollection>())
        {
        }

        public DevicesViewModel(SyncCoordinator coordinator, ITabCollection tabs)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));

            _deviceName = DeviceIdentity.Name;

            RenameCommand = ReactiveCommand.Create(Rename);

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = RefreshInterval
            };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();

            Refresh();
        }

        /// <summary>Строка списка: книга и то, кто её ещё держит.</summary>
        public sealed class Row
        {
            public required string Title { get; init; }
            public required string Holder { get; init; }
            public required bool IsForeign { get; init; }
        }

        public ObservableCollection<Row> Rows { get; } = new();

        /// <summary>Имя этого устройства. Его видят на другой стороне.</summary>
        public string DeviceName
        {
            get => _deviceName;
            set => this.RaiseAndSetIfChanged(ref _deviceName, value);
        }

        /// <summary>
        /// Опознаватель показывается укороченным: полный не нужен человеку, а
        /// узнать своё устройство среди прочих помогает и половина.
        /// </summary>
        public string DeviceIdShort => DeviceIdentity.Id.Length > 8
            ? DeviceIdentity.Id[..8]
            : DeviceIdentity.Id;

        public bool IsRunning => _coordinator.IsRunning;

        public string StatusText => _coordinator.IsRunning
            ? "Синхронизация работает: устройства видят друг друга."
            : "Синхронизация не настроена — Инструменты, Синхронизация…";

        public ReactiveCommand<Unit, Unit> RenameCommand { get; }

        private void Rename()
        {
            DeviceIdentity.Name = DeviceName;
            DeviceName = DeviceIdentity.Name;
        }

        private void Refresh()
        {
            this.RaisePropertyChanged(nameof(IsRunning));
            this.RaisePropertyChanged(nameof(StatusText));

            var rows = new List<Row>();

            foreach (var tab in _tabs.Tabs)
            {
                var path = tab.FilePath;
                if (string.IsNullOrWhiteSpace(path)) continue;

                var other = _coordinator.ForeignOn(path);

                rows.Add(new Row
                {
                    Title = Path.GetFileNameWithoutExtension(path),
                    IsForeign = other is not null,
                    Holder = other is null
                        ? "Только здесь"
                        : other.Editing
                            ? $"Правят на «{other.DeviceName}»"
                            : $"Читают на «{other.DeviceName}»"
                });
            }

            // Список перестраивается целиком, а не правится по месту: книг у
            // человека единицы, а выискивать, что именно изменилось, ради трёх
            // строк — работа дороже результата.
            if (SameAsShown(rows)) return;

            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
        }

        private bool SameAsShown(IReadOnlyList<Row> rows)
        {
            if (rows.Count != Rows.Count) return false;

            for (int i = 0; i < rows.Count; i++)
            {
                if (!string.Equals(rows[i].Title, Rows[i].Title, StringComparison.Ordinal)) return false;
                if (!string.Equals(rows[i].Holder, Rows[i].Holder, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        public void Dispose() => _timer.Stop();
    }
}
