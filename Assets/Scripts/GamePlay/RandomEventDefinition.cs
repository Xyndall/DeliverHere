using UnityEngine;

[CreateAssetMenu(fileName = "RandomEventDefinition", menuName = "DeliverHere/Random Event Definition")]
public class RandomEventDefinition : ScriptableObject
{
    [Header("Identity")]
    public string eventId = "EventId";
    [TextArea] public string description;

    [Header("Chance & Limits")]
    [Tooltip("Base chance per evaluation tick (0-1).")]
    [Range(0f, 1f)] public float baseChance = 0.1f;

    [Tooltip("Max number of times this event can trigger within a single day.")]
    public int maxTriggersPerDay = 1;

    [Tooltip("Cooldown in seconds after triggering before it can trigger again (within the same day).")]
    public float cooldownSeconds = 30f;

    [Header("Timing Window within the day")]
    [Tooltip("Normalized start of the window within the day [0..1], where 0 is day start and 1 is day end.")]
    [Range(0f, 1f)] public float windowStart01 = 0f;

    [Tooltip("Normalized end of the window within the day [0..1].")]
    [Range(0f, 1f)] public float windowEnd01 = 1f;

    [Header("Evaluation")]
    [Tooltip("How often (seconds) to evaluate chance for this event during its window.")]
    public float evaluationIntervalSeconds = 5f;

    [Header("Payload")]
    [Tooltip("Prefab or handler to invoke when event fires (optional).")]
    public GameObject eventPrefab;
}