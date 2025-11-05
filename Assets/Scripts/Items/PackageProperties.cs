using System;
using UnityEngine;
using Unity.Netcode;

namespace DeliverHere.Items
{
    public enum PackageSize : byte
    {
        Small = 0,
        Medium = 1,
        Large = 2,
    }

    [DisallowMultipleComponent]
    public class PackageProperties : NetworkBehaviour
    {
        [Header("Randomization")]
        [Tooltip("Automatically randomize on spawn (server) and on Start when offline.")]
        [SerializeField] private bool autoRandomizeOnSpawn = true;

        [Header("Randomization Ranges")]
        [SerializeField] private float minWeightKg = 1f;
        [SerializeField] private float maxWeightKg = 25f;
        [SerializeField] private int minValue = 10;
        [SerializeField] private int maxValue = 500;
        [SerializeField, Range(0f, 1f)] private float minFragility = 0f;
        [SerializeField, Range(0f, 1f)] private float maxFragility = 1f;

        [Header("Size-based Weight (overrides global min/max for weight)")]
        [Tooltip("If enabled, weight will be sampled from the ranges below based on the chosen size.")]
        [SerializeField] private bool useSizeBasedWeight = true;
        [Tooltip("Weight range (kg) when size is Small.")]
        [SerializeField] private Vector2 smallWeightKgRange = new Vector2(1f, 6f);
        [Tooltip("Weight range (kg) when size is Medium.")]
        [SerializeField] private Vector2 mediumWeightKgRange = new Vector2(6f, 15f);
        [Tooltip("Weight range (kg) when size is Large.")]
        [SerializeField] private Vector2 largeWeightKgRange = new Vector2(15f, 25f);

        [Header("Allowed Options")]
        [SerializeField] private PackageSize[] allowedSizes = new[] { PackageSize.Small, PackageSize.Medium, PackageSize.Large };
        [SerializeField] private Color[] colorPalette = new[] { Color.yellow, Color.red, Color.blue, Color.green, Color.cyan };

        [Header("Visuals")]
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private Vector3 smallScale = new Vector3(0.7f, 0.7f, 0.7f);
        [SerializeField] private Vector3 mediumScale = new Vector3(1f, 1f, 1f);
        [SerializeField] private Vector3 largeScale = new Vector3(1.4f, 1.4f, 1.4f);

        [Header("Randomized Scale Multiplier")]
        [Tooltip("Enable to randomize scale per package based on its size.")]
        [SerializeField] private bool randomizeScale = true;
        [Tooltip("Scale multiplier range when size is Small.")]
        [SerializeField] private Vector2 smallScaleMultiplierRange = new Vector2(0.9f, 1.1f);
        [Tooltip("Scale multiplier range when size is Medium.")]
        [SerializeField] private Vector2 mediumScaleMultiplierRange = new Vector2(0.95f, 1.05f);
        [Tooltip("Scale multiplier range when size is Large.")]
        [SerializeField] private Vector2 largeScaleMultiplierRange = new Vector2(0.9f, 1.15f);

        [Header("Physics")]
        [Tooltip("If not set, will use GetComponent<Rigidbody>() or first child Rigidbody.")]
        [SerializeField] private Rigidbody body;

