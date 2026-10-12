using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Loamlight.Player
{
    /// <summary>
    /// Everything the player character reads from "the player". Press events are latched until the fixed-step
    /// simulation consumes them, so a tap shorter than one physics step is never lost.
    /// The real game uses <see cref="InputSystemPlayerInput"/>; scenarios and tests drive <see cref="ScriptedPlayerInput"/>.
    /// </summary>
    public interface IPlayerInput
    {
        float MoveX { get; }
        bool JumpHeld { get; }
        bool DownHeld { get; }
        bool UseHeld { get; }
        bool ShiftHeld { get; }
        Vector2 AimWorld { get; }
        bool ConsumeJumpPressed();
        bool ConsumeUsePressed();
        bool ConsumeInteractPressed();
        bool ConsumeUndo();
        /// <summary>Hotbar slot chosen with a number key since the last call, or -1.</summary>
        int ConsumeHotbarSelect();
        /// <summary>Accumulated scroll steps since the last call (positive = next slot).</summary>
        int ConsumeHotbarScroll();
    }

    /// <summary>Scenario/test input: set the fields, call Press* for edge events.</summary>
    public sealed class ScriptedPlayerInput : IPlayerInput
    {
        public float moveX;
        public bool jumpHeld, downHeld, useHeld, shiftHeld;
        public Vector2 aimWorld;
        bool _jump, _use, _interact, _undo;
        int _hotbar = -1, _scroll;

        public float MoveX => moveX;
        public bool JumpHeld => jumpHeld;
        public bool DownHeld => downHeld;
        public bool UseHeld => useHeld;
        public bool ShiftHeld => shiftHeld;
        public Vector2 AimWorld => aimWorld;

        public void PressJump() { _jump = true; jumpHeld = true; }
        public void PressUse() { _use = true; }
        public void PressInteract() { _interact = true; }
        public void PressUndo() { _undo = true; }
        public void SelectHotbar(int slot) { _hotbar = slot; }
        public void Scroll(int steps) { _scroll += steps; }

        public bool ConsumeJumpPressed() { bool v = _jump; _jump = false; return v; }
        public bool ConsumeUsePressed() { bool v = _use; _use = false; return v; }
        public bool ConsumeInteractPressed() { bool v = _interact; _interact = false; return v; }
        public bool ConsumeUndo() { bool v = _undo; _undo = false; return v; }
        public int ConsumeHotbarSelect() { int v = _hotbar; _hotbar = -1; return v; }
        public int ConsumeHotbarScroll() { int v = _scroll; _scroll = 0; return v; }
    }

    /// <summary>
    /// Input System actions built in code (no .inputactions asset to hand-edit). Binding overrides are stored as
    /// JSON by the settings menu, so every action is rebindable.
    /// </summary>
    public sealed class InputSystemPlayerInput : IPlayerInput, IDisposable
    {
        public readonly InputActionMap Map;
        public readonly InputAction Move, Jump, Down, Use, Interact, Undo, Shift, Point, Scroll;
        public readonly InputAction[] Hotbar = new InputAction[10];

        readonly Func<Camera> _camera;
        bool _jump, _use, _interact, _undo;
        int _hotbar = -1, _scroll;

        public InputSystemPlayerInput(Func<Camera> camera)
        {
            _camera = camera;
            Map = new InputActionMap("Gameplay");
            Move = Map.AddAction("Move", InputActionType.Value);
            Move.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/a").With("Positive", "<Keyboard>/d");
            Move.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/leftArrow").With("Positive", "<Keyboard>/rightArrow");
            Move.AddBinding("<Gamepad>/leftStick/x");
            Jump = Map.AddAction("Jump", InputActionType.Button, "<Keyboard>/space");
            Jump.AddBinding("<Gamepad>/buttonSouth");
            Down = Map.AddAction("Down", InputActionType.Button, "<Keyboard>/s");
            Down.AddBinding("<Keyboard>/downArrow");
            Down.AddBinding("<Gamepad>/leftStick/down");
            Use = Map.AddAction("Use", InputActionType.Button, "<Mouse>/leftButton");
            Use.AddBinding("<Gamepad>/rightTrigger");
            Interact = Map.AddAction("Interact", InputActionType.Button, "<Mouse>/rightButton");
            Interact.AddBinding("<Gamepad>/buttonWest");
            Undo = Map.AddAction("Undo", InputActionType.Button);
            Undo.AddCompositeBinding("ButtonWithOneModifier").With("Modifier", "<Keyboard>/ctrl").With("Button", "<Keyboard>/z");
            Shift = Map.AddAction("Shift", InputActionType.Button, "<Keyboard>/shift");
            Point = Map.AddAction("Point", InputActionType.Value, "<Pointer>/position");
            Scroll = Map.AddAction("Scroll", InputActionType.Value, "<Mouse>/scroll/y");
            for (int i = 0; i < 10; i++)
                Hotbar[i] = Map.AddAction($"Hotbar{(i + 1) % 10}", InputActionType.Button, $"<Keyboard>/{(i + 1) % 10}");

            Jump.performed += _ => _jump = true;
            Use.performed += _ => _use = true;
            Interact.performed += _ => _interact = true;
            Undo.performed += _ => _undo = true;
            for (int i = 0; i < 10; i++)
            {
                int slot = i;
                Hotbar[i].performed += _ => _hotbar = slot;
            }
            // one slot per wheel event regardless of the device's delta scale (notched wheels and touchpads alike)
            Scroll.performed += ctx =>
            {
                float v = ctx.ReadValue<float>();
                if (v > 0.01f) _scroll--;
                else if (v < -0.01f) _scroll++;
            };
            Map.Enable();
        }

        public float MoveX => Mathf.Clamp(Move.ReadValue<float>(), -1f, 1f);
        public bool JumpHeld => Jump.IsPressed();
        public bool DownHeld => Down.IsPressed();
        public bool UseHeld => Use.IsPressed();
        public bool ShiftHeld => Shift.IsPressed();

        public Vector2 AimWorld
        {
            get
            {
                var cam = _camera();
                if (cam == null) return Vector2.zero;
                var p = Point.ReadValue<Vector2>();
                return cam.ScreenToWorldPoint(new Vector3(p.x, p.y, -cam.transform.position.z));
            }
        }

        /// <summary>Pause gameplay input while a menu has focus.</summary>
        public bool Enabled
        {
            get => Map.enabled;
            set { if (value) Map.Enable(); else Map.Disable(); }
        }

        public bool ConsumeJumpPressed() { bool v = _jump; _jump = false; return v; }
        public bool ConsumeUsePressed() { bool v = _use; _use = false; return v; }
        public bool ConsumeInteractPressed() { bool v = _interact; _interact = false; return v; }
        public bool ConsumeUndo() { bool v = _undo; _undo = false; return v; }
        public int ConsumeHotbarSelect() { int v = _hotbar; _hotbar = -1; return v; }
        public int ConsumeHotbarScroll() { int v = _scroll; _scroll = 0; return v; }

        public void Dispose()
        {
            Map.Disable();
            Map.Dispose();
        }
    }
}
