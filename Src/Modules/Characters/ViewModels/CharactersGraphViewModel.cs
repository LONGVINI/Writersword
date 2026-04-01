using ReactiveUI;
using Serilog;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Writersword.Modules.Characters.Interfaces;
using Writersword.Modules.Characters.Models.Enums;

namespace Writersword.Modules.Characters.ViewModels
{
    public class GraphNodeViewModel : ReactiveObject
    {
        private double _x, _y;
        public string CharacterId { get; }
        public string Name { get; }
        public string Color { get; }
        public string FallbackIcon { get; }
        public double Size { get; }
        public CharacterImportanceLevel ImportanceLevel { get; }
        public bool IsCollective { get; }

        public double X { get => _x; set => this.RaiseAndSetIfChanged(ref _x, value); }
        public double Y { get => _y; set => this.RaiseAndSetIfChanged(ref _y, value); }
        public double CenterX => X + Size / 2.0;
        public double CenterY => Y + Size / 2.0;

        private bool _isFocused;
        public bool IsFocused { get => _isFocused; set => this.RaiseAndSetIfChanged(ref _isFocused, value); }

        private bool _isDimmed;
        public bool IsDimmed { get => _isDimmed; set => this.RaiseAndSetIfChanged(ref _isDimmed, value); }

        public void RaisePropertyChanged(string name) => this.RaisePropertyChanged(name);

        public GraphNodeViewModel(string id, string name, string color, string icon,
            CharacterImportanceLevel importance, bool isCollective)
        {
            CharacterId = id;
            Name = name;
            Color = color;
            FallbackIcon = icon;
            ImportanceLevel = importance;
            IsCollective = isCollective;
            Size = importance == CharacterImportanceLevel.Primary ? 56.0
                : importance == CharacterImportanceLevel.Secondary ? 44.0
                : 34.0;
        }
    }

    public class GraphEdgeViewModel : ReactiveObject
    {
        public GraphNodeViewModel Source { get; }
        public GraphNodeViewModel Target { get; }
        public string RelationshipType { get; }
        public string EdgeColor { get; }
        public double StrokeThickness { get; }
        public bool IsBidirectional { get; }

        private bool _isHighlighted;
        public bool IsHighlighted { get => _isHighlighted; set => this.RaiseAndSetIfChanged(ref _isHighlighted, value); }

        public GraphEdgeViewModel(GraphNodeViewModel source, GraphNodeViewModel target,
            string type, string color, double thickness, bool bidirectional)
        {
            Source = source; Target = target;
            RelationshipType = type; EdgeColor = color;
            StrokeThickness = thickness; IsBidirectional = bidirectional;
        }
    }

    /// <summary>
    /// ViewModel графа связей. Используется во вкладке [Связи].
    /// </summary>
    public class CharactersGraphViewModel : ReactiveObject
    {
        private static readonly ILogger _logger = Log.ForContext<CharactersGraphViewModel>();

        private readonly ICharacterService _characterService;
        private readonly IRelationshipService _relationshipService;
        private readonly System.Action<string>? _onCharacterSelected;

        public ObservableCollection<GraphNodeViewModel> Nodes { get; } = new();
        public ObservableCollection<GraphEdgeViewModel> Edges { get; } = new();

        private double _offsetX = 0, _offsetY = 0, _scale = 1.0;
        public double OffsetX { get => _offsetX; set => this.RaiseAndSetIfChanged(ref _offsetX, value); }
        public double OffsetY { get => _offsetY; set => this.RaiseAndSetIfChanged(ref _offsetY, value); }
        public double Scale { get => _scale; set => this.RaiseAndSetIfChanged(ref _scale, value); }

        private string? _focusedCharacterId;
        public string? FocusedCharacterId
        {
            get => _focusedCharacterId;
            private set => this.RaiseAndSetIfChanged(ref _focusedCharacterId, value);
        }

        public ReactiveCommand<Unit, Unit> ResetViewCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearFocusCommand { get; }
        public ReactiveCommand<string, Unit> FocusNodeCommand { get; }
        public ReactiveCommand<string, Unit> SelectNodeCommand { get; }

        public CharactersGraphViewModel(
            ICharacterService characterService,
            IRelationshipService relationshipService,
            System.Action<string>? onCharacterSelected = null)
        {
            _characterService = characterService;
            _relationshipService = relationshipService;
            _onCharacterSelected = onCharacterSelected;

            ResetViewCommand = ReactiveCommand.Create(ResetView);
            ClearFocusCommand = ReactiveCommand.Create(ClearFocus);
            FocusNodeCommand = ReactiveCommand.Create<string>(FocusNode);
            SelectNodeCommand = ReactiveCommand.Create<string>(id => _onCharacterSelected?.Invoke(id));

            Refresh();
        }

        public void Refresh()
        {
            Nodes.Clear();
            Edges.Clear();

            var characters = _characterService.GetAll().ToList();
            var angleStep = characters.Count > 0 ? (2 * System.Math.PI / characters.Count) : 0;
            var radius = System.Math.Max(150.0, characters.Count * 30.0);

            for (int i = 0; i < characters.Count; i++)
            {
                var c = characters[i];
                var angle = i * angleStep;
                var node = new GraphNodeViewModel(c.Id, c.Name, c.Color, c.FallbackIcon, c.ImportanceLevel, c.IsCollective)
                {
                    X = radius + System.Math.Cos(angle) * radius - 24,
                    Y = radius + System.Math.Sin(angle) * radius - 24
                };
                Nodes.Add(node);
            }

            foreach (var rel in _relationshipService.GetAll())
            {
                var source = Nodes.FirstOrDefault(n => n.CharacterId == rel.SourceCharacterId);
                var target = Nodes.FirstOrDefault(n => n.CharacterId == rel.TargetCharacterId);
                if (source == null || target == null) continue;

                var emotionColor = rel.Emotion switch
                {
                    Models.Enums.CharacterRelationshipEmotion.Positive => "#4CAF50",
                    Models.Enums.CharacterRelationshipEmotion.Negative => "#F44336",
                    Models.Enums.CharacterRelationshipEmotion.Ambivalent => "#FF9800",
                    _ => "#607D8B"
                };

                var thickness = 1.0 + rel.Strength * 3.0;
                Edges.Add(new GraphEdgeViewModel(source, target, rel.RelationshipType, emotionColor, thickness, rel.IsBidirectional));
            }

            _logger.Debug("Graph refreshed: {Nodes} nodes, {Edges} edges", Nodes.Count, Edges.Count);
        }

        private void ResetView() { OffsetX = 0; OffsetY = 0; Scale = 1.0; }

        private void ClearFocus()
        {
            FocusedCharacterId = null;
            foreach (var n in Nodes) { n.IsFocused = false; n.IsDimmed = false; }
            foreach (var e in Edges) e.IsHighlighted = false;
        }

        private void FocusNode(string characterId)
        {
            FocusedCharacterId = characterId;
            var connected = Edges
                .Where(e => e.Source.CharacterId == characterId || e.Target.CharacterId == characterId)
                .SelectMany(e => new[] { e.Source.CharacterId, e.Target.CharacterId })
                .ToHashSet();

            foreach (var n in Nodes)
            {
                n.IsFocused = n.CharacterId == characterId;
                n.IsDimmed = !connected.Contains(n.CharacterId);
            }
            foreach (var e in Edges)
                e.IsHighlighted = e.Source.CharacterId == characterId || e.Target.CharacterId == characterId;
        }
    }
}
