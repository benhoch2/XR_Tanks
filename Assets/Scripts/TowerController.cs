using UnityEngine;
using UnityEngine.XR;
using UnityEngine.InputSystem; // ✅ New Input System

public class TowerControllerSimple : MonoBehaviour
{
    [Header("Tower Rotation Settings")]
    [SerializeField] GameObject towerBase;
    public float rotationSpeed = 100f;
    public float tiltSpeed = 50f;
    public float minTilt = -10f;
    public float maxTilt = 30f;

    [Header("Tank Movement Settings")]
    public float moveSpeed = 5f;

    private float currentTilt = 0f;

    void Update()
    {
        // --- VR joystick input ---
        Vector2 leftStick = Vector2.zero;
        Vector2 rightStick = Vector2.zero;


        // After (fully qualified)
        InputDevices.GetDeviceAtXRNode(XRNode.LeftHand)
            .TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out leftStick);

        InputDevices.GetDeviceAtXRNode(XRNode.RightHand)
            .TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out rightStick);


        // --- Keyboard fallback (new Input System) ---
        float moveInput = leftStick.y;
        float turnInput = leftStick.x;
        float rotationInput = rightStick.x;
        float tiltInput = rightStick.y;

        // WASD for movement
        if (Keyboard.current != null) {
            if (Keyboard.current.wKey.isPressed) moveInput += 1f;
            if (Keyboard.current.sKey.isPressed) moveInput -= 1f;
            if (Keyboard.current.aKey.isPressed) turnInput -= 1f;
            if (Keyboard.current.dKey.isPressed) turnInput += 1f;

            // Arrows for turret control
            if (Keyboard.current.leftArrowKey.isPressed) rotationInput -= 1f;
            if (Keyboard.current.rightArrowKey.isPressed) rotationInput += 1f;
            if (Keyboard.current.upArrowKey.isPressed) tiltInput += 1f;
            if (Keyboard.current.downArrowKey.isPressed) tiltInput -= 1f;
        }

        MoveTank(moveInput, turnInput);
        RotateTower(rotationInput);
        TiltTower(tiltInput);
    }

    void MoveTank(float moveInput, float turnInput)
    {
        if (Mathf.Abs(turnInput) > 0.01f)
            transform.Rotate(Vector3.up, turnInput * rotationSpeed * Time.deltaTime);
        if (Mathf.Abs(moveInput) > 0.01f)
            transform.position += transform.forward * moveInput * moveSpeed * Time.deltaTime;
    }

    void RotateTower(float input)
    {
        if (Mathf.Abs(input) > 0.01f)
            towerBase.transform.Rotate(Vector3.up, input * rotationSpeed * Time.deltaTime, Space.World);
    }

    void TiltTower(float input)
    {
        if (Mathf.Abs(input) > 0.01f)
        {
            currentTilt = Mathf.Clamp(currentTilt + input * tiltSpeed * Time.deltaTime, minTilt, maxTilt);
            Vector3 localEuler = towerBase.transform.localEulerAngles;
            localEuler.x = currentTilt;
            towerBase.transform.localEulerAngles = localEuler;
        }
    }
}
