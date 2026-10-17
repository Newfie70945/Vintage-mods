using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace NewfiesPlayerList;

/// <summary>
/// Reliable hold-key input for the public player list.
///
/// The player-list hotkey is inserted at the front of Vintage Story's hotkey
/// table so the companion gets first chance to consume Tab before vanilla chat.
/// A low-level KeyUp listener plus raw key-state validation handles release.
/// There is no repeating listener while the key is idle.
/// </summary>
public sealed class PlayerListKeyHandler : IDisposable
{
    public const string HotkeyCode = "newfiesplayerlist";

    private readonly ICoreClientAPI api;
    private int heldPrimaryKey = -1;
    private bool disposed;

    public bool IsHeld { get; private set; }

    public event Action? Pressed;
    public event Action? Released;

    public PlayerListKeyHandler(ICoreClientAPI api)
    {
        this.api = api;

        // Tab is also used by vanilla chat. Register first and consume the hotkey
        // only during normal in-world mouse-grabbed gameplay; when a GUI/chat has
        // the mouse, return false so vanilla and other dialogs can keep Tab.
        // Some client reconnect/redirect paths can preserve a hotkey registration.
        // Reuse it when present instead of throwing on a duplicate registration.
        if (api.Input.GetHotKeyByCode(HotkeyCode) == null)
        {
            api.Input.RegisterHotKeyFirst(
                HotkeyCode,
                "Show Newfie's public player list",
                GlKeys.Tab,
                HotkeyType.HelpAndOverlays);
        }

        api.Input.SetHotKeyHandler(HotkeyCode, OnHotKey);

        api.Event.KeyUp += OnKeyUp;
        api.Event.HotkeysChanged += OnHotkeysChanged;
        api.Event.LeaveWorld += OnLeaveWorld;
    }

    private bool OnHotKey(KeyCombination _)
    {
        if (disposed || IsHeld) return IsHeld;
        if (!CanActivate()) return false;

        HotKey? hotkey = api.Input.GetHotKeyByCode(HotkeyCode);
        if (hotkey == null) return false;

        heldPrimaryKey = hotkey.CurrentMapping.KeyCode;
        if (heldPrimaryKey <= 0) return false;

        IsHeld = true;
        Pressed?.Invoke();

        // Consume the press so vanilla's Tab/chat hotkey does not also fire.
        return true;
    }

    private void OnKeyUp(KeyEvent e)
    {
        if (disposed || !IsHeld) return;

        if (e.KeyCode == heldPrimaryKey)
        {
            e.Handled = true;
            Release();
            return;
        }

        // If a modifier/secondary key was released first, raw validation catches it.
        if (!IsConfiguredCombinationDownRaw())
            Release();
    }

    private bool CanActivate()
    {
        if (!api.PlayerReadyFired || api.IsShuttingDown) return false;

        // In normal gameplay the mouse is grabbed. Chat, inventories, the escape
        // menu and other interactive dialogs release it, so we do not steal Tab
        // while the player is typing or navigating another GUI.
        return api.Input.MouseGrabbed;
    }

    public bool ValidateHeldState()
    {
        if (!IsHeld) return false;
        if (!IsConfiguredCombinationDownRaw())
            Release();
        return IsHeld;
    }

    private bool IsConfiguredCombinationDownRaw()
    {
        HotKey? hotkey = api.Input.GetHotKeyByCode(HotkeyCode);
        if (hotkey == null) return false;

        KeyCombination combo = hotkey.CurrentMapping;
        if (!RawKeyDown(combo.KeyCode)) return false;
        if (combo.SecondKeyCode.HasValue && combo.SecondKeyCode.Value != 0 && !RawKeyDown(combo.SecondKeyCode.Value))
            return false;

        return IsAltDownRaw() == combo.Alt &&
               IsCtrlDownRaw() == combo.Ctrl &&
               IsShiftDownRaw() == combo.Shift;
    }

    private bool IsAltDownRaw() =>
        RawKeyDown((int)GlKeys.AltLeft) || RawKeyDown((int)GlKeys.AltRight) ||
        RawKeyDown((int)GlKeys.LAlt) || RawKeyDown((int)GlKeys.RAlt);

    private bool IsCtrlDownRaw() =>
        RawKeyDown((int)GlKeys.ControlLeft) || RawKeyDown((int)GlKeys.ControlRight) ||
        RawKeyDown((int)GlKeys.LControl) || RawKeyDown((int)GlKeys.RControl);

    private bool IsShiftDownRaw() =>
        RawKeyDown((int)GlKeys.ShiftLeft) || RawKeyDown((int)GlKeys.ShiftRight) ||
        RawKeyDown((int)GlKeys.LShift) || RawKeyDown((int)GlKeys.RShift);

    private bool RawKeyDown(int keyCode)
    {
        bool[] state = api.Input.KeyboardKeyStateRaw;
        return keyCode >= 0 && keyCode < state.Length && state[keyCode];
    }

    private void OnHotkeysChanged() => Release();
    private void OnLeaveWorld() => Release();

    private void Release()
    {
        if (!IsHeld) return;
        IsHeld = false;
        heldPrimaryKey = -1;
        Released?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        api.Event.KeyUp -= OnKeyUp;
        api.Event.HotkeysChanged -= OnHotkeysChanged;
        api.Event.LeaveWorld -= OnLeaveWorld;

        // Remove our consumable handler. The hotkey registration itself is owned by
        // the client input system and can safely remain until the game session ends.
        HotKey? hotkey = api.Input.GetHotKeyByCode(HotkeyCode);
        if (hotkey != null)
            hotkey.Handler = null!;

        IsHeld = false;
        heldPrimaryKey = -1;
        Pressed = null;
        Released = null;
    }
}
