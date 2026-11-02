using System.Collections.Generic;
using Player;
using UnityEngine;

namespace ImproveFlashlightAgainstFog;

public sealed class FlashlightFogCut : MonoBehaviour
{
    const int MaxSpheres = 6;
    const float OriginBias = 0.35f;
    const float MinRadius = 0.2f;
    const float ChainStart = 0.45f;
    const float MaxRadius = 3.5f;
    const float RadiusOverStep = 0.78f;
    // Illuminance falls with distance squared, so clear distance scales with sqrt(brightness).
    // Brightness 1 is a white light at intensity 1 and clears about 10 m. Six spheres can cover ~27 m.
    const float ReferenceBrightness = 1f;
    const float ReferenceReach = 10f;
    const float MinClear = 2.5f;

    readonly List<FogSphereAllocator> _pool = new List<FogSphereAllocator>(MaxSpheres);

    int _used;
    bool _loggedBudget;
    bool _loggedBeam;
    bool _loggedFailure;

    void LateUpdate()
    {
        try
        {
            _used = 0;
            if (!Plugin.CutFog.Value)
            {
                ReleaseUnused();
                return;
            }

            var agents = PlayerManager.PlayerAgentsInLevel;
            if (agents == null || agents.Count == 0 || PreLitVolume.Current == null)
            {
                ReleaseUnused();
                return;
            }

            float density = Plugin.CutDensity.Value;
            if (Mathf.Abs(density) < 0.0001f)
            {
                ReleaseUnused();
                return;
            }

            LogBudgetOnce();
            int count = agents.Count;
            for (int i = 0; i < count && _used < MaxSpheres; i++)
            {
                PlayerAgent agent = agents[i];
                if (agent == null)
                    continue;
                PlayerInventoryBase inventory = agent.Inventory;
                if (inventory == null)
                    continue;
                if (!TryGetBeam(inventory, out Vector3 origin, out Vector3 direction, out float reach, out float brightness))
                    continue;
                PlaceChain(origin, direction, reach, brightness, density);
            }

            ReleaseUnused();
        }
        catch (System.Exception ex)
        {
            if (_loggedFailure)
                return;
            _loggedFailure = true;
            Plugin.Logger.LogError("Flashlight fog cut failed: " + ex);
            ReleaseUnused();
        }
    }

    void OnDestroy()
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            FogSphereAllocator allocator = _pool[i];
            if (allocator != null && allocator.IsAllocated)
                allocator.Deallocate();
        }
        _pool.Clear();
    }

    void LogBudgetOnce()
    {
        if (_loggedBudget)
            return;
        _loggedBudget = true;
        Plugin.Logger.LogInfo("Fog sphere slots: " + PreLitVolume.SUPPORTED_FOG_SPHERE_COUNT + ". Flashlight uses at most " + MaxSpheres + ".");
    }

    static bool IsFlashlightOn(PlayerInventoryBase inventory)
    {
        if (inventory.FlashlightEnabled)
            return true;

        MeshRenderer cone = inventory.m_flashlightConeRenderer;
        return cone != null && cone.enabled && cone.gameObject.activeInHierarchy;
    }

    static bool TryGetBeam(PlayerInventoryBase inventory, out Vector3 origin, out Vector3 direction, out float reach, out float brightness)
    {
        origin = default;
        direction = default;
        reach = 0f;
        brightness = 0f;
        if (!IsFlashlightOn(inventory))
            return false;

        Light light = inventory.m_flashlight;
        var clustered = inventory.m_flashlightCLight;
        if (clustered != null && clustered.m_unityLight != null)
            light = clustered.m_unityLight;

        bool havePose = false;
        if (clustered != null && clustered.m_forward.sqrMagnitude > 0.01f)
        {
            direction = clustered.m_forward;
            origin = clustered.m_position;
            havePose = true;
        }
        else if (light != null)
        {
            direction = light.transform.forward;
            origin = light.transform.position;
            havePose = true;
        }

        if (!havePose || direction.sqrMagnitude < 0.0001f)
            return false;
        direction.Normalize();

        float hardwareRange = 20f;
        brightness = ReferenceBrightness;
        if (light != null)
        {
            if (light.range > 1f)
                hardwareRange = light.range;
            float luminance = light.color.grayscale;
            if (luminance < 0.05f)
                luminance = 0.05f;
            brightness = Mathf.Max(light.intensity, 0.01f) * luminance;
        }

        float maxClear = ChainStart + MaxSpheres * (MaxRadius / RadiusOverStep);
        float clear = ReferenceReach * Mathf.Sqrt(brightness / ReferenceBrightness);
        clear = Mathf.Clamp(clear, MinClear, Mathf.Min(hardwareRange, maxClear));

        origin += direction * OriginBias;
        reach = Mathf.Max(clear - OriginBias, 1f);
        reach = StopAtGeometry(origin, direction, reach);
        return reach > MinRadius;
    }

    static float StopAtGeometry(Vector3 origin, Vector3 direction, float reach)
    {
        int mask = ~(1 << 2);
        RaycastHit[] hits = Physics.RaycastAll(origin, direction, reach, mask, QueryTriggerInteraction.Ignore);
        float closest = reach;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.distance < 0.75f)
                continue;
            Collider collider = hit.collider;
            if (collider != null && collider.GetComponentInParent<PlayerAgent>() != null)
                continue;
            if (hit.distance < closest)
                closest = hit.distance;
        }
        return closest;
    }

    void PlaceChain(Vector3 origin, Vector3 direction, float reach, float brightness, float density)
    {
        float span = reach - ChainStart;
        int budget = MaxSpheres - _used;
        if (span < MinRadius || budget <= 0)
            return;

        if (!_loggedBeam)
        {
            _loggedBeam = true;
            Plugin.Logger.LogInfo(
                "Flashlight fog cut follows brightness " + brightness.ToString("0.00")
                + ". Clear distance " + reach.ToString("0.0")
                + ", density " + density.ToString("0.0") + ".");
        }

        int count = Mathf.Clamp(Mathf.CeilToInt(span / 1.75f), 1, budget);
        float step = span / count;
        for (int i = 0; i < count; i++)
        {
            float center = ChainStart + step * (i + 0.5f);
            float radius = step * RadiusOverStep;
            if (radius > MaxRadius)
                radius = MaxRadius;
            float room = reach - center;
            if (radius > room)
                radius = room;
            if (radius < MinRadius)
                continue;

            FogSphereAllocator allocator = Rent();
            if (allocator == null)
                return;

            allocator.SetPositionRange(origin + direction * center, radius);
            allocator.SetRadiance(Color.white, 0.02f);
            allocator.SetDensity(density);
        }
    }

    FogSphereAllocator Rent()
    {
        FogSphereAllocator allocator;
        if (_used < _pool.Count)
        {
            allocator = _pool[_used];
        }
        else
        {
            allocator = new FogSphereAllocator();
            _pool.Add(allocator);
        }

        if (!allocator.IsAllocated && !allocator.TryAllocate())
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Plugin.Logger.LogWarning("No free fog-sphere slot for the flashlight. A repeller or turbine is using the remaining slots.");
            }
            return null;
        }

        _used++;
        return allocator;
    }

    void ReleaseUnused()
    {
        for (int i = _used; i < _pool.Count; i++)
        {
            FogSphereAllocator allocator = _pool[i];
            if (allocator != null && allocator.IsAllocated)
                allocator.Deallocate();
        }
    }
}
