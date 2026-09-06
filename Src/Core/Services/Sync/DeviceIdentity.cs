using Newtonsoft.Json;
using Serilog;
using System;
using System.IO;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Как это устройство зовут в хранилище.
    ///
    /// Опознаватель выдаётся один раз и живёт вместе с настройками: по нему
    /// устройство узнаёт среди отметок свою и отличает её от чужих. Имя нужно
    /// человеку — «книга открыта на Телефоне» понятнее, чем набор знаков.
    ///
    /// Опознаватель случайный, а не выведенный из железа или имени машины.
    /// Выведенный означал бы, что владелец сервера, глядя на отметки, узнаёт
    /// что-то о машинах владельца книг, — а он не должен узнавать ничего.
    /// </summary>
    public static class DeviceIdentity
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(DeviceIdentity));
        private static readonly object _lock = new();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "device.json");

        private sealed class State
        {
            [JsonProperty("id")] public string Id { get; set; } = string.Empty;
            [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        }

        private static State? _state;

        public static string Id
        {
            get { lock (_lock) { EnsureLoaded(); return _state!.Id; } }
        }

        /// <summary>
        /// Род устройства. Не хранится и не настраивается: телефон не станет
        /// компьютером от смены настроек, а от рода зависит право править книгу.
        /// </summary>
        public static string Kind
            => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
                ? Writersword.Core.Models.Sync.DevicePresence.KindMobile
                : Writersword.Core.Models.Sync.DevicePresence.KindDesktop;

        /// <summary>
        /// Имя устройства. Меняется человеком — он один знает, где какая машина.
        /// </summary>
        public static string Name
        {
            get { lock (_lock) { EnsureLoaded(); return _state!.Name; } }
            set
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    _state!.Name = string.IsNullOrWhiteSpace(value) ? DefaultName() : value.Trim();
                    Save();
                }
            }
        }

        private static void EnsureLoaded()
        {
            if (_state is not null) return;

            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonConvert.DeserializeObject<State>(json);

                    if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.Id))
                    {
                        if (string.IsNullOrWhiteSpace(loaded.Name))
                            loaded.Name = DefaultName();

                        _state = loaded;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to read device identity from {Path}", FilePath);
            }

            _state = new State { Id = Guid.NewGuid().ToString("N"), Name = DefaultName() };
            Save();
        }

        private static void Save()
        {
            try
            {
                var folder = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to write device identity to {Path}", FilePath);
            }
        }

        /// <summary>
        /// Имя по умолчанию — сетевое имя машины, а на телефоне его нет, и там
        /// остаётся просто «Устройство». Переименовать может человек.
        /// </summary>
        private static string DefaultName()
        {
            try
            {
                var name = Environment.MachineName;
                return string.IsNullOrWhiteSpace(name) ? "Устройство" : name;
            }
            catch
            {
                return "Устройство";
            }
        }
    }
}
