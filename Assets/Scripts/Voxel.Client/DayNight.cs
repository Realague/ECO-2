using Eco.Sim;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxel.Client
{
    // Days and nights, from the simulation's clock (EcosystemSync.StepTime), so every machine sees
    // the same hour: one game day is SimClock.StepsPerDay steps, 24 real minutes at speed 1, faster
    // when time is sped up. Display only, nothing is sent and nothing in the simulation depends on it.
    //
    // The scene's directional light is the sun from sunrise to sunset, rising in the east and
    // crossing the southern sky; at night it becomes a faint bluish moon, opposite. The ambient light
    // (the scene's own, from the sky), the reflections and the skybox fade with the daylight. In the
    // menu, the scene's lighting is given back as it was.
    public class DayNight : MonoBehaviour
    {
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private Light sun;

        [Tooltip("Hours of sunrise and sunset, game time.")]
        [SerializeField] private float sunriseHour = 6f;
        [SerializeField] private float sunsetHour = 20f;

        [Tooltip("Height of the sun above the horizon at midday, in degrees.")]
        [SerializeField] private float middayElevation = 60f;

        [Tooltip("Colour temperature of the sun at midday and at the horizon, in kelvins.")]
        [SerializeField] private float middayTemperature = 5500f;
        [SerializeField] private float horizonTemperature = 2200f;

        [SerializeField] private float moonIntensity = 0.12f;
        [SerializeField] private Color moonColour = new Color(0.55f, 0.65f, 1f);

        [Tooltip("Ambient light left at night, added to the faded ambient of the sky.")]
        [SerializeField] private Color nightAmbient = new Color(0.015f, 0.02f, 0.04f);

        [SerializeField] private float nightSkyboxExposure = 0.03f;
        [SerializeField] private float nightReflections = 0.05f;

        private SceneLighting scene;
        private Material skybox;
        private bool applied;
        private double shownSteps;

        // How much daylight there is, 0 in the dead of night to 1 by day. 1 in the menu.
        public static float Daylight { get; private set; } = 1f;

        private struct SceneLighting
        {
            public float Intensity;
            public Color Colour;
            public bool UsesTemperature;
            public float Temperature;
            public Quaternion Rotation;
            public SphericalHarmonicsL2 Ambient;
            public float Reflections;
            public Material Skybox;
            public float SkyboxExposure;
        }

        private void Update()
        {
            if (Application.isBatchMode) return;

            if (worldSync.World == null)
            {
                Restore();
                return;
            }

            if (!applied) Capture();

            // The clock seen here may step back a little when a message corrects it; the sun waits
            // instead of going back. A big change (time sped up, a new session) is followed at once.
            double target = ecosystemSync.StepTime;
            if (target > shownSteps || shownSteps - target > 2.0) shownSteps = target;

            double minutes = SimClock.MinutesAt(shownSteps) % SimClock.MinutesPerDay;
            Apply((float)(minutes / 60.0));
        }

        private void OnDisable()
        {
            Restore();
        }

        private void Apply(float hour)
        {
            // The sun's course: across the sky from sunrise to sunset, below the horizon the rest of
            // the time, at its own pace in each.
            float dayLength = sunsetHour - sunriseHour;
            float angle = hour >= sunriseHour && hour < sunsetHour
                ? (hour - sunriseHour) / dayLength * Mathf.PI
                : Mathf.PI + Mathf.Repeat(hour - sunsetHour, 24f) / (24f - dayLength) * Mathf.PI;

            // Towards the sun: east at sunrise, south at midday (south is z = 0), west at sunset.
            float elevation = middayElevation * Mathf.Deg2Rad;
            var towardsSun = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle) * Mathf.Sin(elevation), -Mathf.Sin(angle) * Mathf.Cos(elevation));

            float height = towardsSun.y;
            Daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.25f, height));
            float sunStrength = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.02f, 0.15f, height));
            float moonStrength = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.02f, 0.15f, -height));

            sun.useColorTemperature = false;
            if (height >= 0f)
            {
                sun.transform.rotation = Quaternion.LookRotation(-towardsSun);
                float temperature = Mathf.Lerp(horizonTemperature, middayTemperature, Mathf.Sqrt(Mathf.Clamp01(height / Mathf.Sin(elevation))));
                sun.color = scene.Colour * Mathf.CorrelatedColorTemperatureToRGB(temperature);
                sun.intensity = scene.Intensity * sunStrength;
            }
            else
            {
                sun.transform.rotation = Quaternion.LookRotation(towardsSun);
                sun.color = moonColour;
                sun.intensity = moonIntensity * moonStrength;
            }

            SphericalHarmonicsL2 ambient = scene.Ambient * Daylight;
            ambient.AddAmbientLight(nightAmbient * (1f - Daylight));
            RenderSettings.ambientProbe = ambient;
            RenderSettings.reflectionIntensity = Mathf.Lerp(nightReflections, scene.Reflections, Daylight);
            if (skybox != null) skybox.SetFloat("_Exposure", Mathf.Lerp(nightSkyboxExposure, scene.SkyboxExposure, Daylight));
        }

        private void Capture()
        {
            scene = new SceneLighting
            {
                Intensity = sun.intensity,
                Colour = sun.color,
                UsesTemperature = sun.useColorTemperature,
                Temperature = sun.colorTemperature,
                Rotation = sun.transform.rotation,
                Ambient = RenderSettings.ambientProbe,
                Reflections = RenderSettings.reflectionIntensity,
                Skybox = RenderSettings.skybox
            };

            // The skybox is darkened on a copy, so that the asset itself never changes.
            if (scene.Skybox != null && scene.Skybox.HasProperty("_Exposure"))
            {
                scene.SkyboxExposure = scene.Skybox.GetFloat("_Exposure");
                skybox = new Material(scene.Skybox) { name = scene.Skybox.name + " (day and night)" };
                RenderSettings.skybox = skybox;
            }

            shownSteps = ecosystemSync.StepTime;
            applied = true;
        }

        private void Restore()
        {
            Daylight = 1f;
            if (!applied) return;

            applied = false;
            sun.intensity = scene.Intensity;
            sun.color = scene.Colour;
            sun.useColorTemperature = scene.UsesTemperature;
            sun.colorTemperature = scene.Temperature;
            sun.transform.rotation = scene.Rotation;
            RenderSettings.ambientProbe = scene.Ambient;
            RenderSettings.reflectionIntensity = scene.Reflections;
            RenderSettings.skybox = scene.Skybox;
            if (skybox != null)
            {
                Destroy(skybox);
                skybox = null;
            }
        }
    }
}
