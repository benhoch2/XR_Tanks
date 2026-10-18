using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Describes the world a scene is played in: the MRUK room (<see cref="MRRoomEnvironment"/>)
/// or the procedural VR terrain (<see cref="TerrainEnvironment"/>). GameConfigManager is a
/// DontDestroyOnLoad singleton shared by every scene, so everything that differs between
/// those worlds lives here, on one component per scene, instead of on the manager.
///
/// GameConfigManager drives the startup in this order:
/// <see cref="ApplySpawnCounts"/> → <see cref="PrepareWorld"/> → runtime NavMesh build →
/// enemy configuration starts → <see cref="SpawnActors"/>.
/// The two coroutines run on the environment itself so they die with the scene.
/// </summary>
public abstract class GameEnvironment : MonoBehaviour
{
    /// <summary>
    /// True to hide the Guardian boundary. Meta only allows this while passthrough is
    /// visible, so a pure-VR world must leave the boundary on.
    /// </summary>
    public virtual bool SuppressBoundary => false;

    /// <summary>
    /// Called synchronously during scene startup, before any frame elapses — early enough
    /// for spawners that fire on their own (MRUK <c>SpawnOnStart</c>).
    /// </summary>
    public virtual void ApplySpawnCounts(int enemies, int crates) { }

    /// <summary>Waits for or builds the playable world. The NavMesh is built right after.</summary>
    public abstract IEnumerator PrepareWorld();

    /// <summary>Root whose colliders feed the runtime NavMesh. Null = every collider in the scene.</summary>
    public abstract Transform NavMeshSourceRoot { get; }

    /// <summary>Explicit NavMesh build volume. Null = encapsulate the source colliders.</summary>
    public virtual Bounds? NavMeshBounds => null;

    /// <summary>Adjusts the tank-scale NavMesh settings GameConfigManager starts from.</summary>
    public virtual void TuneNavMesh(ref NavMeshBuildSettings settings) { }

    /// <summary>Spawns the player tank (and, where the world doesn't do it itself, enemies and crates).</summary>
    public abstract IEnumerator SpawnActors();

    /// <summary>Moves <paramref name="obj"/> vertically so its collider's bottom sits <paramref name="clearance"/> above <paramref name="groundY"/>.</summary>
    protected static void SnapBottomToHeight(GameObject obj, float groundY, float clearance = 0.01f)
    {
        Collider col = obj.GetComponentInChildren<Collider>();
        if (col == null)
            return;

        // Collider bounds lag behind transform writes made earlier this frame (e.g. a fresh Instantiate).
        Physics.SyncTransforms();
        float offsetY = (groundY + clearance) - col.bounds.min.y;
        if (Mathf.Abs(offsetY) > 0.001f)
            obj.transform.position += Vector3.up * offsetY;
    }

    /// <summary>Yaws <paramref name="t"/> upright to face <paramref name="forward"/> projected onto the horizontal plane.</summary>
    protected static void FaceHorizontally(Transform t, Vector3 forward)
    {
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.001f)
            t.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }
}
