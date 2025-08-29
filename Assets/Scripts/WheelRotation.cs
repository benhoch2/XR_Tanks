using UnityEngine;

public class WheelRotation : MonoBehaviour
{
    [Tooltip("The transform of the tank body or parent object that moves.")]
    public Transform tankBody;

    [Tooltip("If true, radius will be calculated automatically from the mesh bounds.")]
    public bool autoCalculateRadius = true;

    [Tooltip("Radius of the wheel in meters (used if autoCalculateRadius is false).")]
    public float wheelRadius = 0.5f;

    private Vector3 lastTankPosition;

    void Start()
    {
        if (tankBody == null)
        {
            Debug.LogError("Tank body not assigned to WheelRotation script on " + gameObject.name);
            enabled = false;
            return;
        }

        if (autoCalculateRadius)
        {
            wheelRadius = CalculateWheelRadiusFromMesh();
            Debug.Log($"{gameObject.name} auto-calculated radius: {wheelRadius:F3} meters");
        }

        lastTankPosition = tankBody.position;
    }

    void Update()
    {
        Vector3 delta = tankBody.position - lastTankPosition;
        float forwardMovement = Vector3.Dot(delta, tankBody.forward);

        float rotationDegrees = (forwardMovement / (2 * Mathf.PI * wheelRadius)) * 360f;
        transform.Rotate(Vector3.right, rotationDegrees, Space.Self);

        lastTankPosition = tankBody.position;
    }

    private float CalculateWheelRadiusFromMesh()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            // Bounds are in local space, so apply local scale
            float diameter = mf.sharedMesh.bounds.size.y * transform.localScale.y;
            return diameter / 2f;
        }

        Debug.LogWarning($"No MeshFilter found on {gameObject.name}, using default radius.");
        return wheelRadius;
    }
}
