using System.IO;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class PickingGame : MonoBehaviour
{
    [Header("Canvas settings")]
    public GameObject day1Canvas;
    public Dialogue GuideDialue;
    public GraphEditorController graphController;
    public Image MixedDogBox;
    public Image MixedMuffinBox;
    public GameObject coverBG;

    // file path
    private string projectRoot;

    void Start()
    {
        projectRoot = Directory.GetParent(Application.dataPath).FullName;
    }
    public void ClickingBox()
    {
        StartCoroutine(ClickingBoxRoutine());
    }

    private IEnumerator ClickingBoxRoutine()
    {
        MixedDogBox.gameObject.SetActive(true);
        coverBG.SetActive(true);

        // Wait for 1 second
        yield return new WaitForSeconds(2f);

        GuideDialue.gameObject.SetActive(true);
    }

    public void turnoff()
    {
        MixedDogBox.gameObject.SetActive(false);
        MixedMuffinBox.gameObject.SetActive(false);
        coverBG.SetActive(false);
    }

    public void DogVsMuffin()
    {
        day1Canvas.SetActive(true);
        string MuffinFile = Path.Combine(projectRoot, "DogVsMuffin.json");
        graphController.LoadGraphFromFile(MuffinFile);
    }
}
