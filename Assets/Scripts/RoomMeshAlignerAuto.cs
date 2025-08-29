using UnityEngine;
using Meta.XR.MRUtilityKit;

public class RoomMeshAlignerAuto : MonoBehaviour
{
    [Tooltip("The GameObject containing your Mesh Effect or MRUKRoomMesh")]
    public Transform meshRoot;

    [Tooltip("If true, will keep aligning every frame (useful if origin can drift)")]
    public bool continuousUpdate = false;

    private MRUKRoom currentRoom;

    void OnEnable()
    {
        // Subscribe to MRUK's room creation event
        MRUK.Instance.RoomCreatedEvent.AddListener(OnRoomCreated);
    }

    void OnDisable()
    {
        // Unsubscribe to avoid memory leaks
        MRUK.Instance.RoomCreatedEvent.RemoveListener(OnRoomCreated);
    }

    private void OnRoomCreated(MRUKRoom newRoom)
    {
        currentRoom = newRoom;

        if (meshRoot == null)
            meshRoot = transform; // default to this object

        AlignMesh();
    }

    void Update()
    {
        if (continuousUpdate && currentRoom != null)
            AlignMesh();
    }

    private void AlignMesh()
    {
        if (currentRoom == null || meshRoot == null)
            return;

        meshRoot.SetPositionAndRotation(currentRoom.transform.position, currentRoom.transform.rotation);
    }
}