        // Replicated properties (networked runs)
        public readonly NetworkVariable<float> NetWeightKg = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public readonly NetworkVariable<int> NetValue = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public readonly NetworkVariable<float> NetFragility = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public readonly NetworkVariable<PackageSize> NetSize = new NetworkVariable<PackageSize>(PackageSize.Medium, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public readonly NetworkVariable<byte> NetColorIndex = new NetworkVariable<byte>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        // New: replicated scale multiplier
        public readonly NetworkVariable<float> NetScaleMultiplier = new NetworkVariable<float>(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Local fallback values (offline runs)
        private float localWeightKg;
        private int localValue;
        private float localFragility;
        private PackageSize localSize = PackageSize.Medium;
        private byte localColorIndex;
        private float localScaleMultiplier = 1f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private bool IsNetworked => NetworkManager != null;
        private bool UseNetValues => IsNetworked && (IsServer || IsClient);

        public float WeightKg => UseNetValues ? NetWeightKg.Value : localWeightKg;
        public int Value => UseNetValues ? NetValue.Value : localValue;
        public float Fragility => UseNetValues ? NetFragility.Value : localFragility;
        public PackageSize Size => UseNetValues ? NetSize.Value : localSize;
        private float ScaleMultiplier => UseNetValues ? NetScaleMultiplier.Value : localScaleMultiplier;
        public Color Color
        {
            get
            {
                var idx = UseNetValues ? NetColorIndex.Value : localColorIndex;
                return (colorPalette != null && colorPalette.Length > idx) ? colorPalette[idx] : Color.white;
            }
        }

        private void Awake()
        {
            if (body == null)
            {
                body = GetComponent<Rigidbody>() ?? GetComponentInChildren<Rigidbody>(true);
            }
            if (targetRenderer == null)
            {
                targetRenderer = GetComponent<Renderer>() ?? GetComponentInChildren<Renderer>(true);
            }
        }

        private void Start()
        {
            // Offline: randomize on Start so instances get unique values immediately.
            if (!IsNetworked && autoRandomizeOnSpawn)
            {
                ServerRandomize();
            }
            else
            {
                ApplyAll(); // ensure visuals/physics reflect current values
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Apply visuals/physics when values change (clients included)
            NetWeightKg.OnValueChanged += (_, __) => ApplyAll();
            NetValue.OnValueChanged += (_, __) => ApplyAll();
            NetFragility.OnValueChanged += (_, __) => ApplyAll();
            NetSize.OnValueChanged += (_, __) => ApplyAll();
            NetColorIndex.OnValueChanged += (_, __) => ApplyAll();
            NetScaleMultiplier.OnValueChanged += (_, __) => ApplyAll();

            // Server: randomize AFTER spawn so NetworkVariables are initialized and replicate correctly
            if (IsServer && autoRandomizeOnSpawn)
            {
                ServerRandomize();
            }
            else
            {
                ApplyAll();
            }
        }

        [ContextMenu("Server Randomize (Play Mode)")]
        public void ServerRandomize()
        {
            bool isOffline = !IsNetworked;
            if (!isOffline && !IsServer)
                return;

            // Pick size first
            var size = allowedSizes != null && allowedSizes.Length > 0
                ? allowedSizes[UnityEngine.Random.Range(0, allowedSizes.Length)]
                : PackageSize.Medium;

            // Weight depends on size
            var weight = RandomWeightForSize(size);

            // Scale multiplier depends on size (optional)
            var scaleMult = randomizeScale ? RandomScaleMultiplierForSize(size) : 1f;

            // Other properties
            var value = UnityEngine.Random.Range(Mathf.Min(minValue, maxValue), Mathf.Max(minValue, maxValue) + 1);
            var fragility = UnityEngine.Random.Range(Mathf.Min(minFragility, maxFragility), Mathf.Max(minFragility, maxFragility));

            byte colorIndex = 0;
            if (colorPalette != null && colorPalette.Length > 0)
            {
                colorIndex = (byte)UnityEngine.Random.Range(0, Mathf.Min(255, colorPalette.Length));
            }

            if (isOffline)
            {
                localWeightKg = weight;
                localValue = value;
                localFragility = fragility;
                localSize = size;
                localColorIndex = colorIndex;
                localScaleMultiplier = Mathf.Max(0.01f, scaleMult);
            }
            else
            {
                NetWeightKg.Value = weight;
                NetValue.Value = value;
                NetFragility.Value = fragility;
                NetSize.Value = size;
                NetColorIndex.Value = colorIndex;
                NetScaleMultiplier.Value = Mathf.Max(0.01f, scaleMult);
            }

            ApplyAll(weight, value, fragility, size, colorIndex, Mathf.Max(0.01f, scaleMult));
        }

        private float RandomWeightForSize(PackageSize size)
        {
            float min = minWeightKg, max = maxWeightKg;

            if (useSizeBasedWeight)
            {
                switch (size)
                {
                    case PackageSize.Small:
                        min = Mathf.Max(minWeightKg, Mathf.Min(smallWeightKgRange.x, smallWeightKgRange.y));
                        max = Mathf.Min(maxWeightKg, Mathf.Max(smallWeightKgRange.x, smallWeightKgRange.y));
                        break;
                    case PackageSize.Medium:
                        min = Mathf.Max(minWeightKg, Mathf.Min(mediumWeightKgRange.x, mediumWeightKgRange.y));
                        max = Mathf.Min(maxWeightKg, Mathf.Max(mediumWeightKgRange.x, mediumWeightKgRange.y));
                        break;
                    case PackageSize.Large:
                        min = Mathf.Max(minWeightKg, Mathf.Min(largeWeightKgRange.x, largeWeightKgRange.y));
                        max = Mathf.Min(maxWeightKg, Mathf.Max(largeWeightKgRange.x, largeWeightKgRange.y));
                        break;
                }
            }

            if (max < min) max = min;
            return UnityEngine.Random.Range(min, max);
        }

        private float RandomScaleMultiplierForSize(PackageSize size)
        {
            Vector2 range = new Vector2(1f, 1f);
            switch (size)
            {
                case PackageSize.Small:
                    range = smallScaleMultiplierRange;
                    break;
                case PackageSize.Medium:
                    range = mediumScaleMultiplierRange;
                    break;
                case PackageSize.Large:
                    range = largeScaleMultiplierRange;
                    break;
            }
            var min = Mathf.Max(0.01f, Mathf.Min(range.x, range.y));
            var max = Mathf.Max(min, Mathf.Max(range.x, range.y));
            return UnityEngine.Random.Range(min, max);
        }

        public int GetDeliveryReward() => Value;

        private void ApplyAll()
        {
            ApplyAll(WeightKg, Value, Fragility, Size, UseNetValues ? NetColorIndex.Value : localColorIndex, ScaleMultiplier);
        }

        private void ApplyAll(float weight, int value, float fragility, PackageSize size, byte colorIndex, float scaleMult)
        {
            ApplyVisuals(size, colorIndex, scaleMult);
            ApplyPhysics(weight);
        }

        private void ApplyVisuals(PackageSize size, byte colorIndex, float scaleMult)
        {
            Vector3 baseScale;
            switch (size)
            {
                case PackageSize.Small:  baseScale = smallScale;  break;
                case PackageSize.Medium: baseScale = mediumScale; break;
                case PackageSize.Large:  baseScale = largeScale;  break;
                default:                 baseScale = mediumScale; break;
            }

            transform.localScale = baseScale * Mathf.Max(0.01f, scaleMult);

            if (targetRenderer != null && colorPalette != null && colorPalette.Length > colorIndex)
            {
                var color = colorPalette[colorIndex];
                var mpb = new MaterialPropertyBlock();
                targetRenderer.GetPropertyBlock(mpb);

                var mat = targetRenderer.sharedMaterial;
                if (mat != null && mat.HasProperty(BaseColorId))
                {
                    mpb.SetColor(BaseColorId, color);
                }
                else
                {
                    mpb.SetColor(ColorId, color);
                }
                targetRenderer.SetPropertyBlock(mpb);
            }
        }

        private void ApplyPhysics(float weight)
        {
            if (body != null)
            {
                body.mass = Mathf.Max(0.01f, weight);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (maxWeightKg < minWeightKg) maxWeightKg = minWeightKg;
            if (maxValue < minValue) maxValue = minValue;
            minFragility = Mathf.Clamp01(minFragility);
            maxFragility = Mathf.Clamp01(maxFragility);
            if (maxFragility < minFragility) maxFragility = minFragility;

            // Keep size ranges sensible and within global bounds
            smallWeightKgRange.x = Mathf.Clamp(smallWeightKgRange.x, minWeightKg, maxWeightKg);
            smallWeightKgRange.y = Mathf.Clamp(smallWeightKgRange.y, smallWeightKgRange.x, maxWeightKg);

            mediumWeightKgRange.x = Mathf.Clamp(mediumWeightKgRange.x, minWeightKg, maxWeightKg);
            mediumWeightKgRange.y = Mathf.Clamp(mediumWeightKgRange.y, mediumWeightKgRange.x, maxWeightKg);

            largeWeightKgRange.x = Mathf.Clamp(largeWeightKgRange.x, minWeightKg, maxWeightKg);
            largeWeightKgRange.y = Mathf.Clamp(largeWeightKgRange.y, largeWeightKgRange.x, maxWeightKg);

            // Clamp scale multiplier ranges > 0
            smallScaleMultiplierRange.x = Mathf.Max(0.01f, smallScaleMultiplierRange.x);
            smallScaleMultiplierRange.y = Mathf.Max(smallScaleMultiplierRange.x, smallScaleMultiplierRange.y);

            mediumScaleMultiplierRange.x = Mathf.Max(0.01f, mediumScaleMultiplierRange.x);
            mediumScaleMultiplierRange.y = Mathf.Max(mediumScaleMultiplierRange.x, mediumScaleMultiplierRange.y);

            largeScaleMultiplierRange.x = Mathf.Max(0.01f, largeScaleMultiplierRange.x);
            largeScaleMultiplierRange.y = Mathf.Max(largeScaleMultiplierRange.x, largeScaleMultiplierRange.y);
        }
#endif
    }
}