using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class NumberOfEnemiesCFG : MonoBehaviour
{
    public Slider slider;
    public TextMeshProUGUI valueText;

    void Start()
    {
        if (slider == null)
            return;
        // Initialize text with current slider value
        UpdateText(slider.value);

        // Subscribe to slider value changes
        slider.onValueChanged.AddListener(UpdateText);
    }

    void UpdateText(float value)
    {
        // Cast to int since slider is whole numbers
        int intValue = Mathf.RoundToInt(value);
        valueText.text = intValue.ToString();
    }

    public void SetValue(int value)
    {
        Debug.Log($"NumberOfEnemiesCFG setting slider value to {value}");
        slider.value = value;
        UpdateText(value);
    }

    public int GetValue()
    {
        return Mathf.RoundToInt(slider.value);
    }
}
