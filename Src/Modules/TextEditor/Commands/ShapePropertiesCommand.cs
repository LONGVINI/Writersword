using System;
using Writersword.Core.Interfaces.Modules;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Commands
{
    /// <summary>
    /// Гранулярная Undo-команда свойств одной фигуры: хранит значения до и после
    /// операции (позиция, размер, поворот, отражение, оформление, положение
    /// обводки, кадрирование заливки, прозрачность).
    /// Как и у картинок, документ целиком не сериализуется — перемещение и
    /// изменение размера фигуры отменяются мгновенно на документе любого объёма.
    /// </summary>
    public sealed class ShapePropertiesCommand : IUndoableCommand
    {
        private readonly struct ShapeState
        {
            public ShapeState(ShapeBlock s)
            {
                ShapeType = s.ShapeType;
                OffsetXPt = s.OffsetXPt;
                OffsetYPt = s.OffsetYPt;
                WidthPt = s.WidthPt;
                HeightPt = s.HeightPt;
                LockAspectRatio = s.LockAspectRatio;
                RotationDeg = s.RotationDeg;
                Opacity = s.Opacity;
                FillColor = s.FillColor;
                FillImageFileName = s.FillImageFileName;
                FillImageStretch = s.FillImageStretch;
                StrokeColor = s.StrokeColor;
                StrokeThicknessPt = s.StrokeThicknessPt;
                DashStyle = s.DashStyle;
                StrokeAlign = s.StrokeAlign;
                CornerRadiusPt = s.CornerRadiusPt;
                StartArrow = s.StartArrow;
                EndArrow = s.EndArrow;
                FlipHorizontal = s.FlipHorizontal;
                FlipVertical = s.FlipVertical;
                CropLeftFrac = s.CropLeftFrac;
                CropTopFrac = s.CropTopFrac;
                CropRightFrac = s.CropRightFrac;
                CropBottomFrac = s.CropBottomFrac;
                AltText = s.AltText;
                WrapMode = s.WrapMode;
                WrapSide = s.WrapSide;
                WrapPadTopPt = s.WrapPadTopPt;
                WrapPadBottomPt = s.WrapPadBottomPt;
                WrapPadLeftPt = s.WrapPadLeftPt;
                WrapPadRightPt = s.WrapPadRightPt;
                Alignment = s.Alignment;
                Anchor = s.Anchor;
                PinnedPage = s.PinnedPage;
                ZOrder = s.ZOrder;
                InnerText = s.InnerText;
            }

            public ShapeType ShapeType { get; }
            public double OffsetXPt { get; }
            public double OffsetYPt { get; }
            public double WidthPt { get; }
            public double HeightPt { get; }
            public bool LockAspectRatio { get; }
            public double RotationDeg { get; }
            public double Opacity { get; }
            public string? FillColor { get; }
            public string? FillImageFileName { get; }
            public bool FillImageStretch { get; }
            public string? StrokeColor { get; }
            public double StrokeThicknessPt { get; }
            public ShapeDashStyle DashStyle { get; }
            public ImageBorderAlign StrokeAlign { get; }
            public double CornerRadiusPt { get; }
            public ShapeArrowHead StartArrow { get; }
            public ShapeArrowHead EndArrow { get; }
            public bool FlipHorizontal { get; }
            public bool FlipVertical { get; }
            public double CropLeftFrac { get; }
            public double CropTopFrac { get; }
            public double CropRightFrac { get; }
            public double CropBottomFrac { get; }
            public string? AltText { get; }
            public WrapMode WrapMode { get; }
            public WrapSide WrapSide { get; }
            public double WrapPadTopPt { get; }
            public double WrapPadBottomPt { get; }
            public double WrapPadLeftPt { get; }
            public double WrapPadRightPt { get; }
            public Models.Styles.TextAlignment Alignment { get; }
            public FloatAnchor Anchor { get; }
            public int PinnedPage { get; }
            public int ZOrder { get; }
            public string? InnerText { get; }

            public void ApplyTo(ShapeBlock s)
            {
                s.ShapeType = ShapeType;
                s.OffsetXPt = OffsetXPt;
                s.OffsetYPt = OffsetYPt;
                s.WidthPt = WidthPt;
                s.HeightPt = HeightPt;
                s.LockAspectRatio = LockAspectRatio;
                s.RotationDeg = RotationDeg;
                s.Opacity = Opacity;
                s.FillColor = FillColor;
                s.FillImageFileName = FillImageFileName;
                s.FillImageStretch = FillImageStretch;
                s.StrokeColor = StrokeColor;
                s.StrokeThicknessPt = StrokeThicknessPt;
                s.DashStyle = DashStyle;
                s.StrokeAlign = StrokeAlign;
                s.CornerRadiusPt = CornerRadiusPt;
                s.StartArrow = StartArrow;
                s.EndArrow = EndArrow;
                s.FlipHorizontal = FlipHorizontal;
                s.FlipVertical = FlipVertical;
                s.CropLeftFrac = CropLeftFrac;
                s.CropTopFrac = CropTopFrac;
                s.CropRightFrac = CropRightFrac;
                s.CropBottomFrac = CropBottomFrac;
                s.AltText = AltText;
                s.WrapMode = WrapMode;
                s.WrapSide = WrapSide;
                s.WrapPadTopPt = WrapPadTopPt;
                s.WrapPadBottomPt = WrapPadBottomPt;
                s.WrapPadLeftPt = WrapPadLeftPt;
                s.WrapPadRightPt = WrapPadRightPt;
                s.Alignment = Alignment;
                s.Anchor = Anchor;
                s.PinnedPage = PinnedPage;
                s.ZOrder = ZOrder;
                s.InnerText = InnerText;
            }
        }

        private readonly ShapeBlock _shape;
        private readonly ShapeState _before;
        private ShapeState _after;
        private bool _committed;

        public string Description { get; }

        /// <summary>Вызывается после Undo/Redo — канвас пересобирает раскладку.</summary>
        public Action? Changed { get; set; }

        public ShapePropertiesCommand(ShapeBlock shape, string description)
        {
            _shape = shape ?? throw new ArgumentNullException(nameof(shape));
            Description = description;
            _before = new ShapeState(shape);
        }

        /// <summary>Фиксирует состояние «после» — вызывается по завершении операции.</summary>
        public void Commit()
        {
            _after = new ShapeState(_shape);
            _committed = true;
        }

        public void Execute()
        {
            if (!_committed) return;
            _after.ApplyTo(_shape);
            Changed?.Invoke();
        }

        public void Undo()
        {
            _before.ApplyTo(_shape);
            Changed?.Invoke();
        }
    }
}
