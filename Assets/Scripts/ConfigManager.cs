using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.SceneManagement; // <-- added

public class ConfigManager : MonoBehaviour
{
    [SerializeField] private GameObject configCanvas;
    [SerializeField] private FindSpawnPositions enemyTankSpawner;
    [SerializeField] private FindSpawnPositions crateSpawner;

    [SerializeField] private SliderPanelCfg numberOfEnemiesCFG;
    [SerializeField] private SliderPanelCfg numberOfCratesCFG;
    [SerializeField] private SliderPanelCfg loadSecondsCFG;

    // Start is called before the first frame update
    void Awake()
    {
        Debug.Log("ConfigManager started");
        if (enemyTankSpawner != null)
        {
            enemyTankSpawner.SpawnAmount = GameConfig.numberOfEnemies;
        }

        if (crateSpawner != null)
        {
            crateSpawner.SpawnAmount = GameConfig.numberOfCrates;
        }
        
    }

    void Start()
    {
        if (numberOfEnemiesCFG != null)
            numberOfEnemiesCFG.SetValue(GameConfig.numberOfEnemies);

        if (numberOfCratesCFG != null)
            numberOfCratesCFG.SetValue(GameConfig.numberOfCrates);

        if (loadSecondsCFG != null)
            loadSecondsCFG.SetValue(GameConfig.powerUpDuration);
    }

    // Update is called once per frame
    void Update()
    {

    }

    // Public method to reload the currently active scene
    public void ReloadScene()
    {
        Debug.Log("Reloading scene...");
        Scene active = SceneManager.GetActiveScene();
        SceneManager.LoadScene(active.name);
    }

    public void CancelButton()
    {
        Debug.Log("Cancel button pressed");
        configCanvas.SetActive(false);
    }

    public void SaveButton()
    {
        Debug.Log("Save button pressed");
        GameConfig.numberOfEnemies = numberOfEnemiesCFG.GetValue();
        GameConfig.numberOfCrates = numberOfCratesCFG.GetValue();
        GameConfig.powerUpDuration = loadSecondsCFG.GetValue();
    }
}
