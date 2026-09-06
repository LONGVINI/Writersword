using Newtonsoft.Json;
using System;

namespace Writersword.Core.Models.Sync
{
    /// <summary>
    /// Отметка о том, что книга сейчас у кого-то открыта.
    ///
    /// Синхронизация умеет не сводить расхождение молча: если обе стороны
    /// правили один текст, выбор между ними принадлежит автору. Но лучше до
    /// расхождения не доводить вовсе — а для этого устройства должны знать друг
    /// о друге заранее, а не выяснять постфактум, что работали одновременно.
    ///
    /// Отметка обновляется, пока книга открыта, и протухает сама: устройство
    /// может умереть, не убрав её за собой, и вечная отметка запирала бы книгу
    /// навсегда. Поэтому она не запрет, а осведомление — программа предупреждает,
    /// а решает человек.
    /// </summary>
    public sealed class DevicePresence
    {
        /// <summary>Опознаватель устройства. Живёт вместе с настройками.</summary>
        [JsonProperty("deviceId")]
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>Имя, которое видит человек: «Ноутбук», «Телефон».</summary>
        [JsonProperty("deviceName")]
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>Книгу правят, а не просто читают.</summary>
        [JsonProperty("editing")]
        public bool Editing { get; set; }

        /// <summary>
        /// Род устройства: <see cref="KindDesktop"/> или <see cref="KindMobile"/>.
        ///
        /// Нужен для правила старшинства: книга принадлежит компьютеру, и пока он
        /// её держит, телефон её не правит. Без рода отметки неразличимы, и телефон
        /// запирал бы книгу от другого телефона наравне с компьютером.
        /// </summary>
        [JsonProperty("kind")]
        public string Kind { get; set; } = string.Empty;

        public const string KindDesktop = "desktop";
        public const string KindMobile = "mobile";

        /// <summary>
        /// Отметка оставлена компьютером.
        ///
        /// Пустой род считается компьютерным: так отвечают отметки, записанные до
        /// появления поля, и ошибка в эту сторону безопасна — телефон лишний раз
        /// не станет править, тогда как обратная ошибка дала бы правку поверх
        /// работы за компьютером.
        /// </summary>
        [JsonIgnore]
        public bool IsDesktop
            => !string.Equals(Kind, KindMobile, StringComparison.OrdinalIgnoreCase);

        [JsonProperty("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>
        /// Сколько отметка считается свежей.
        ///
        /// Втрое дольше самого частого обновления: пропущенный по сети заход не
        /// должен объявлять живое устройство мёртвым, а мёртвое не должно
        /// держать книгу дольше нескольких минут.
        /// </summary>
        public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(6);

        [JsonIgnore]
        public bool IsFresh => DateTimeOffset.UtcNow - UpdatedAt < Freshness;

        public override string ToString()
            => $"{DeviceName} ({(Editing ? "правит" : "читает")})";
    }

    /// <summary>
    /// Книга открыта ещё на одном устройстве.
    /// </summary>
    public sealed class ForeignPresenceEventArgs : EventArgs
    {
        /// <summary>Путь к книге на этом устройстве.</summary>
        public required string LocalPath { get; init; }

        /// <summary>Кто ещё её держит.</summary>
        public required DevicePresence Other { get; init; }
    }

    /// <summary>
    /// Кто ещё держит эту книгу открытой.
    /// </summary>
    public sealed class PresenceReport
    {
        /// <summary>Свежая отметка другого устройства или null.</summary>
        public DevicePresence? Other { get; init; }

        /// <summary>
        /// Свежая отметка компьютера, если книга открыта на нём. Отдельно от
        /// <see cref="Other"/>: там выбирается самая важная отметка вообще, а
        /// правило старшинства спрашивает именно про компьютер.
        /// </summary>
        public DevicePresence? Desktop { get; init; }

        /// <summary>Другое устройство правит книгу прямо сейчас.</summary>
        public bool ForeignEditing => Other?.Editing == true;

        /// <summary>Книга занята компьютером: телефон её не правит.</summary>
        public bool DesktopHolds => Desktop is not null;
    }
}
