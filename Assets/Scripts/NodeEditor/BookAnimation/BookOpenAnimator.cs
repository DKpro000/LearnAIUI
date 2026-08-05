using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class BookOpenAnimator : MonoBehaviour
{
    [Header("Book animation frames, in order: closed cover -> fully open")]
    public Image bookImage;
    public Sprite closedCoverSprite;
    public Sprite[] openFrames;
    public float frameDelay = 0.04f;

    [Header("Sprite to stay visible as the backdrop once opened")]
    public Sprite openRestingSprite;

    [Header("Content to reveal once the book finishes opening")]
    public GameObject[] contentToReveal;

    [Header("Cover-only elements to hide once opened (title, click button)")]
    public GameObject bookCoverTitle;
    public Button coverButton;

    private bool isOpen = false;
    private bool isAnimating = false;

    public void OnBookClicked()
    {
        if (isOpen || isAnimating)
        {
            return;
        }

        StartCoroutine(PlayOpenAnimation());
    }

    private IEnumerator PlayOpenAnimation()
    {
        isAnimating = true;

        foreach (Sprite frame in openFrames)
        {
            bookImage.sprite = frame;
            yield return new WaitForSeconds(frameDelay);
        }

        if (openRestingSprite != null)
        {
            bookImage.sprite = openRestingSprite;
        }

        foreach (GameObject obj in contentToReveal)
        {
            if (obj != null)
            {
                obj.SetActive(true);
            }
        }
        
        if (bookCoverGroupTransform != null)
        {
            bookCoverGroupTransform.SetAsFirstSibling();
        }

        if (bookCoverTitle != null)
        {
            bookCoverTitle.SetActive(false);
        }

        if (coverButton != null)
        {
            coverButton.interactable = false;
        }

        isOpen = true;
        isAnimating = false;
    }

    public void ResetToClosedState()
    {
        isOpen = false;

        if (bookImage != null && closedCoverSprite != null)
        {
            bookImage.sprite = closedCoverSprite;
        }

        foreach (GameObject obj in contentToReveal)
        {
            if (obj != null)
            {
                obj.SetActive(false);
            }
        }

        if (bookCoverTitle != null)
        {
            bookCoverTitle.SetActive(true);
        }

        if (coverButton != null)
        {
            coverButton.interactable = true;
        }

        if (bookCoverGroupTransform != null)
        {
            bookCoverGroupTransform.SetAsLastSibling();
        }       
    }

    [Header("The book cover's own container (for reordering behind content)")]
    public Transform bookCoverGroupTransform;
}