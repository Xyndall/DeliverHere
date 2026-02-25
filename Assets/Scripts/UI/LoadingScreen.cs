using UnityEngine;
using TMPro;

public class LoadingScreen : MonoBehaviour
{
    [Header("Optional")]
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private RectTransform spinner;
    [SerializeField] private float spinnerSpeed = 180f; // degrees per second

    private void OnEnable()
    {
        if (statusText != null)
            statusText.text = "Loading...";
    }

    private void Update()
    {
        if (spinner != null)
        {
            spinner.Rotate(0f, 0f, -spinnerSpeed * Time.unscaledDeltaTime);
        }
    }
}
