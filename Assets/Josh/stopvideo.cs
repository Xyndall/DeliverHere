using UnityEngine;
using UnityEngine.Video;
using UnityEngine.UI;

public class MainMenuVideo : MonoBehaviour
{
    public VideoPlayer videoPlayer;
    public RawImage videoRawImage;

    public void StopMenuVideo()
    {
        if (videoPlayer != null)
        {
            videoPlayer.Stop();
            // Clear the RawImage to prevent last frame showing
            if (videoRawImage != null)
            {
                videoRawImage.texture = null;
            }
        }
    }
}