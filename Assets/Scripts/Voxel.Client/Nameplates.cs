using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The names of the other players over their heads (the "nameplates" layer of Assets/UI/Hud.uxml,
    // run by HudBanner): one pill per player, placed every frame over its head, fading out from
    // FadeFrom to MaxDistance blocks away, hidden behind the camera and when a block stands between
    // the eye and the head. Players without a name yet (still choosing it) have none.
    internal sealed class Nameplates
    {
        private const float AboveHead = 0.45f;
        private const float FadeFrom = 32f;
        private const float MaxDistance = 48f;

        private readonly VisualElement layer;
        private readonly List<Label> labels = new List<Label>();

        public Nameplates(VisualElement layer)
        {
            this.layer = layer;
        }

        public void Place(NetworkManager networkManager, World world, Camera eye)
        {
            int shown = 0;
            IPanel panel = layer.panel;
            if (eye != null && world != null && panel != null && networkManager.IsListening && networkManager.SpawnManager != null)
            {
                Vector3 from = eye.transform.position;
                foreach (NetworkObject other in networkManager.SpawnManager.SpawnedObjectsList)
                {
                    if (other.IsLocalPlayer || !other.TryGetComponent(out PlayerModel model)) continue;

                    string name = model.PlayerName;
                    if (name.Length == 0) continue;

                    Vector3 head = other.transform.position + Vector3.up * (PlayerRules.Height + AboveHead);
                    Vector3 toHead = head - from;
                    float distance = toHead.magnitude;
                    if (distance > MaxDistance || Vector3.Dot(toHead, eye.transform.forward) <= 0.1f) continue;
                    if (VoxelRaycast.Cast(world, from.ToNumerics(), (toHead / distance).ToNumerics(), distance, out _)) continue;

                    Label label = LabelAt(shown++);
                    if (label.text != name) label.text = name;
                    Vector2 point = RuntimePanelUtils.CameraTransformWorldToPanel(panel, head, eye);
                    label.style.left = point.x;
                    label.style.top = point.y;
                    label.style.opacity = Mathf.Clamp01((MaxDistance - distance) / (MaxDistance - FadeFrom));
                }
            }

            for (int i = shown; i < labels.Count; i++)
            {
                labels[i].style.display = DisplayStyle.None;
            }
        }

        private Label LabelAt(int index)
        {
            if (index == labels.Count)
            {
                var label = new Label { pickingMode = PickingMode.Ignore };
                label.AddToClassList("nameplate");
                layer.Add(label);
                labels.Add(label);
            }

            labels[index].style.display = DisplayStyle.Flex;
            return labels[index];
        }
    }
}
