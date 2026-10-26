using System.Collections.Generic;
using Writersword.Modules.Characters.Models;

namespace Writersword.Modules.Characters.Interfaces
{
    public interface ICharacterService
    {
        IReadOnlyList<Character> GetAll();
        IReadOnlyList<Character> Search(string query);
        IReadOnlyList<Character> GetByTags(IEnumerable<string> tags);
        Character? GetById(string id);
        IReadOnlyList<string> GetAllTags();

        /// <summary>
        /// Реестр меток проекта — по одной записи на метку. Нужен, чтобы
        /// «Ранен» у разных персонажей был одной и той же меткой со своим
        /// значком и цветом, а не тремя похожими.
        /// </summary>
        IReadOnlyList<CharacterLabel> GetAllLabels();

        /// <summary>
        /// Записать метку в реестр проекта: по идентификатору, а при его
        /// отсутствии — по имени. Сохраняется образец, поэтому личные поля
        /// персонажа (порядок и показ на карточке) в реестр не уходят.
        /// </summary>
        void SaveGlobalLabel(CharacterLabel label);

        /// <summary>
        /// Разнести вид метки по всем персонажам, у которых она стоит.
        /// Меняются подпись, значок и цвета; порядок и признак показа на
        /// карточке у каждого персонажа остаются своими. Возвращает число
        /// затронутых персонажей.
        /// </summary>
        int ApplyLabelToAll(CharacterLabel label);

        Character Create(string name);
        /// <summary>
        /// Персонаж с подключёнными анкетами. depths — глубина анкет по
        /// умолчанию (из шаблона проекта); не заданные раскрываются по
        /// важности персонажа.
        /// </summary>
        Character CreateFromAnketas(string name, IEnumerable<CharacterAnketa> anketas, bool randomize = false,
            IReadOnlyDictionary<string, int>? depths = null);
        Character CreateCollective(string name, IEnumerable<CharacterAnketa>? anketas = null);
        void Update(Character character);
        void Delete(string id);
        Character Duplicate(string id);

        void ApplyAnketa(string characterId, CharacterAnketa anketa, bool randomize = false);

        /// <summary>
        /// Разнести изменения набора по карточкам, к которым он подключён:
        /// новые поля добавляются, уже заполненные значения не трогаются.
        /// Возвращает число изменённых карточек.
        ///
        /// Без этого правка своего набора не доезжала бы до персонажей:
        /// автор добавил поле и не увидел его там, где ждал.
        /// </summary>
        int SyncAnketa(CharacterAnketa anketa);

        /// <summary>
        /// Отключить набор от карточки. Значения полей при этом остаются:
        /// набор перестаёт числиться подключённым, но написанное автором
        /// не пропадает — удалять параметры можно поштучно и осознанно.
        /// </summary>
        void DetachAnketa(string characterId, string anketaId);

        /// <summary>
        /// Задать глубину подключённой анкеты у персонажа: 1 — кратко,
        /// 2 — подробно, 3 — глубоко. null — снова по важности персонажа.
        /// Скрытые уровнем поля значения не теряют.
        /// </summary>
        void SetAnketaDepth(string characterId, string anketaId, int? depth);

        /// <summary>
        /// Подключить к карточке все анкеты шаблона, которых на ней ещё нет.
        /// Уже подключённые остаются на своих местах, а заполненные значения
        /// не трогаются — шаблон добавляет разделы, а не переписывает работу.
        /// Возвращает число подключённых анкет.
        /// </summary>
        int ApplyTemplate(string characterId, CharacterTemplate template);

        /// <summary>
        /// Переставить подключённую анкету выше или ниже. Порядок анкет — это
        /// порядок разделов в карточке, и поля переставляются вслед за ними.
        /// </summary>
        void MoveAttachedAnketa(string characterId, string anketaId, int delta);
        void RandomizeParameters(string characterId);

        string? GetAvatarPath(string characterId);
        void SetAvatar(string characterId, byte[] data, string extension);
        void DeleteAvatar(string characterId);

        CharactersModuleData GetModuleData();
        void LoadModuleData(CharactersModuleData data);
        void SetContext(object? context);
        Character CreateWithId(Character character);

        /// <summary>
        /// Переписать ссылки на картинки после их переезда: ключ — прежняя
        /// ссылка, значение — новая. Возвращает число задетых персонажей.
        ///
        /// Зовётся, когда пак укладывают в проект или переносят между
        /// областями. Файл при этом цел, но лежит на новом месте, и персонаж,
        /// смотрящий по старому адресу, показывает пустой кружок.
        /// </summary>
        int RemapAvatarRefs(IReadOnlyDictionary<string, string> map);
    }

    public interface IRelationshipService
    {
        IReadOnlyList<CharacterRelationship> GetAll();
        IReadOnlyList<CharacterRelationship> GetAllForCharacter(string characterId);
        IReadOnlyList<CharacterRelationship> GetOutgoing(string characterId);
        IReadOnlyList<CharacterRelationship> GetIncoming(string characterId);
        CharacterRelationship? GetBetween(string sourceId, string targetId);

        CharacterRelationship Create(string sourceId, string targetId);
        void Update(CharacterRelationship relationship);
        void Delete(string id);

        void LoadRelationships(List<CharacterRelationship> relationships);
    }

