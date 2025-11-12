using UnityEngine;
using TMPro;
using DeliverHere.Items;

public class PackageIndicatorUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerHold playerHold;
    [SerializeField] private TextMeshProUGUI weightText;

    [Header("Behavior")]
    [SerializeField] private bool hideWhenNotHolding = true;
    [SerializeField] private string weightOnlyFormat = "{0:0.0} kg";
    [SerializeField] private string weightAndValueFormat = "{0:0.0} kg  |  ${1}";

    [Header("Optional Color By Heaviness")]
    [Tooltip("0 = light, 1 = heavy (uses PlayerHold.ControlPenalty01).")]
    [SerializeField] private Color lightColor = Color.white;
    [SerializeField] private Color heavyColor = Color.red;
    [SerializeField] private bool colorizeByPenalty = true;

    private void Reset()
    {
        if (playerHold == null)
            playerHold = FindAnyObjectByType<PlayerHold>();
        if (weightText == null)
            weightText = GetComponentInChildren<TextMeshProUGUI>();
    }

    private void Update()
    {
        if (playerHold == null || weightText == null)
            return;

        if (!playerHold.IsHolding || playerHold.HeldMass == null)
        {
            if (hideWhenNotHolding)
                weightText.gameObject.SetActive(false);
            else
            {
                weightText.gameObject.SetActive(true);
                weightText.text = "";
            }
            return;
        }

        weightText.gameObject.SetActive(true);

        float mass = playerHold.HeldMass.Value;

        // Try to read value from a held package (if any)
        int? value = null;
        var rb = playerHold.HeldBody;
        if (rb != null)
        {
            var pkg = rb.GetComponent<PackageProperties>();
            if (pkg != null)
                value = pkg.Value;
        }

        weightText.text = value.HasValue
            ? string.Format(weightAndValueFormat, mass, value.Value)
            : string.Format(weightOnlyFormat, mass);

        if (colorizeByPenalty)
        {
            float p = Mathf.Clamp01(playerHold.ControlPenalty01);
            weightText.color = Color.Lerp(lightColor, heavyColor, p);
        }
    }
}