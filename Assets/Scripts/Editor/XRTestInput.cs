using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

/// <summary>
/// Editor-only virtual Quest controllers for play-testing without a headset. Adds a left
/// and right <c>OculusTouchController</c> tagged LeftHand / RightHand, so every
/// <c>&lt;XRController&gt;{LeftHand|RightHand}/...</c> binding in the game reads them like
/// real controllers. Values stick until changed; events are applied on the next input update.
///
/// Driven from the editor (e.g. through the MCP execute_code tool) during Play mode:
/// <code>
/// XRTestInput.Connect();
/// XRTestInput.Press("right", "primaryButton");   // A
/// XRTestInput.Stick("left", 0f, 1f);             // drive forward
/// XRTestInput.Axis("right", "trigger", 1f);      // start charging
/// XRTestInput.ReleaseAll();
/// XRTestInput.AimHand("right", panelPosition);   // point the hand anchor (menu laser)
/// </code>
/// Hand aiming only moves the OVRCameraRig anchor transforms; with no XR runtime running
/// nothing overwrites them, which is what makes pointing testable.
/// </summary>
public static class XRTestInput
{
    private const string Layout = "OculusTouchController";

    private static InputDevice _left;
    private static InputDevice _right;

    public static void Connect()
    {
        _left = EnsureDevice(_left, "Test Left Touch", CommonUsages.LeftHand);
        _right = EnsureDevice(_right, "Test Right Touch", CommonUsages.RightHand);
    }

    public static void Disconnect()
    {
        if (_left != null && _left.added) InputSystem.RemoveDevice(_left);
        if (_right != null && _right.added) InputSystem.RemoveDevice(_right);
        _left = null;
        _right = null;
    }

    /// <summary>Sets a button to pressed (1) or released (0).</summary>
    public static void Press(string hand, string control, bool pressed = true)
    {
        Axis(hand, control, pressed ? 1f : 0f);
    }

    public static void Release(string hand, string control)
    {
        Axis(hand, control, 0f);
    }

    /// <summary>Sets an axis or button control (trigger, grip, primaryButton, gripPressed, ...).</summary>
    public static void Axis(string hand, string control, float value)
    {
        InputDevice device = Device(hand);
        var target = device.TryGetChildControl(control) as InputControl<float>;
        if (target == null)
            throw new System.ArgumentException($"{Layout} has no float control '{control}'.");

        using (StateEvent.From(device, out InputEventPtr eventPtr))
        {
            target.WriteValueIntoEvent(value, eventPtr);
            InputSystem.QueueEvent(eventPtr);
        }
    }

    /// <summary>Sets the thumbstick (x right, y forward) on <paramref name="hand"/>.</summary>
    public static void Stick(string hand, float x, float y)
    {
        InputDevice device = Device(hand);
        var stick = device.TryGetChildControl("thumbstick") as InputControl<Vector2>;
        if (stick == null)
            throw new System.ArgumentException($"{Layout} has no thumbstick.");

        using (StateEvent.From(device, out InputEventPtr eventPtr))
        {
            stick.WriteValueIntoEvent(new Vector2(x, y), eventPtr);
            InputSystem.QueueEvent(eventPtr);
        }
    }

    private static readonly string[] ResettableControls =
    {
        "trigger", "grip", "primaryButton", "secondaryButton", "gripPressed", "triggerPressed",
        "thumbstickClicked", "menu",
    };

    /// <summary>Releases every button, trigger and stick on both controllers.</summary>
    public static void ReleaseAll()
    {
        foreach (InputDevice device in new[] { _left, _right })
        {
            if (device == null || !device.added) continue;

            using (StateEvent.From(device, out InputEventPtr eventPtr))
            {
                foreach (string name in ResettableControls)
                {
                    if (device.TryGetChildControl(name) is InputControl<float> control)
                        control.WriteValueIntoEvent(0f, eventPtr);
                }
                if (device.TryGetChildControl("thumbstick") is InputControl<Vector2> stick)
                    stick.WriteValueIntoEvent(Vector2.zero, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }
    }

    /// <summary>Points the rig's hand anchor at <paramref name="worldTarget"/> (for laser pointing).</summary>
    public static void AimHand(string hand, Vector3 worldTarget)
    {
        OVRCameraRig rig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (rig == null)
            throw new System.InvalidOperationException("No OVRCameraRig in the scene.");

        Transform anchor = IsLeft(hand) ? rig.leftHandAnchor : rig.rightHandAnchor;
        Vector3 head = rig.centerEyeAnchor.position;
        // Hold the hand a little below and to the side of the head, as a player would.
        anchor.position = head + rig.transform.right * (IsLeft(hand) ? -0.2f : 0.2f) + Vector3.down * 0.3f;
        anchor.rotation = Quaternion.LookRotation(worldTarget - anchor.position, Vector3.up);
    }

    /// <summary>Lifts the head anchor to standing eye height (no headset = floor level).</summary>
    public static void StandUp(float eyeHeight = 1.6f)
    {
        OVRCameraRig rig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (rig == null)
            return;
        rig.trackingSpace.localPosition = new Vector3(0f, eyeHeight, 0f);
    }

    private static InputDevice Device(string hand)
    {
        if ((IsLeft(hand) ? _left : _right) == null)
            Connect();
        return IsLeft(hand) ? _left : _right;
    }

    private static bool IsLeft(string hand) => hand.ToLowerInvariant().StartsWith("l");

    private static InputDevice EnsureDevice(InputDevice existing, string name, InternedString usage)
    {
        if (existing != null && existing.added)
            return existing;

        InputDevice device = InputSystem.AddDevice(Layout, name);
        InputSystem.SetDeviceUsage(device, usage);
        return device;
    }
}
