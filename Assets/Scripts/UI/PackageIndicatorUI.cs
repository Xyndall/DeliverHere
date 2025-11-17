using UnityEngine;
using TMPro;
using DeliverHere.Items;
using Unity.Netcode;

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

    [Header("Networking")]
    [Tooltip("When enabled, the UI only updates for the locally owned player.")]
    [SerializeField] private bool requireLocalOwner = true;

    private void Reset()
    {
        if (playerHold == null)
            playerHold = FindAnyObjectByType<PlayerHold>();
        if (weightText == null)
            weightText = GetComponentInChildren<TextMeshProUGUI>();
    }

    private void Awake()
    {
        TryResolveLocalPlayerHold();
    }

    private void OnEnable()
    {
        TryResolveLocalPlayerHold();
        if (weightText != null)
            weightText.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (playerHold == null || weightText == null)
        {
            TryResolveLocalPlayerHold();
            return;
        }

        // Gate by local ownership so only the local player's HUD updates.
        if (requireLocalOwner && !playerHold.IsOwner)
        {
            weightText.gameObject.SetActive(false);
            return;
        }

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

    private void TryResolveLocalPlayerHold()
    {
        if (playerHold != null)
            return;

        // Prefer the local player's PlayerHold via Netcode
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsClient)
        {
            var localObj = nm.SpawnManager?.GetLocalPlayerObject();
            if (localObj != null)
                playerHold = localObj.GetComponent<PlayerHold>();
        }

        // Fallback: pick the owned PlayerHold found in the scene (e.g., host before player object assignment).
        if (playerHold == null)
        {
            var all = FindObjectsByType<PlayerHold>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].IsOwner)
                {
                    playerHold = all[i];
                    break;
                }
            }
        }
    }
}