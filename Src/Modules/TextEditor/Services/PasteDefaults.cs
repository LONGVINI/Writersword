using System;
using Serilog;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Services;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>Что делает обычная вставка (Ctrl+V): с исходным оформлением или только текст.</summary>
    public sealed class PasteDefaultsData
    {
        public bool TextOnly { get; set; }
    }

    /// <summary>
    /// Вставка по умолчанию, как «Параметры вставки по умолчанию» в Word. Выбирается в
    /// меню правой кнопки и хранится в настройках программы, общих для всех проектов.
    ///
    /// «Только текст» снимает оформление источника; картинка из буфера, у которой
    /// текста нет, всё равно вставляется картинкой — иначе Ctrl+V с ней не делал бы
    /// ничего. Строго один текст даёт пункт меню «Вставить только текст».
    /// </summary>
    public static class PasteDefaults
    {
        private const string SettingsKey = "PasteDefaults";
        private static readonly ILogger _log = Log.ForContext(typeof(PasteDefaults));
        private static bool? _textOnly;

        public static bool TextOnly
        {
            get
            {
                if (_textOnly is { } known) return known;
                try
                {
                    _textOnly = CoreServices.GetService<ISettingsService>()?.GetModuleSettings<PasteDefaultsData>(SettingsKey)?.TextOnly ?? false;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[PASTE] Вставка по умолчанию не прочиталась");
                    _textOnly = false;
                }
                return _textOnly.Value;
            }
            set
            {
                _textOnly = value;
                var settings = CoreServices.GetService<ISettingsService>();
                if (settings is null) return;
                try
                {
                    settings.SaveModuleSettings(SettingsKey, new PasteDefaultsData { TextOnly = value });
                    settings.Save();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[PASTE] Вставка по умолчанию не сохранилась");
                }
            }
        }
    }
}
