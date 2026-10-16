using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // What a map draws over the world's picture, as elements kept from frame to frame: the nearby
    // animals (dots of their species' colour), the other players, and the local player's arrow.
    // Each map says where a world position falls on it.
    internal sealed class MapMarkers
    {
        // Where a world position falls in the map's frame, and whether it is on the map at all.
        public delegate bool Projection(Vector3 position, out Vector2 point);

        private readonly VisualElement animalLayer;
        private readonly VisualElement playerLayer;
        private readonly VisualElement arrow;
        private readonly List<VisualElement> animalDots = new List<VisualElement>();
        private readonly List<VisualElement> playerDots = new List<VisualElement>();
        private readonly List<KeyValuePair<int, Vector3>> animals = new List<KeyValuePair<int, Vector3>>();

        public MapMarkers(VisualElement animalLayer, VisualElement playerLayer, VisualElement arrow)
        {
            this.animalLayer = animalLayer;
            this.playerLayer = playerLayer;
            this.arrow = arrow;
        }

        public void Place(NetworkManager networkManager, EcosystemSync ecosystemSync, WorldMap worldMap, Transform localPlayer, Projection project)
        {
            ecosystemSync.GetAnimalPositions(animals);
            int shown = 0;
            foreach (KeyValuePair<int, Vector3> animal in animals)
            {
                if (!project(animal.Value, out Vector2 point)) continue;

                VisualElement dot = Dot(animalDots, animalLayer, shown++, "map-animal");
                dot.style.translate = new Translate(point.x, point.y);
                dot.style.backgroundColor = worldMap.AnimalColour(animal.Key);
            }

            HideFrom(animalDots, shown);

            shown = 0;
            foreach (NetworkObject other in networkManager.SpawnManager.SpawnedObjectsList)
            {
                if (!other.IsPlayerObject || other.IsLocalPlayer || !project(other.transform.position, out Vector2 point)) continue;

                Dot(playerDots, playerLayer, shown++, "map-player").style.translate = new Translate(point.x, point.y);
            }

            HideFrom(playerDots, shown);

            bool arrowShown = project(localPlayer.position, out Vector2 here);
            arrow.style.display = arrowShown ? DisplayStyle.Flex : DisplayStyle.None;
            arrow.style.translate = new Translate(here.x, here.y);
            arrow.style.rotate = new Rotate(new Angle(localPlayer.eulerAngles.y, AngleUnit.Degree));
        }

        private static VisualElement Dot(List<VisualElement> pool, VisualElement layer, int index, string styleClass)
        {
            if (index == pool.Count)
            {
                var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                dot.AddToClassList(styleClass);
                layer.Add(dot);
                pool.Add(dot);
            }

            pool[index].style.display = DisplayStyle.Flex;
            return pool[index];
        }

        private static void HideFrom(List<VisualElement> pool, int count)
        {
            for (int i = count; i < pool.Count; i++)
            {
                pool[i].style.display = DisplayStyle.None;
            }
        }
    }
}
