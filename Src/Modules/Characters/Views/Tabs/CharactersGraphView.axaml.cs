using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Serilog;
using SkiaSharp;
using System;
using System.Linq;
using Writersword.Core.Models.Project;
using Writersword.Modules.Characters.Models.Enums;
using Writersword.Modules.Characters.ViewModels;

namespace Writersword.Modules.Characters.Views.Tabs
{
    public partial class CharactersGraphView : UserControl
    {
        private CharactersGraphViewModel? _viewModel;
        private GraphRenderCanvas? _renderCanvas;

        private bool _isPanning = false;
        private Point _panStart;
        private double _panOffsetXAtStart;
        private double _panOffsetYAtStart;

        private bool _isDraggingNode = false;
        private GraphNodeViewModel? _draggedNode;
        private Point _dragStart;
        private double _nodeXAtDragStart;
        private double _nodeYAtDragStart;

        public CharactersGraphView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            _viewModel = DataContext as CharactersGraphViewModel;
            if (_viewModel == null) return;

            var container = this.FindControl<Grid>("GraphCanvas");
            if (container == null) return;

            _renderCanvas = new GraphRenderCanvas(_viewModel);
            container.Children.Add(_renderCanvas);

            _renderCanvas.PointerPressed += OnPointerPressed;
            _renderCanvas.PointerMoved += OnPointerMoved;
            _renderCanvas.PointerReleased += OnPointerReleased;
            _renderCanvas.PointerWheelChanged += OnPointerWheelChanged;

            _viewModel.Nodes.CollectionChanged += (_, _) => _renderCanvas?.InvalidateVisual();
            _viewModel.Edges.CollectionChanged += (_, _) => _renderCanvas?.InvalidateVisual();
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_viewModel == null || _renderCanvas == null) return;

            var pos = e.GetPosition(_renderCanvas);
            var graphPos = ScreenToGraph(pos);
            var hitNode = HitTestNode(graphPos);

            if (hitNode != null)
            {
                var props = e.GetCurrentPoint(_renderCanvas).Properties;

                if (props.IsRightButtonPressed)
                {
                    _viewModel.FocusNodeCommand.Execute(hitNode.CharacterId).Subscribe();
                    _renderCanvas.InvalidateVisual();
                    e.Handled = true;
                    return;
                }

                if (props.IsLeftButtonPressed)
                {
                    _isDraggingNode = true;
                    _draggedNode = hitNode;
                    _dragStart = pos;
                    _nodeXAtDragStart = hitNode.X;
                    _nodeYAtDragStart = hitNode.Y;
                    e.Pointer.Capture(_renderCanvas);
                    e.Handled = true;
                    return;
                }
            }

