using Newtonsoft.Json;
using ReactiveUI;
using System;
using System.Collections.Generic;

namespace Writersword.Core.Models.WorkModes
{
    /// <summary>
    /// Режим работы (WorkMode)
    /// </summary>
    public class WorkMode : ReactiveObject
    {
        private bool _isActive;

        [JsonProperty("Id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("WorkModeId")]
        public string WorkModeId { get; set; } = "Unknown";

        [JsonProperty("Title")]
        public string Title { get; set; } = "Unknown";

        [JsonProperty("Icon")]
        public string Icon { get; set; } = "❌";

        [JsonProperty("IsActive")]
        public bool IsActive
        {
            get => _isActive;
            set => this.RaiseAndSetIfChanged(ref _isActive, value);
        }

        [JsonProperty("Order")]
        public int Order { get; set; }

        [JsonProperty("IsCloseable")]
        public bool IsCloseable { get; set; } = true;

        [JsonProperty("ModuleSlots")]
        public List<ModuleSlot> ModuleSlots { get; set; } = new();

        [JsonProperty("Containers")]
        public List<SplitContainer> Containers { get; set; } = new();
    }
}