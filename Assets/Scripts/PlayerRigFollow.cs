using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// VR only. The player is a giant standing in the mountains while driving a small tank,
/// and the map is far bigger than the play space, so this moves the camera rig root
/// (never TrackingSpace) to keep the player near their tank:
/// <list type="bullet">
/// <item>Glide: once the tank is more than <see cref="followRadius"/> from the head, the rig
/// eases after it just far enough to bring it back inside. The player can still walk
/// around physically within that radius.</item>
/// <item>Snap: a big jump (teleport shot, respawn), or any follow while the comfort setting
/// <see cref="GameConfigManager.vrSnapFollow"/> is on, moves the rig instantly instead.</item>
/// <item>Height: the rig floor follows the terrain under the head, plus an offset the
/// player raises with grip-drag to look over the mountains.</item>
/// <item>Grip (either hand): grab the world and drag yourself around, 1:1.</item>
/// <item>X: recenter behind the tank, facing its forward.</item>
/// </list>
/// Runs on unscaled time so the player can still reposition while the config menu has
/// the game paused.
/// </summary>
[RequireComponent(typeof(OVRCameraRig))]
public class PlayerRigFollow : MonoBehaviour
{
    [Header("Follow")]
    [Tooltip("Horizontal head-to-tank distance the player may drift before the rig starts following.")]
    [SerializeField] private float followRadius = 1.5f;
    [Tooltip("Smoothing time of the glide follow, in seconds. Higher = lazier.")]
    [SerializeField] private float followSmoothTime = 0.6f;
    [Tooltip("Beyond this head-to-tank distance the rig jumps instead of gliding (teleport shot, respawn).")]
    [SerializeField] private float snapDistance = 4f;
    [Tooltip("How far behind the tank Recenter (X) places the head.")]
    [SerializeField] private float behindDistance = 1.2f;

    [Header("Height")]
    [Tooltip("Smoothing time of the rig floor following the terrain, in seconds.")]
    [SerializeField] private float heightSmoothTime = 0.35f;
    [Tooltip("How high above the terrain grip-drag can lift the player, in meters.")]
    [SerializeField] private float maxHeightOffset = 4f;

    private OVRCameraRig _rig;
    private Transform _target;
    private System.Func<Vector3, float> _groundHeight;

    private InputAction _recenterAction;
    private InputAction _leftGripAction;
    private InputAction _rightGripAction;

    private Vector3 _followVelocity;
    private float _heightVelocity;
    private float _heightOffset;
    private Transform _dragHand;
    private Vector3 _lastDragHandLocal;

    private void Awake()
    {
        _rig = GetComponent<OVRCameraRig>();
    }

    private void OnEnable()
    {
        _recenterAction = new InputAction("Recenter", InputActionType.Button, "<XRController>{LeftHand}/primaryButton");
        _leftGripAction = new InputAction("DragLeft", InputActionType.Button, "<XRController>{LeftHand}/gripPressed");
        _rightGripAction = new InputAction("DragRight", InputActionType.Button, "<XRController>{RightHand}/gripPressed");
        _recenterAction.Enable();
        _leftGripAction.Enable();
        _rightGripAction.Enable();
    }

    private void OnDisable()
    {
        _recenterAction?.Disable();
        _leftGripAction?.Disable();
        _rightGripAction?.Disable();
        _recenterAction?.Dispose();
        _leftGripAction?.Dispose();
        _rightGripAction?.Dispose();
        _recenterAction = null;
        _leftGripAction = null;
        _rightGripAction = null;
    }

    /// <summary>Starts following <paramref name="target"/>, using <paramref name="groundHeight"/> to find the floor under the head.</summary>
    public void SetTarget(Transform target, System.Func<Vector3, float> groundHeight)
    {
        _target = target;
        _groundHeight = groundHeight;
        _followVelocity = Vector3.zero;
        _heightVelocity = 0f;
    }