            _isPanning = true;
            _panStart = pos;
            _panOffsetXAtStart = _viewModel.OffsetX;
            _panOffsetYAtStart = _viewModel.OffsetY;
            e.Pointer.Capture(_renderCanvas);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_viewModel == null || _renderCanvas == null) return;

            var pos = e.GetPosition(_renderCanvas);

            if (_isDraggingNode && _draggedNode != null)
            {
                var dx = (pos.X - _dragStart.X) / _viewModel.Scale;
                var dy = (pos.Y - _dragStart.Y) / _viewModel.Scale;
                _draggedNode.X = _nodeXAtDragStart + dx;
                _draggedNode.Y = _nodeYAtDragStart + dy;
                _renderCanvas.InvalidateVisual();
                return;
            }

            if (_isPanning)
            {
                _viewModel.OffsetX = _panOffsetXAtStart + (pos.X - _panStart.X);
                _viewModel.OffsetY = _panOffsetYAtStart + (pos.Y - _panStart.Y);
                _renderCanvas.InvalidateVisual();
            }
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_viewModel == null || _renderCanvas == null) return;

            var pos = e.GetPosition(_renderCanvas);

            if (_isDraggingNode && _draggedNode != null)
            {
                var dx = Math.Abs(pos.X - _dragStart.X);
                var dy = Math.Abs(pos.Y - _dragStart.Y);
                if (dx < 5 && dy < 5)
                    _viewModel.SelectNodeCommand.Execute(_draggedNode.CharacterId).Subscribe();

                _isDraggingNode = false;
                _draggedNode = null;
            }

            _isPanning = false;
            e.Pointer.Capture(null);
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (_viewModel == null || _renderCanvas == null) return;

            var pos = e.GetPosition(_renderCanvas);
            var delta = e.Delta.Y > 0 ? 1.1 : 0.9;
            var newScale = Math.Clamp(_viewModel.Scale * delta, 0.2, 4.0);
            var ratio = newScale / _viewModel.Scale;

            _viewModel.OffsetX = pos.X - (pos.X - _viewModel.OffsetX) * ratio;
            _viewModel.OffsetY = pos.Y - (pos.Y - _viewModel.OffsetY) * ratio;
            _viewModel.Scale = newScale;

            _renderCanvas.InvalidateVisual();
            e.Handled = true;
        }

        private Point ScreenToGraph(Point screenPos)
        {
            if (_viewModel == null) return screenPos;
            return new Point(
                (screenPos.X - _viewModel.OffsetX) / _viewModel.Scale,
                (screenPos.Y - _viewModel.OffsetY) / _viewModel.Scale);
        }

        private GraphNodeViewModel? HitTestNode(Point graphPos)
        {
            if (_viewModel == null) return null;
            foreach (var node in _viewModel.Nodes)
            {
                var radius = node.Size / 2.0;
                var cx = node.X + radius;
                var cy = node.Y + radius;
                var dx = graphPos.X - cx;
                var dy = graphPos.Y - cy;
                if (dx * dx + dy * dy <= radius * radius)
                    return node;
            }
            return null;
        }

        private sealed class GraphRenderCanvas : Control
        {
            private readonly CharactersGraphViewModel _viewModel;

            public GraphRenderCanvas(CharactersGraphViewModel viewModel)
            {
                _viewModel = viewModel;
                ClipToBounds = true;
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            }

            public override void Render(DrawingContext ctx)
            {
                SKColor bgColor = SKColors.Black;
                if (this.TryGetResource("BgSurfaceBrush", Avalonia.Styling.ThemeVariant.Default, out var res)
                    && res is Avalonia.Media.SolidColorBrush brush)
                {
                    var c = brush.Color;
                    bgColor = new SKColor(c.R, c.G, c.B, c.A);
                }
                ctx.Custom(new GraphDrawOperation(_viewModel, bgColor, new Rect(0, 0, Bounds.Width, Bounds.Height)));
            }

            private sealed class GraphDrawOperation : ICustomDrawOperation
            {
                private static readonly SKTypeface _defaultTypeface = SKTypeface.FromFamilyName("sans-serif");
                private readonly CharactersGraphViewModel _viewModel;
                private readonly SKColor _bgColor;
                public Rect Bounds { get; }

                public GraphDrawOperation(CharactersGraphViewModel viewModel, SKColor bgColor, Rect bounds)
                {
                    _viewModel = viewModel;
                    _bgColor = bgColor;
                    Bounds = bounds;
                }

                public void Dispose() { }
                public bool Equals(ICustomDrawOperation? other) => false;
                public bool HitTest(Point p) => true;

                public void Render(ImmediateDrawingContext context)
                {
                    var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
                    if (feature is null) return;
                    using var lease = feature.Lease();
                    RenderGraph(lease.SkCanvas);
                }

                private void RenderGraph(SKCanvas canvas)
                {
                    canvas.Clear(_bgColor);
                    canvas.Save();
                    canvas.Translate((float)_viewModel.OffsetX, (float)_viewModel.OffsetY);
                    canvas.Scale((float)_viewModel.Scale);
                    DrawEdges(canvas);
                    DrawNodes(canvas);
                    canvas.Restore();
                }

                private void DrawEdges(SKCanvas canvas)
                {
                    foreach (var edge in _viewModel.Edges)
                    {
                        var alpha = edge.IsHighlighted ? 220 : 60;
                        var color = ResolveColor(edge.EdgeColor, (byte)alpha);

                        using var paint = new SKPaint
                        {
                            Color = color,
                            StrokeWidth = (float)edge.StrokeThickness,
                            IsStroke = true,
                            IsAntialias = true,
                            StrokeCap = SKStrokeCap.Round
                        };

                        var x1 = (float)edge.Source.CenterX;
                        var y1 = (float)edge.Source.CenterY;
                        var x2 = (float)edge.Target.CenterX;
                        var y2 = (float)edge.Target.CenterY;

                        canvas.DrawLine(x1, y1, x2, y2, paint);

                        if (edge.IsHighlighted && !string.IsNullOrEmpty(edge.RelationshipType))
                        {
                            var midX = (x1 + x2) / 2f;
                            var midY = (y1 + y2) / 2f;
                            using var labelFont = new SKFont(_defaultTypeface, 10f);
                            using var labelPaint = new SKPaint { Color = color, IsAntialias = true };
                            canvas.DrawText(edge.RelationshipType, midX, midY - 4f, SKTextAlign.Center, labelFont, labelPaint);
                        }

                        if (!edge.IsBidirectional)
                            DrawArrow(canvas, x1, y1, x2, y2, color, (float)edge.StrokeThickness);
                    }
                }

                private static void DrawArrow(SKCanvas canvas, float x1, float y1, float x2, float y2, SKColor color, float thickness)
                {
                    var dx = x2 - x1;
                    var dy = y2 - y1;
                    var len = MathF.Sqrt(dx * dx + dy * dy);
                    if (len < 1f) return;

                    dx /= len; dy /= len;
                    var arrowX = x2 - dx * 24f;
                    var arrowY = y2 - dy * 24f;
                    var arrowSize = 8f + thickness;

                    var ax = arrowX - dx * arrowSize + dy * arrowSize * 0.4f;
                    var ay = arrowY - dy * arrowSize - dx * arrowSize * 0.4f;
                    var bx = arrowX - dx * arrowSize - dy * arrowSize * 0.4f;
                    var by = arrowY - dy * arrowSize + dx * arrowSize * 0.4f;

                    using var paint = new SKPaint { Color = color, IsAntialias = true };
                    using var path = new SKPath();
                    path.MoveTo(arrowX, arrowY);
                    path.LineTo(ax, ay);
                    path.LineTo(bx, by);
                    path.Close();
                    canvas.DrawPath(path, paint);
                }

                // Цвет узла/ребра может быть кодом градиента (grad|...). Skia парсит
                // только сплошной hex, поэтому сводим к первому цвету спека.
                private static SKColor ResolveColor(string? code, byte alpha)
                {
                    var hex = GradientSpec.Parse(code).SolidHex;
                    var color = SKColor.TryParse(hex, out var c) ? c : new SKColor(0x60, 0x7D, 0x8B);
                    return color.WithAlpha(alpha);
                }

                private void DrawNodes(SKCanvas canvas)
                {
                    foreach (var node in _viewModel.Nodes)
                    {
                        // Мёртвые узлы приглушаются; крестик рисуется ниже как
                        // объект-носитель смысла (приглушение — только усилитель).
                        var alpha = node.IsDimmed ? 80 : node.IsDead ? 140 : 255;
                        var nodeColor = ResolveColor(node.Color, (byte)alpha);
                        var size = (float)node.Size;
                        var cx = (float)(node.X + size / 2.0);
                        var cy = (float)(node.Y + size / 2.0);
                        var radius = size / 2f;

                        using var fillPaint = new SKPaint { Color = nodeColor, IsAntialias = true };
                        canvas.DrawCircle(cx, cy, radius, fillPaint);

                        if (node.IsFocused)
                        {
                            using var strokePaint = new SKPaint
                            {
                                Color = SKColors.White.WithAlpha(220),
                                StrokeWidth = 3f,
                                IsStroke = true,
                                IsAntialias = true
                            };
                            canvas.DrawCircle(cx, cy, radius + 3f, strokePaint);
                        }

                        var iconSize = size * 0.38f;
                        using var iconFont = new SKFont(_defaultTypeface, iconSize);
                        using var iconPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)alpha), IsAntialias = true };
                        canvas.DrawText(node.FallbackIcon, cx, cy + iconSize * 0.35f, SKTextAlign.Center, iconFont, iconPaint);

                        var nameSize = node.ImportanceLevel == CharacterImportanceLevel.Primary ? 12f : 10f;
                        using var nameFont = new SKFont(_defaultTypeface, nameSize);
                        using var namePaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)alpha), IsAntialias = true };
                        canvas.DrawText(node.Name, cx, cy + radius + nameSize + 2f, SKTextAlign.Center, nameFont, namePaint);

                        // Метка «Мёртв»: бейдж в правом верхнем секторе узла.
                        // Значок — череп из общего реестра иконок меток, а не
                        // крестик: крестиком в интерфейсе обозначается удаление,
                        // и бейдж читался как кнопка «убрать персонажа».
                        if (node.IsDead)
                        {
                            var badgeR = System.Math.Max(5f, radius * 0.28f);
                            var bx = cx + radius * 0.72f;
                            var by = cy - radius * 0.72f;
                            using var badgeBg = new SKPaint { Color = new SKColor(0x20, 0x20, 0x20, 0xCC), IsAntialias = true };
                            canvas.DrawCircle(bx, by, badgeR, badgeBg);

                            var skullData = Writersword.Modules.Characters.Models.CharacterLabelIcons
                                .GetPathData(Writersword.Modules.Characters.Models.CharacterLabelIcons.Skull);
                            using var skullPath = SKPath.ParseSvgPathData(skullData);
                            using var skullPaint = new SKPaint
                            {
                                Color = new SKColor(0xFF, 0x52, 0x52),
                                IsStroke = false,
                                IsAntialias = true
                            };

                            if (skullPath != null)
                            {
                                // Геометрия иконок задана в системе координат 24x24;
                                // вписываем её в квадрат внутри кружка бейджа.
                                var side = badgeR * 1.45f;
                                var scale = side / 24f;
                                skullPath.Transform(SKMatrix.CreateScaleTranslation(
                                    scale, scale, bx - side / 2f, by - side / 2f));
                                canvas.DrawPath(skullPath, skullPaint);
                            }
                            else
                            {
                                // Запасной путь, если геометрия не разобралась:
                                // сплошная точка вместо значка, но не крестик.
                                canvas.DrawCircle(bx, by, badgeR * 0.45f, skullPaint);
                            }
                        }
                    }
                }
            }
        }
    }
}
