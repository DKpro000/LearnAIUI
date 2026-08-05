using UnityEngine;
using UnityEngine.UI;
 
public class GameModeSelector : MonoBehaviour
{
    [Tooltip("Drag Level, Sandbox, Interview (in the order you want to cycle) here.")]
    [SerializeField] private GameObject[] panels;
 
    [Tooltip("If true, cycling wraps around (last -> first, first -> last).")]
    [SerializeField] private bool wrapAround = true;
 
    private int currentIndex = 0;
 
    private void OnEnable()
    {
        ShowOnly(currentIndex);
    }
 
    public void Next()
    {
        if (panels == null || panels.Length == 0) return;
 
        int newIndex = currentIndex + 1;
 
        if (newIndex >= panels.Length)
        {
            if (!wrapAround) return;
            newIndex = 0;
        }
 
        ShowOnly(newIndex);
    }
 
    public void Prev()
    {
        if (panels == null || panels.Length == 0) return;
 
        int newIndex = currentIndex - 1;
 
        if (newIndex < 0)
        {
            if (!wrapAround) return;
            newIndex = panels.Length - 1;
        }
 
        ShowOnly(newIndex);
    }
 
    private void ShowOnly(int index)
    {
        currentIndex = index;
 
        for (int i = 0; i < panels.Length; i++)
        {
            if (panels[i] != null)
                panels[i].SetActive(i == currentIndex);
        }
    }

}