    public interface ICharacterAnketaService
    {
        IReadOnlyList<CharacterAnketa> GetAll();
        IReadOnlyList<CharacterAnketa> GetBuiltIn();
        IReadOnlyList<CharacterAnketa> GetCustom();
        IReadOnlyList<CharacterAnketa> GetRecommended(IEnumerable<string> tags);
        CharacterAnketa? GetById(string id);

        CharacterAnketa Create(string name);
        void Update(CharacterAnketa anketa);
        void Delete(string id);
        CharacterAnketa Duplicate(string id);

        List<CharacterParameter> BuildParameters(CharacterAnketa anketa);
        List<CharacterParameter> BuildParametersRandomized(CharacterAnketa anketa);
        List<CharacterParameter> MergeParameters(IEnumerable<CharacterAnketa> anketas);
        List<CharacterParameter> MergeParametersRandomized(IEnumerable<CharacterAnketa> anketas);
        void RandomizeParameters(List<CharacterParameter> parameters);

        void LoadCustomAnketas(List<CharacterAnketa> anketas);

        // ── Недавние ──────────────────────────────────────────────────────

        IReadOnlyList<CharacterAnketa> GetRecentAnketas();
        void RememberAnketa(string anketaId);
        void ClearRecentAnketas();
        IReadOnlyList<string> GetRecentAnketaIds();
        void LoadRecentAnketas(List<string> ids);

        // ── Шаблоны ───────────────────────────────────────────────────────
        //
        // Шаблон — список анкет под тип персонажа. Живёт здесь же, а не в
        // своём сервисе: шаблон без анкет пуст по определению, и разводить
        // их по разным хозяевам значило бы держать связь между двумя
        // хранилищами вручную.

        IReadOnlyList<CharacterTemplate> GetTemplates();
        CharacterTemplate? GetTemplateById(string id);

        CharacterTemplate CreateTemplate(string name);
        void UpdateTemplate(CharacterTemplate template);
        void DeleteTemplate(string id);

        /// <summary>
        /// Переставить свой шаблон на место index среди своих шаблонов.
        /// Встроенные стоят отдельно и не двигаются.
        /// </summary>
        void MoveTemplate(string id, int index);

        void LoadTemplates(List<CharacterTemplate> templates);

        /// <summary>Пользовательские шаблоны — то, что уходит в файл проекта.</summary>
        IReadOnlyList<CharacterTemplate> GetCustomTemplates();

        /// <summary>
        /// Разложить свои шаблоны: порядок списка — новый порядок своих
        /// шаблонов, у каждого — его папка (пусто — без папки). Свои шаблоны,
        /// которых в списке нет, остаются следом в прежнем порядке.
        /// </summary>
        void ArrangeTemplates(IReadOnlyList<(string Id, string FolderId)> order);

        // ── Папки шаблонов ────────────────────────────────────────────────

        IReadOnlyList<CharacterTemplateFolder> GetTemplateFolders();
        CharacterTemplateFolder CreateTemplateFolder(string name);
        void UpdateTemplateFolder(CharacterTemplateFolder folder);

        /// <summary>Удалить папку; её шаблоны уходят в конец «Моих шаблонов».</summary>
        void DeleteTemplateFolder(string id);

        /// <summary>
        /// Сдвинуть раздел вкладки шаблонов на shift мест: −1 — выше, +1 — ниже.
        /// Двигаются и свои папки, и «Мои шаблоны», и «Встроенные» (ключи
        /// TemplateSections).
        /// </summary>
        void MoveTemplateFolder(string id, int shift);

        void LoadTemplateFolders(List<CharacterTemplateFolder> folders);

        /// <summary>
        /// Порядок разделов вкладки шаблонов сверху вниз: ключи своих папок,
        /// TemplateSections.Ungrouped и TemplateSections.BuiltIn. В нём всегда
        /// все разделы — недостающие встают на места по умолчанию.
        /// </summary>
        IReadOnlyList<string> GetTemplateSectionOrder();

        /// <summary>Порядок разделов из проекта; вызывается после LoadTemplateFolders.</summary>
        void LoadTemplateSectionOrder(List<string> ids);

        // ── Заготовки полей ───────────────────────────────────────────────

        /// <summary>Все заготовки полей: сначала свои, за ними встроенные.</summary>
        IReadOnlyList<CharacterFieldPreset> GetFieldPresets();

        /// <summary>Свои заготовки — то, что уходит в файл проекта.</summary>
        IReadOnlyList<CharacterFieldPreset> GetCustomFieldPresets();

        /// <summary>
        /// Сохранить поле своей заготовкой под его названием. Своя заготовка
        /// с тем же названием и типом заменяется. null — в режиме только чтения.
        /// </summary>
        CharacterFieldPreset? SaveFieldPreset(CharacterAnketaField field);

        /// <summary>Удалить свою заготовку; встроенные не удаляются.</summary>
        void DeleteFieldPreset(string id);

        void LoadFieldPresets(List<CharacterFieldPreset> presets);

        // ── Скрытые шаблоны и свёрнутые разделы ───────────────────────────

        bool IsTemplateHidden(string id);
        void SetTemplateHidden(string id, bool hidden);
        IReadOnlyCollection<string> GetHiddenTemplateIds();
        void LoadHiddenTemplateIds(List<string> ids);

        bool IsTemplateFolderCollapsed(string id);
        void SetTemplateFolderCollapsed(string id, bool collapsed);
        IReadOnlyCollection<string> GetCollapsedTemplateFolderIds();
        void LoadCollapsedTemplateFolderIds(List<string> ids);
    }
}
