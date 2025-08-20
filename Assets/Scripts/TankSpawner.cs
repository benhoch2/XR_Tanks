using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class TankSpawner : MonoBehaviour
{

    public int numberOfTanksToSpawn = 1;
    public GameObject tankPrefab;
    public float height;

    public List<GameObject> spawnedTanks;

    public int maxSpawnAttempts = 100;


    private int currentSpawnAttempts = 0;

    public static TankSpawner Instance;
    // Start is called before the first frame update
    void Start()
    {
        Instance = this;
        MRUK.Instance.RegisterSceneLoadedCallback(SpawnTanks);
    }

    public virtual void SpawnTanks()
    {
        Debug.Log("Spawning tanks...");
        for (int i = 0; i < numberOfTanksToSpawn; i++)
        {
            Vector3 randomPosition = Vector3.zero;

            MRUKRoom room = MRUK.Instance.GetCurrentRoom();

            while (currentSpawnAttempts < maxSpawnAttempts)
            {
                bool hasFound = room.GenerateRandomPositionOnSurface(MRUK.SurfaceType.FACING_UP, 1, new LabelFilter(MRUKAnchor.SceneLabels.FLOOR), out randomPosition, out Vector3 normal);
                if (hasFound)
                {
                    break; // Exit the loop if a valid position is found
                }
                currentSpawnAttempts++;
            }

            randomPosition.y = height; // Offset the tank above the surface
            Debug.Log($"Spawning tank at position: {randomPosition}");
            GameObject tank = Instantiate(tankPrefab, randomPosition, Quaternion.identity);
            spawnedTanks.Add(tank);
        }
    }

    public void Destroytank(GameObject tank)
    {
        if (spawnedTanks.Contains(tank))
        {
            spawnedTanks.Remove(tank);
            Destroy(tank);
        }

        if (spawnedTanks.Count == 0)
        {
            Debug.Log("All tanks have been destroyed");
            UnityEngine.SceneManagement.SceneManager.LoadScene(0); // Load a game over scene or handle accordingly    
        }
    }
}

