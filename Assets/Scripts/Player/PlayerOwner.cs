using UnityEngine;
using Unity.Netcode;

[DisallowMultipleComponent]
public class PlayerOwner : NetworkBehaviour
{
    [Header("Local-Only Visibility")]
    [Tooltip("Root of the body mesh that should be hidden for the local player only.")]
    [SerializeField] private GameObject bodyRoot;

    private Renderer[] _bodyRenderers;

    private void Awake()
    {
        if (bodyRoot != null)
        {
            _bodyRenderers = bodyRoot.GetComponentsInChildren<Renderer>(true);
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // Only the owning client should hide their own body
        if (IsOwner)
        {
            SetBodyVisible(false);
        }
        else
        {
            // Ensure remote players always see the body
            SetBodyVisible(true);
        }
    }

    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        SetBodyVisible(false);
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        SetBodyVisible(true);
    }

    private void SetBodyVisible(bool visible)
    {
        if (_bodyRenderers == null || _bodyRenderers.Length == 0) return;

        for (int i = 0; i < _bodyRenderers.Length; i++)
        {
            _bodyRenderers[i].enabled = visible;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (bodyRoot != null && (_bodyRenderers == null || _bodyRenderers.Length == 0))
        {
            _bodyRenderers = bodyRoot.GetComponentsInChildren<Renderer>(true);
        }
    }
#endif
}
