using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Writersword.Infrastructure.Behaviours
{
    /// <summary>
    /// Attached behavior for TextBox: executes a command when the user clicks
    /// anywhere outside the TextBox while it is visible.
    ///
    /// Usage:
    ///   behaviors:ClickOutsideBehavior.Command="{ReflectionBinding SomeCommand}"
    ///   behaviors:ClickOutsideBehavior.CommandParameter="{ReflectionBinding SomeId}"  <!-- optional -->
    /// </summary>
    public static class ClickOutsideBehavior
    {
        // ── Attached properties ───────────────────────────────────────────

        // Avalonia's RegisterAttached requires a non-static TOwner class.
        // A private nested class is the standard workaround for static behavior types.
        private sealed class Owner { }

        public static readonly AttachedProperty<ICommand?> CommandProperty =
            AvaloniaProperty.RegisterAttached<Owner, TextBox, ICommand?>("Command");

        public static readonly AttachedProperty<object?> CommandParameterProperty =
            AvaloniaProperty.RegisterAttached<Owner, TextBox, object?>("CommandParameter");

        public static ICommand? GetCommand(TextBox element) => element.GetValue(CommandProperty);
        public static void SetCommand(TextBox element, ICommand? value) => element.SetValue(CommandProperty, value);

        public static object? GetCommandParameter(TextBox element) => element.GetValue(CommandParameterProperty);
        public static void SetCommandParameter(TextBox element, object? value) => element.SetValue(CommandParameterProperty, value);

        // ── State storage (per TextBox instance) ─────────────────────────

        private sealed class State
        {
            public TopLevel? TopLevel;
            public EventHandler<PointerPressedEventArgs>? Handler;
        }

        private static readonly ConditionalWeakTable<TextBox, State> _states = new();

        // ── Initialization ────────────────────────────────────────────────

        static ClickOutsideBehavior()
        {
            CommandProperty.Changed.AddClassHandler<TextBox>(OnCommandChanged);
        }

        private static void OnCommandChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
        {
            // Unsubscribe previous setup regardless of direction of change.
            Detach(box);
            box.AttachedToVisualTree -= OnAttachedToVisualTree;
            box.DetachedFromVisualTree -= OnDetachedFromVisualTree;

            if (e.NewValue is not null)
            {
                box.AttachedToVisualTree += OnAttachedToVisualTree;
                box.DetachedFromVisualTree += OnDetachedFromVisualTree;

                // Already in visual tree at the moment the property is set.
                if (TopLevel.GetTopLevel(box) is not null)
                    Attach(box);
            }
        }

        private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is TextBox box) Attach(box);
        }

        private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is TextBox box) Detach(box);
        }

        // ── Subscribe / Unsubscribe ────────────────────────────────────────

        private static void Attach(TextBox box)
        {
            var topLevel = TopLevel.GetTopLevel(box);
            if (topLevel == null) return;

            var state = new State { TopLevel = topLevel };

            state.Handler = (_, args) =>
            {
                if (!box.IsVisible) return;

                var command = box.GetValue(CommandProperty);
                if (command == null) return;

                Visual? src = args.Source as Visual;
                if (IsInsideControl(src, box)) return;

                var parameter = box.GetValue(CommandParameterProperty);
                if (command.CanExecute(parameter))
                    command.Execute(parameter);
            };

            _states.AddOrUpdate(box, state);
            topLevel.AddHandler(
                InputElement.PointerPressedEvent,
                state.Handler,
                RoutingStrategies.Tunnel);
        }

        private static void Detach(TextBox box)
        {
            if (_states.TryGetValue(box, out var state))
            {
                if (state.Handler is not null)
                    state.TopLevel?.RemoveHandler(InputElement.PointerPressedEvent, state.Handler);
                _states.Remove(box);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        // Returns true when 'source' is 'target' itself or any of its visual descendants.
        private static bool IsInsideControl(Visual? source, Control target)
        {
            Visual? v = source;
            while (v != null)
            {
                if (ReferenceEquals(v, target)) return true;
                v = v.GetVisualParent();
            }
            return false;
        }
    }
}