    /// <summary>Puts the head <see cref="behindDistance"/> behind the target, looking along its forward.</summary>
    public void SnapBehindTarget()
    {
        if (_target == null || _groundHeight == null)
            return;

        Transform head = _rig.centerEyeAnchor;
        Vector3 tankForward = Flat(_target.forward);
        if (tankForward.sqrMagnitude < 0.0001f)
            tankForward = Flat(head.forward);
        if (tankForward.sqrMagnitude < 0.0001f)
            tankForward = Vector3.forward;
        tankForward.Normalize();

        // Yaw the rig about the head so the player ends up looking along the tank.
        Vector3 headForward = Flat(head.forward);
        if (headForward.sqrMagnitude > 0.0001f)
            transform.RotateAround(head.position, Vector3.up, Vector3.SignedAngle(headForward, tankForward, Vector3.up));

        Vector3 desiredHead = _target.position - tankForward * behindDistance;
        Vector3 rigPosition = transform.position + Flat(desiredHead - head.position);
        rigPosition.y = _groundHeight(desiredHead) + _heightOffset;
        transform.position = rigPosition;

        _followVelocity = Vector3.zero;
        _heightVelocity = 0f;
    }

    private void LateUpdate()
    {
        if (_target == null || _groundHeight == null)
            return;

        if (_recenterAction.WasPressedThisFrame())
        {
            SnapBehindTarget();
            return;
        }

        float dt = Time.unscaledDeltaTime;
        Transform head = _rig.centerEyeAnchor;
        Vector3 rigPosition = transform.position;
        bool snapped = false;

        if (!UpdateGripDrag(ref rigPosition))
        {
            Vector3 toTarget = Flat(_target.position - head.position);
            float distance = toTarget.magnitude;
            bool comfortSnap = GameConfigManager.Instance != null && GameConfigManager.Instance.vrSnapFollow;

            if (distance > snapDistance || (comfortSnap && distance > followRadius))
            {
                // Jump so the tank lands halfway inside the follow radius, same direction from the head.
                rigPosition += toTarget * ((distance - followRadius * 0.5f) / distance);
                _followVelocity = Vector3.zero;
                snapped = true;
            }
            else if (distance > followRadius)
            {
                Vector3 desired = rigPosition + toTarget * ((distance - followRadius) / distance);
                Vector3 eased = Vector3.SmoothDamp(rigPosition, desired, ref _followVelocity, followSmoothTime, Mathf.Infinity, dt);
                rigPosition.x = eased.x;
                rigPosition.z = eased.z;
            }
            else
            {
                _followVelocity = Vector3.zero;
            }
        }

        // Floor under where the head will be after this frame's horizontal move.
        Vector3 headAfterMove = head.position + Flat(rigPosition - transform.position);
        float floorY = _groundHeight(headAfterMove) + _heightOffset;
        rigPosition.y = snapped
            ? floorY
            : Mathf.SmoothDamp(transform.position.y, floorY, ref _heightVelocity, heightSmoothTime, Mathf.Infinity, dt);

        transform.position = rigPosition;
    }

    /// <summary>
    /// Grab-the-world drag with whichever grip is held. Hand motion is measured in
    /// TrackingSpace-local coordinates so the rig moving under the hand doesn't feed back
    /// into the drag. Returns true while dragging (auto-follow pauses).
    /// </summary>
    private bool UpdateGripDrag(ref Vector3 rigPosition)
    {
        Transform hand = null;
        if (_leftGripAction.IsPressed())
            hand = _rig.leftHandAnchor;
        else if (_rightGripAction.IsPressed())
            hand = _rig.rightHandAnchor;

        if (hand == null)
        {
            _dragHand = null;
            return false;
        }

        Transform trackingSpace = _rig.trackingSpace;
        Vector3 handLocal = trackingSpace.InverseTransformPoint(hand.position);
        if (hand != _dragHand)
        {
            _dragHand = hand;
            _lastDragHandLocal = handLocal;
            return true;
        }

        Vector3 handDelta = trackingSpace.TransformVector(handLocal - _lastDragHandLocal);
        _lastDragHandLocal = handLocal;

        // Pulling the world toward you moves you the opposite way; pushing it down lifts you.
        rigPosition.x -= handDelta.x;
        rigPosition.z -= handDelta.z;
        _heightOffset = Mathf.Clamp(_heightOffset - handDelta.y, 0f, maxHeightOffset);
        _followVelocity = Vector3.zero;
        return true;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
