using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Гранулярная Undo-команда свойств одной картинки: хранит значения до и после
    /// операции (позиция, размер, поворот, обтекание, оформление, обрезка, отражение).
    /// В отличие от DocumentSnapshotCommand не сериализует документ целиком, поэтому
    /// операции с картинками и их отмена мгновенны даже на очень больших документах.
    /// </summary>
    public sealed class ImagePropertiesCommand : IUndoableCommand
    {
        private readonly struct ImageState
        {
            public ImageState(ImageBlock i)
            {
                WidthPt = i.WidthPt;
                HeightPt = i.HeightPt;
                OffsetXPt = i.OffsetXPt;
                OffsetYPt = i.OffsetYPt;
                RotationDeg = i.RotationDeg;
                WrapMode = i.WrapMode;
                Alignment = i.Alignment;
                Anchor = i.Anchor;
                ZOrder = i.ZOrder;
                AltText = i.AltText;
                LockAspectRatio = i.LockAspectRatio;
                Opacity = i.Opacity;
                BorderColor = i.BorderColor;
                BorderThicknessPt = i.BorderThicknessPt;
                BorderDashStyle = i.BorderDashStyle;
                ShapeType = i.ShapeType;
                CornerRadiusPt = i.CornerRadiusPt;
                BorderAlign = i.BorderAlign;
                FlipHorizontal = i.FlipHorizontal;
                FlipVertical = i.FlipVertical;
                CropLeftFrac = i.CropLeftFrac;
                CropTopFrac = i.CropTopFrac;
                CropRightFrac = i.CropRightFrac;
                CropBottomFrac = i.CropBottomFrac;
                WrapPadTopPt = i.WrapPadTopPt;
                WrapPadBottomPt = i.WrapPadBottomPt;
                WrapPadLeftPt = i.WrapPadLeftPt;
                WrapPadRightPt = i.WrapPadRightPt;
            }

            public double WidthPt { get; }
            public double HeightPt { get; }
            public double OffsetXPt { get; }
            public double OffsetYPt { get; }
            public double RotationDeg { get; }
            public WrapMode WrapMode { get; }
            public Models.Styles.TextAlignment Alignment { get; }
            public FloatAnchor Anchor { get; }
            public int ZOrder { get; }
            public string? AltText { get; }
            public bool LockAspectRatio { get; }
            public double Opacity { get; }
            public string? BorderColor { get; }
            public double BorderThicknessPt { get; }
            public ShapeDashStyle BorderDashStyle { get; }
            public ShapeType ShapeType { get; }
            public double CornerRadiusPt { get; }
            public ImageBorderAlign BorderAlign { get; }
            public bool FlipHorizontal { get; }
            public bool FlipVertical { get; }
            public double CropLeftFrac { get; }
            public double CropTopFrac { get; }
            public double CropRightFrac { get; }
            public double CropBottomFrac { get; }
            public double WrapPadTopPt { get; }
            public double WrapPadBottomPt { get; }
            public double WrapPadLeftPt { get; }
            public double WrapPadRightPt { get; }

            public void ApplyTo(ImageBlock i)
            {
                i.WidthPt = WidthPt;
                i.HeightPt = HeightPt;
                i.OffsetXPt = OffsetXPt;
                i.OffsetYPt = OffsetYPt;
                i.RotationDeg = RotationDeg;
                i.WrapMode = WrapMode;
                i.Alignment = Alignment;
                i.Anchor = Anchor;
                i.ZOrder = ZOrder;
                i.AltText = AltText;
                i.LockAspectRatio = LockAspectRatio;
                i.Opacity = Opacity;
                i.BorderColor = BorderColor;
                i.BorderThicknessPt = BorderThicknessPt;
                i.BorderDashStyle = BorderDashStyle;
                i.ShapeType = ShapeType;
                i.CornerRadiusPt = CornerRadiusPt;
                i.BorderAlign = BorderAlign;
                i.FlipHorizontal = FlipHorizontal;
                i.FlipVertical = FlipVertical;
                i.CropLeftFrac = CropLeftFrac;
                i.CropTopFrac = CropTopFrac;
                i.CropRightFrac = CropRightFrac;
                i.CropBottomFrac = CropBottomFrac;
                i.WrapPadTopPt = WrapPadTopPt;
                i.WrapPadBottomPt = WrapPadBottomPt;
                i.WrapPadLeftPt = WrapPadLeftPt;
                i.WrapPadRightPt = WrapPadRightPt;
            }
        }

        private readonly ImageBlock _image;
        private readonly ImageState _before;
        private ImageState _after;
        private bool _committed;

        public string Description { get; }

        /// <summary>Вызывается после Undo/Redo — канвас пересобирает раскладку.</summary>
        public Action? Changed { get; set; }

        public ImagePropertiesCommand(ImageBlock image, string description)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            Description = description;
            _before = new ImageState(image);
        }

        /// <summary>Фиксирует состояние «после» — вызывается по завершении операции.</summary>
        public void Commit()
        {
            _after = new ImageState(_image);
            _committed = true;
        }

        public void Execute()
        {
            if (!_committed) return;
            _after.ApplyTo(_image);
            Changed?.Invoke();
        }

        public void Undo()
        {
            _before.ApplyTo(_image);
            Changed?.Invoke();
        }
    }
}
