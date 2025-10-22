using UnityEngine;
using UnityEngine.UI;

public class MenuUIController : MonoBehaviour
{
    [Header("Menu UI")]
    [SerializeField] private GameObject menuRoot; // Optional: assign to hide menu after start
    [SerializeField] private Button startGameButton;
    [SerializeField] private Button endGameButton;

    private void Awake()
    {
        // Ensure cursor is visible in menu
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // Wire up buttons via AddListener
        if (startGameButton != null)
            startGameButton.onClick.AddListener(OnStartGameClicked);

        if (endGameButton != null)
            endGameButton.onClick.AddListener(OnEndGameClicked);
    }

    private void OnDestroy()
    {
        if (startGameButton != null)
            startGameButton.onClick.RemoveListener(OnStartGameClicked);

        if (endGameButton != null)
            endGameButton.onClick.RemoveListener(OnEndGameClicked);
    }

    private void OnStartGameClicked()
    {
        GameManager.Instance?.StartGame();
        if (menuRoot != null) menuRoot.SetActive(false);
    }

    private void OnEndGameClicked()
    {
        GameManager.Instance?.EndGame();
        if (menuRoot != null) menuRoot.SetActive(true);

        // Show cursor again when returning to menu
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
