using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Serilog;
using Writersword.Core.Models.Preview;
using Writersword.Core.Models.Project;

namespace Writersword.Core.Services
{
    /// <summary>
    /// Чтение снимков сущностей прямо из файла проекта, без модуля-владельца.
    ///
    /// Нужен для случая, когда в сборке нет модуля, чьи данные лежат в проекте:
    /// персонажи есть, модуля Characters нет. Ссылка из текста остаётся живой,
    /// карточку по ней есть чем показать, но менять данные нечем — и это ровно
    /// то поведение, которое требуется: чужой блок доступен на чтение и никогда
    /// не удаляется.
    ///
    /// Опознание идёт по соглашению: блок модуля содержит массив Preview.
    /// Реестра видов сущностей для этого не требуется — читатель не обязан
    /// знать ни одного модуля в лицо.
    /// </summary>
    public static class EntityPreviewReader
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(EntityPreviewReader));

        /// <summary>Снимки из блока конкретного модуля.</summary>
        public static IReadOnlyList<EntityPreview> Read(ProjectFile project, string moduleType)
        {
            if (project?.ModulesData == null) return Array.Empty<EntityPreview>();
            if (!project.ModulesData.TryGetValue(moduleType, out var block)) return Array.Empty<EntityPreview>();
            return ReadBlock(block, moduleType);
        }

        /// <summary>
        /// Снимки из всех блоков проекта: словарь «тип модуля — его сущности».
        /// Блоки без Preview пропускаются молча — модуль просто не отдаёт
        /// снимков, это не ошибка.
        /// </summary>
        public static Dictionary<string, IReadOnlyList<EntityPreview>> ReadAll(ProjectFile project)
        {
            var result = new Dictionary<string, IReadOnlyList<EntityPreview>>();
            if (project?.ModulesData == null) return result;

            foreach (var kvp in project.ModulesData)
            {
                var previews = ReadBlock(kvp.Value, kvp.Key);
                if (previews.Count > 0) result[kvp.Key] = previews;
            }

            return result;
        }

        private static IReadOnlyList<EntityPreview> ReadBlock(object? block, string moduleType)
        {
            if (block == null) return Array.Empty<EntityPreview>();

            try
            {
                // Блок хранится непрозрачно: после десериализации проекта это
                // JObject, но модуль мог отдать и готовый объект — тогда его
                // сначала приводим к токену.
                var token = block as JToken ?? JToken.FromObject(block);
                if (token.Type != JTokenType.Object) return Array.Empty<EntityPreview>();

                var previewToken = token["Preview"];
                if (previewToken == null || previewToken.Type != JTokenType.Array)
                    return Array.Empty<EntityPreview>();

                var list = previewToken.ToObject<List<EntityPreview>>();
                return list ?? (IReadOnlyList<EntityPreview>)Array.Empty<EntityPreview>();
            }
            catch (Exception ex)
            {
                // Чужой блок читаем на свой страх: непонятная структура не
                // должна ронять того, кто просто хотел показать превью.
                _logger.Debug(ex, "Preview read failed for module {Module}", moduleType);
                return Array.Empty<EntityPreview>();
            }
        }
    }
}
