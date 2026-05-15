using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>
/// Generic component that allows players to look at and interact with buttons 
/// on world space canvases using raycast-based detection.
/// Attach to the player or camera object.
/// </summary>
public class WorldSpaceUIInteractor : MonoBehaviour
{
    [Header("Raycast Settings")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private float interactionRange = 10f;
    [SerializeField] private LayerMask uiLayerMask = -1; // Set to UI layer in inspector
    
    [Header("Input")]
    [SerializeField] private PlayerInputController inputController;
    
    [Header("Debug")]
    [SerializeField] private bool debugRaycast = false;
    [SerializeField] private Color debugRayColor = Color.yellow;

    private Button currentHoveredButton;
    private bool isEnabled = true;

    private void Awake()
    {
        // Auto-find camera if not assigned
        if (playerCamera == null)
        {
            playerCamera = Camera.main;
        }

        // Auto-find input controller if not assigned
        if (inputController == null)
        {
            inputController = GetComponentInParent<PlayerInputController>();
            if (inputController == null)
            {
                inputController = GetComponent<PlayerInputController>();
            }
        }
    }

    private void Update()
    {
        if (!isEnabled) return;

        HandleWorldSpaceButtonInteraction();
    }

    private void HandleWorldSpaceButtonInteraction()
    {
        if (playerCamera == null)
        {
            if (debugRaycast) Debug.LogWarning("WorldSpaceUIInteractor: playerCamera is null!");
            return;
        }

        if (inputController == null)
        {
            if (debugRaycast) Debug.LogWarning("WorldSpaceUIInteractor: inputController is null!");
            return;
        }

        Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
        RaycastHit hit;

        if (debugRaycast)
        {
            Debug.DrawRay(ray.origin, ray.direction * interactionRange, debugRayColor);
        }

        Button hitButton = null;

        // Raycast to detect UI elements
        if (Physics.Raycast(ray, out hit, interactionRange, uiLayerMask))
        {
            if (debugRaycast)
            {
                Debug.Log($"Raycast HIT: {hit.collider.gameObject.name} on layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}");
            }

            // Try to get button component from hit object or its parents
            hitButton = hit.collider.GetComponentInParent<Button>();

            if (debugRaycast && hitButton != null)
            {
                Debug.Log($"Button found: {hitButton.name} (interactable: {hitButton.interactable})");
            }
        }

        // Handle button hover state
        if (hitButton != currentHoveredButton)
        {
            // Unhover previous button
            if (currentHoveredButton != null)
            {
                if (debugRaycast) Debug.Log($"Exiting hover: {currentHoveredButton.name}");
                
                if (EventSystem.current != null)
                {
                    ExecuteEvents.Execute(currentHoveredButton.gameObject, 
                        new PointerEventData(EventSystem.current), 
                        ExecuteEvents.pointerExitHandler);
                }
            }

            currentHoveredButton = hitButton;

            // Hover new button
            if (currentHoveredButton != null && currentHoveredButton.interactable)
            {
                if (debugRaycast) Debug.Log($"Entering hover: {currentHoveredButton.name}");
                
                if (EventSystem.current != null)
                {
                    ExecuteEvents.Execute(currentHoveredButton.gameObject, 
                        new PointerEventData(EventSystem.current), 
                        ExecuteEvents.pointerEnterHandler);
                }
            }
        }

        // Handle button click
        if (currentHoveredButton != null && currentHoveredButton.interactable && inputController.InteractPressedThisFrame)
        {
            if (debugRaycast) Debug.Log($"Button clicked: {currentHoveredButton.name}");
            currentHoveredButton.onClick.Invoke();
        }
    }

    /// <summary>
    /// Enable or disable the interactor. Useful for temporarily disabling during cutscenes, etc.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        isEnabled = enabled;
        
        // Clear hover state when disabled
        if (!enabled && currentHoveredButton != null)
        {
            if (EventSystem.current != null)
            {
                ExecuteEvents.Execute(currentHoveredButton.gameObject, 
                    new PointerEventData(EventSystem.current), 
                    ExecuteEvents.pointerExitHandler);
            }
            currentHoveredButton = null;
        }
    }

    /// <summary>
    /// Manually set the camera reference
    /// </summary>
    public void SetCamera(Camera cam)
    {
        playerCamera = cam;
    }

    /// <summary>
    /// Manually set the input controller reference
    /// </summary>
    public void SetInputController(PlayerInputController input)
    {
        inputController = input;
    }

    private void OnDisable()
    {
        // Clear hover state when component is disabled
        if (currentHoveredButton != null && EventSystem.current != null)
        {
            ExecuteEvents.Execute(currentHoveredButton.gameObject, 
                new PointerEventData(EventSystem.current), 
                ExecuteEvents.pointerExitHandler);
            currentHoveredButton = null;
        }
    }
}