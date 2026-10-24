using UnityEngine;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // Translates the texts written in the scene's UXML documents (Loc.Apply), before the screens
    // fill them in, and again when the language changes. Texts the screens set themselves go
    // through Loc.T.
    [DefaultExecutionOrder(-100)]
    public sealed class LocalizedDocuments : MonoBehaviour
    {
        private void Awake()
        {
            Loc.Initialize();
            Loc.Changed += ApplyAll;
        }

        private void Start()
        {
            ApplyAll();
        }

        private void OnDestroy()
        {
            Loc.Changed -= ApplyAll;
        }

        private static void ApplyAll()
        {
            foreach (UIDocument document in FindObjectsByType<UIDocument>(FindObjectsInactive.Include))
            {
                Loc.Apply(document.rootVisualElement);
            }
        }
    }
}
