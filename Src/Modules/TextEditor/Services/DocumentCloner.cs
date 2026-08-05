using System;
using System.Collections.Generic;
using Writersword.Modules.TextEditor.Models.Document;
using Writersword.Modules.TextEditor.Models.Inline;
using Writersword.Modules.TextEditor.Models.Page;
using Writersword.Modules.TextEditor.Models.Styles;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Глубокое клонирование модели документа для снимков состояния.
    /// В отличие от RunModel.Clone() (генерирует новые Id для операций редактирования),
    /// здесь все Id сохраняются в точности — клон сериализуется в тот же JSON,
    /// что и оригинал, и остаётся совместимым с дельта-кешем и аннотациями.
    /// Клонирование выполняется на UI-потоке (быстрое копирование объектов в памяти),
    /// после чего клон можно сериализовать на любом потоке без гонок с живой моделью.
    /// </summary>
    public static class DocumentCloner
    {
        /// <summary>Создаёт глубокую копию документа с сохранением всех Id.</summary>
        public static DocumentModel Clone(DocumentModel source)
        {
            var clone = new DocumentModel
            {
                Id = source.Id,
                Title = source.Title,
                SchemaVersion = source.SchemaVersion,
                AuthorId = source.AuthorId,
                RevisionId = source.RevisionId,
                LastSyncedAt = source.LastSyncedAt,
                PageSettings = ClonePageSettings(source.PageSettings),
                ColumnSettings = CloneColumnSettings(source.ColumnSettings),
                CanvasSettings = CloneCanvasSettings(source.CanvasSettings),
                ViewMode = source.ViewMode,
                Zoom = source.Zoom,
                Styles = new List<DocumentStyle>(source.Styles.Count),
                Sections = new List<SectionModel>(source.Sections.Count),
                Annotations = new List<InlineAnnotation>(source.Annotations.Count),
                DocumentAutoReplaceRules = CloneAutoReplaceRules(source.DocumentAutoReplaceRules)
            };

            foreach (var style in source.Styles)
                clone.Styles.Add(CloneStyle(style));

            foreach (var section in source.Sections)
                clone.Sections.Add(CloneSection(section));

            foreach (var annotation in source.Annotations)
                clone.Annotations.Add(CloneAnnotation(annotation));

            return clone;
        }

        private static TextEditorPageSettings ClonePageSettings(TextEditorPageSettings source)
        {
            return new TextEditorPageSettings
            {
                PaperSize = source.PaperSize,
                WidthMm = source.WidthMm,
                HeightMm = source.HeightMm,
                Orientation = source.Orientation,
                MarginTopMm = source.MarginTopMm,
                MarginBottomMm = source.MarginBottomMm,
                MarginLeftMm = source.MarginLeftMm,
                MarginRightMm = source.MarginRightMm,
                MarginGutterMm = source.MarginGutterMm,
                HeaderDistanceMm = source.HeaderDistanceMm,
                FooterDistanceMm = source.FooterDistanceMm
            };
        }

        private static ColumnSettings CloneColumnSettings(ColumnSettings source)
        {
            return new ColumnSettings
            {
                ColumnCount = source.ColumnCount,
                GapMm = source.GapMm,
                ShowSeparator = source.ShowSeparator
            };
        }

        private static CanvasSettings CloneCanvasSettings(CanvasSettings source)
        {
            return new CanvasSettings
            {
                Preset = source.Preset,
                PageBackgroundColor = source.PageBackgroundColor,
                DefaultTextColor = source.DefaultTextColor
            };
        }

        private static List<AutoReplaceRule>? CloneAutoReplaceRules(List<AutoReplaceRule>? source)
        {
            if (source is null) return null;

            var clone = new List<AutoReplaceRule>(source.Count);
            foreach (var rule in source)
            {
                clone.Add(new AutoReplaceRule
                {
                    From = rule.From,
                    To = rule.To,
                    IsEnabled = rule.IsEnabled,
                    IsBuiltIn = rule.IsBuiltIn
                });
            }
            return clone;
        }

        private static DocumentStyle CloneStyle(DocumentStyle source)
        {
            return new DocumentStyle
            {
                Name = source.Name,
                DisplayName = source.DisplayName,
                StyleType = source.StyleType,
                IsBuiltIn = source.IsBuiltIn,
                BasedOn = source.BasedOn,
                ParagraphProperties = source.ParagraphProperties?.Clone(),
                RunProperties = source.RunProperties?.Clone(),
                SortOrder = source.SortOrder
            };
        }

        private static SectionModel CloneSection(SectionModel source)
        {
            var clone = new SectionModel
            {
                Id = source.Id,
                Hash = source.Hash,
                PageSettings = source.PageSettings is null ? null : ClonePageSettings(source.PageSettings),
                ColumnSettings = source.ColumnSettings is null ? null : CloneColumnSettings(source.ColumnSettings),
                Header = CloneHeaderFooter(source.Header),
                Footer = CloneHeaderFooter(source.Footer),
                Blocks = new List<BlockModel>(source.Blocks.Count),
                FloatingObjects = new List<BlockModel>(source.FloatingObjects.Count),
                InlineObjects = new List<BlockModel>(source.InlineObjects.Count)
            };

            foreach (var block in source.Blocks)
                clone.Blocks.Add(CloneBlock(block));

            foreach (var block in source.FloatingObjects)
                clone.FloatingObjects.Add(CloneBlock(block));

            // Объекты в строке — такая же часть документа: без них снимок потеряет
            // встроенные картинки ровно так же, как раньше терялся поворот.
            foreach (var block in source.InlineObjects)
                clone.InlineObjects.Add(CloneBlock(block));

            return clone;
        }

        private static HeaderFooterModel CloneHeaderFooter(HeaderFooterModel source)
        {
            var clone = new HeaderFooterModel
            {
                IsEnabled = source.IsEnabled,
                DifferentFirstPage = source.DifferentFirstPage,
                Paragraphs = new List<ParagraphBlock>(source.Paragraphs.Count)
            };

            foreach (var paragraph in source.Paragraphs)
                clone.Paragraphs.Add(CloneParagraph(paragraph));

            return clone;
        }

        private static BlockModel CloneBlock(BlockModel source)
        {
            switch (source)
            {
                case ParagraphBlock paragraph:
                    return CloneParagraph(paragraph);
                case TableBlock table:
                    return CloneTable(table);
                case ImageBlock image:
                    return CloneImage(image);
                case ShapeBlock shape:
                    return CloneShape(shape);
                case FloatingTextBlock floatingText:
                    return CloneFloatingText(floatingText);
                case BreakBlock breakBlock:
                    return new BreakBlock
                    {
                        Id = breakBlock.Id,
                        Hash = breakBlock.Hash,
                        BreakType = breakBlock.BreakType
                    };
                default:
                    throw new NotSupportedException(
                        $"DocumentCloner: unknown block type {source.GetType().FullName}");
            }
        }

        private static ParagraphBlock CloneParagraph(ParagraphBlock source)
        {
            var clone = new ParagraphBlock
            {
                Id = source.Id,
                Hash = source.Hash,
                Chunks = new List<TextChunk>(source.Chunks.Count),
                Properties = source.Properties.Clone(),
                ListProperties = source.ListProperties?.Clone()
            };

            foreach (var chunk in source.Chunks)
                clone.Chunks.Add(CloneChunk(chunk));

            return clone;
        }

        private static TextChunk CloneChunk(TextChunk source)
        {
            var clone = new TextChunk
            {
                Id = source.Id,
                Hash = source.Hash,
                Runs = new List<RunModel>(source.Runs.Count)
            };

            foreach (var run in source.Runs)
                clone.Runs.Add(CloneRun(run));

            return clone;
        }

        private static RunModel CloneRun(RunModel source)
        {
            // Не используется RunModel.Clone(): он генерирует новый Id,
            // а для снимка Id должен совпадать с оригиналом.
            return new RunModel
            {
                Id = source.Id,
                Text = source.Text,
                Properties = source.Properties?.Clone()
            };
        }

        private static TableBlock CloneTable(TableBlock source)
        {
            var clone = new TableBlock
            {
                Id = source.Id,
                Hash = source.Hash,
                RowCount = source.RowCount,
                ColumnCount = source.ColumnCount,
                Columns = new List<TableColumnDefinition>(source.Columns.Count),
                Cells = new List<TableCell>(source.Cells.Count),
                StyleName = source.StyleName,
                WidthPercent = source.WidthPercent,
                LeftIndentPt = source.LeftIndentPt,
                RepeatHeader = source.RepeatHeader,
                SplitMode = source.SplitMode,
                BreakLabel = source.BreakLabel,
                ContinuationLabel = source.ContinuationLabel
            };

            foreach (var column in source.Columns)
            {
                clone.Columns.Add(new TableColumnDefinition
                {
                    WidthType = column.WidthType,
                    WidthValue = column.WidthValue
                });
            }

            foreach (var cell in source.Cells)
                clone.Cells.Add(CloneCell(cell));

            return clone;
        }

        private static TableCell CloneCell(TableCell source)
        {
            var clone = new TableCell
            {
                Id = source.Id,
                Paragraphs = new List<ParagraphBlock>(source.Paragraphs.Count),
                Row = source.Row,
                Column = source.Column,
                RowSpan = source.RowSpan,
                ColSpan = source.ColSpan,
                BackgroundColor = source.BackgroundColor,
                Borders = source.Borders.Clone(),
                VerticalAlignment = source.VerticalAlignment,
                PaddingTopPt = source.PaddingTopPt,
                PaddingBottomPt = source.PaddingBottomPt,
                PaddingLeftPt = source.PaddingLeftPt,
                PaddingRightPt = source.PaddingRightPt
            };

            foreach (var paragraph in source.Paragraphs)
                clone.Paragraphs.Add(CloneParagraph(paragraph));

            return clone;
        }

        private static ImageBlock CloneImage(ImageBlock source)
        {
            // Копируются ВСЕ свойства картинки. Снимок документа строится именно из
            // этого клона, поэтому забытое здесь поле не попадает ни в кеш, ни в файл:
            // правка выглядит применённой на экране и исчезает после перезапуска.
            // Так терялись поворот, прозрачность, рамка, отражение, обрезка и отступы
            // обтекания — в клоне они оставались значениями по умолчанию.
            return new ImageBlock
            {
                Id = source.Id,
                Hash = source.Hash,
                ImageFileName = source.ImageFileName,
                WidthPt = source.WidthPt,
                HeightPt = source.HeightPt,
                LockAspectRatio = source.LockAspectRatio,
                RotationDeg = source.RotationDeg,
                Opacity = source.Opacity,
                BorderColor = source.BorderColor,
                BorderThicknessPt = source.BorderThicknessPt,
                FlipHorizontal = source.FlipHorizontal,
                FlipVertical = source.FlipVertical,
                CropLeftFrac = source.CropLeftFrac,
                CropTopFrac = source.CropTopFrac,
                CropRightFrac = source.CropRightFrac,
                CropBottomFrac = source.CropBottomFrac,
                WrapMode = source.WrapMode,
                WrapSide = source.WrapSide,
                PinnedPage = source.PinnedPage,
                WrapPadTopPt = source.WrapPadTopPt,
                WrapPadBottomPt = source.WrapPadBottomPt,
                WrapPadLeftPt = source.WrapPadLeftPt,
                WrapPadRightPt = source.WrapPadRightPt,
                Alignment = source.Alignment,
                Anchor = source.Anchor,
                OffsetXPt = source.OffsetXPt,
                OffsetYPt = source.OffsetYPt,
                ZOrder = source.ZOrder,
                AltText = source.AltText
            };
        }

        private static ShapeBlock CloneShape(ShapeBlock source)
        {
            return new ShapeBlock
            {
                Id = source.Id,
                Hash = source.Hash,
                ShapeType = source.ShapeType,
                XPt = source.XPt,
                YPt = source.YPt,
                WidthPt = source.WidthPt,
                HeightPt = source.HeightPt,
                FillColor = source.FillColor,
                StrokeColor = source.StrokeColor,
                StrokeThicknessPt = source.StrokeThicknessPt,
                Anchor = source.Anchor,
                ZOrder = source.ZOrder,
                InnerText = source.InnerText,
                IsGrouped = source.IsGrouped,
                GroupId = source.GroupId
            };
        }

        private static FloatingTextBlock CloneFloatingText(FloatingTextBlock source)
        {
            var clone = new FloatingTextBlock
            {
                Id = source.Id,
                Hash = source.Hash,
                XPt = source.XPt,
                YPt = source.YPt,
                WidthPt = source.WidthPt,
                HeightPt = source.HeightPt,
                BackgroundColor = source.BackgroundColor,
                BorderColor = source.BorderColor,
                BorderThicknessPt = source.BorderThicknessPt,
                Anchor = source.Anchor,
                ZOrder = source.ZOrder,
                Paragraphs = new List<ParagraphBlock>(source.Paragraphs.Count),
                IsGrouped = source.IsGrouped,
                GroupId = source.GroupId
            };

            foreach (var paragraph in source.Paragraphs)
                clone.Paragraphs.Add(CloneParagraph(paragraph));

            return clone;
        }

        private static InlineAnnotation CloneAnnotation(InlineAnnotation source)
        {
            return new InlineAnnotation
            {
                Id = source.Id,
                Hash = source.Hash,
                Type = source.Type,
                Start = ClonePosition(source.Start),
                End = ClonePosition(source.End),
                Color = source.Color,
                LinkedEntityId = source.LinkedEntityId,
                DisplayLabel = source.DisplayLabel,
                Content = source.Content,
                Url = source.Url,
                BookmarkName = source.BookmarkName,
                AuthorId = source.AuthorId,
                CreatedAt = source.CreatedAt
            };
        }

        private static DocumentPosition ClonePosition(DocumentPosition source)
        {
            return new DocumentPosition
            {
                BlockId = source.BlockId,
                ChunkId = source.ChunkId,
                Offset = source.Offset
            };
        }
    }
}
