using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Уборка картинок, потерявших свою страницу.
    ///
    /// Под удаление попадают только два случая, и оба означают, что картинка ничего
    /// не делает и не печатается:
    ///   • она ЦЕЛИКОМ за пределами своего листа — в редакторе такая помечена бледной
    ///     заливкой с красной штриховкой;
    ///   • она закреплена за страницей и при этом выходит на другую — закрепление
    ///     обещает, что на соседние страницы картинка не влияет, поэтому её часть там
    ///     не имеет смысла.
    /// Частичный свес за край листа — нормальная вёрстка (поля, обрез, полукруг за
    /// верхним краем) и не трогается.
    ///
    /// Запускать это можно ТОЛЬКО при штатном закрытии приложения. Кеш восстановления —
    /// страховка от падения: после аварийного завершения картинки обязаны остаться на
    /// месте, иначе восстановленный документ придёт без них.
    ///
    /// Геометрия считается по модели, без раскладки: смещения плавающей картинки и так
    /// заданы относительно текстовой области своей страницы.
    /// </summary>
    public static class OffPageImageCleanup
    {
        /// <summary>
        /// Убирает из документа картинки, потерявшие страницу. Возвращает число удалённых.
        /// Файлы картинок не трогает — их подберёт обычная уборка неиспользуемых файлов,
        /// когда на них не останется ссылок.
        /// </summary>
        public static int Purge(DocumentModel? document)
        {
            if (document is null) return 0;

            var settings = document.PageSettings;
            if (settings is null) return 0;

            float pageWidthPt = MmToPt(settings.GetPhysicalWidthMm());
            float pageHeightPt = MmToPt(settings.GetPhysicalHeightMm());
            if (pageWidthPt <= 1f || pageHeightPt <= 1f) return 0;

            // Смещения отсчитываются от текстовой области, границы — от края бумаги.
            float marginLeftPt = MmToPt(settings.MarginLeftMm + settings.MarginGutterMm);
            float marginTopPt = MmToPt(settings.MarginTopMm);

            int removed = 0;
            foreach (var section in document.Sections)
            {
                removed += PurgeList(section.Blocks, pageWidthPt, pageHeightPt, marginLeftPt, marginTopPt);
                removed += PurgeList(section.FloatingObjects, pageWidthPt, pageHeightPt, marginLeftPt, marginTopPt);
            }

            return removed;
        }

        private static int PurgeList(
            List<BlockModel> blocks,
            float pageWidthPt, float pageHeightPt,
            float marginLeftPt, float marginTopPt)
        {
            int removed = 0;

            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i] is not ImageBlock image) continue;

                // Картинка в строке — обычный символ абзаца, страницы у неё нет.
                if (image.WrapMode == WrapMode.Inline) continue;

                if (!IsLost(image, pageWidthPt, pageHeightPt, marginLeftPt, marginTopPt)) continue;

                blocks.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        /// <summary>Потеряла ли картинка свою страницу.</summary>
        private static bool IsLost(
            ImageBlock image,
            float pageWidthPt, float pageHeightPt,
            float marginLeftPt, float marginTopPt)
        {
            // Габарит повёрнутой картинки — описанный прямоугольник вокруг её центра:
            // та же геометрия, по которой считается обтекание и пометка в редакторе.
            double rad = image.RotationDeg * Math.PI / 180.0;
            float absCos = (float)Math.Abs(Math.Cos(rad));
            float absSin = (float)Math.Abs(Math.Sin(rad));

            float w = (float)image.WidthPt;
            float h = (float)image.HeightPt;
            if (w <= 0f || h <= 0f) return false;

            float boxW = w * absCos + h * absSin;
            float boxH = w * absSin + h * absCos;

            float centerX = marginLeftPt + (float)image.OffsetXPt + w / 2f;
            float centerY = marginTopPt + (float)image.OffsetYPt + h / 2f;

            float left = centerX - boxW / 2f;
            float right = centerX + boxW / 2f;
            float top = centerY - boxH / 2f;
            float bottom = centerY + boxH / 2f;

            // Целиком мимо листа.
            if (right <= 0f || left >= pageWidthPt || bottom <= 0f || top >= pageHeightPt)
                return true;

            // Закреплённая, выходящая на соседнюю страницу. Страницы идут столбиком,
            // поэтому на другой лист картинка попадает выходом за верх или за низ;
            // выход вбок — это поля и обрез, они законны.
            if (image.PinnedPage > 0 && (top < 0f || bottom > pageHeightPt))
                return true;

            return false;
        }

        private static float MmToPt(double mm) => (float)(mm * 72.0 / 25.4);
    }
}
