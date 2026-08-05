using UnityEngine;
using UnityEngine.SceneManagement;

public class loginCall : MonoBehaviour
{
    public void OnLoginButtonPressed()
    {
        toLogin navigator = GameObject.FindAnyObjectByType<toLogin>();
        if (navigator != null)
        {
            navigator.OnLoginComplete();
        }
    }
}