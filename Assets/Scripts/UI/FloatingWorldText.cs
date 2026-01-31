using UnityEngine;
using TMPro;

[RequireComponent(typeof(TextMeshPro))]
public class FloatingWorldText : MonoBehaviour
{
    [Header("Motion")]
    [SerializeField] private Vector3 worldVelocity = new Vector3(0f, 0.75f, 0f);
    [SerializeField] private float lifetimeSeconds = 1.25f;
    [SerializeField] private float fadeStartSeconds = 0.6f;

    [Header("Appearance")]
    [SerializeField] private Color startColor = Color.red;
    [SerializeField] private float startScale = 0.35f;

    private TextMeshPro _tmp;
    private float _spawnTime;
    private float _alpha = 1f;

    private void Awake()
    {
        _tmp = GetComponent<TextMeshPro>();
        if (_tmp != null)
        {
            _tmp.color = startColor;
            transform.localScale = Vector3.one * startScale;
        }
    }

    private void OnEnable()
    {
        _spawnTime = Time.time;
    }

    private void Update()
    {
        // Billboard towards main camera
        if (Camera.main != null)
        {
            transform.rotation = Quaternion.LookRotation(
                transform.position - Camera.main.transform.position, Vector3.up);
        }

        // Upward drift
        transform.position += worldVelocity * Time.deltaTime;

        float elapsed = Time.time - _spawnTime;

        // Fade out after fadeStartSeconds
        if (elapsed >= fadeStartSeconds && _tmp != null)
        {
            float t = Mathf.InverseLerp(fadeStartSeconds, lifetimeSeconds, elapsed);
            _alpha = Mathf.Lerp(1f, 0f, t);
            var c = _tmp.color;
            c.a = _alpha;
            _tmp.color = c;
        }

        if (elapsed >= lifetimeSeconds)
        {
            Destroy(gameObject);
        }
    }

    private void LateUpdate()
    {
        if (Camera.main != null)
        {
            // Make the text face the camera, adjusting for potential backward-facing issues
            transform.LookAt(transform.position + Camera.main.transform.rotation * Vector3.forward,
                             Camera.main.transform.rotation * Vector3.up);
        }
    }

    public void SetText(string text, Color? colorOverride = null)
    {
        if (_tmp == null) return;
        _tmp.text = text;
        if (colorOverride.HasValue)
        {
            var c = colorOverride.Value;
            c.a = _alpha;
            _tmp.color = c;
        }
    }

    public static FloatingWorldText Spawn(FloatingWorldText prefab, Transform parent, Vector3 localPosition, string text, Color? colorOverride = null)
    {
        if (prefab == null) return null;
        var inst = Instantiate(prefab, parent);
        inst.transform.localPosition = localPosition;
        inst.transform.localRotation = Quaternion.identity;
        inst.SetText(text, colorOverride);
        return inst;
    }
}