using UnityEngine;
using TMPro;
using System.Collections.Generic;

[RequireComponent(typeof(TextMeshPro))]
public class FloatingWorldText : MonoBehaviour
{
    [Header("Motion")]
    [SerializeField] private Vector3 worldVelocity = new Vector3(0f, 0.75f, 0f);
    [SerializeField] private float lifetimeSeconds = 1.25f;
    [SerializeField] private float fadeStartSeconds = 0.6f;
    [SerializeField] private Vector3 spawnUpOffset = new Vector3(0f, 1f, 0f);

    [Header("Appearance")]
    [SerializeField] private Color startColor = Color.red;
    [SerializeField] private float startScale = 0.35f;

    private const int MaxPoolSize = 200;

    private TextMeshPro _tmp;
    private float _spawnTime;
    private float _alpha = 1f;

    private Transform _followTarget;
    private Vector3 _localOffset;
    private Vector3 _driftOffset;

    private static Transform _poolRoot;
    private static readonly Stack<FloatingWorldText> _pool = new();

    private static Transform PoolRoot
    {
        get
        {
            if (_poolRoot == null)
            {
                var go = new GameObject("FloatingWorldText_Pool");
                Object.DontDestroyOnLoad(go);
                _poolRoot = go.transform;
            }
            return _poolRoot;
        }
    }

    private void Awake()
    {
        _tmp = GetComponent<TextMeshPro>();
    }

    private void OnEnable()
    {
        _spawnTime = Time.time;
        _alpha = 1f;
        _driftOffset = spawnUpOffset;

        if (_tmp != null)
        {
            _tmp.color = startColor;
            transform.localScale = Vector3.one * startScale;
        }
    }

    private void Update()
    {
        var cam = Camera.main;

        // Follow target in world space (no parenting), plus upward drift.
        _driftOffset += worldVelocity * Time.deltaTime;

        if (_followTarget != null)
        {
            transform.position = _followTarget.TransformPoint(_localOffset) + _driftOffset;
        }
        else
        {
            transform.position += worldVelocity * Time.deltaTime;
        }

        if (cam != null)
        {
            transform.rotation = Quaternion.LookRotation(
                transform.position - cam.transform.position, Vector3.up);
        }

        float elapsed = Time.time - _spawnTime;

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
            Release();
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

    private void Release()
    {
        _followTarget = null;

        if (_pool.Count >= MaxPoolSize)
        {
            Destroy(gameObject);
            return;
        }

        gameObject.SetActive(false);
        _pool.Push(this);
    }

    /// <param name="followTarget">Transform to track (e.g. the package). Pass null for a static spawn point.</param>
    /// <param name="localOffset">Offset in followTarget's local space, applied each frame.</param>
    public static FloatingWorldText Spawn(FloatingWorldText prefab, Transform followTarget, Vector3 localOffset, string text, Color? colorOverride = null)
    {
        if (prefab == null) return null;

        FloatingWorldText inst;
        if (_pool.Count > 0)
        {
            inst = _pool.Pop();
            inst.gameObject.SetActive(true);
        }
        else
        {
            inst = Instantiate(prefab, PoolRoot);
        }

        inst._followTarget = followTarget;
        inst._localOffset = localOffset;
        inst._driftOffset = inst.spawnUpOffset;
        inst.transform.position = (followTarget != null
            ? followTarget.TransformPoint(localOffset)
            : localOffset) + inst.spawnUpOffset;
        inst.transform.localRotation = Quaternion.identity;
        inst.SetText(text, colorOverride);
        return inst;
    }
}