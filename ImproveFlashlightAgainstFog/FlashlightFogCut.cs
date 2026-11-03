using System.Collections.Generic;
using CullingSystem;
using GameData;
using Gear;
using Player;
using UnityEngine;

namespace ImproveFlashlightAgainstFog;

// Il2Cpp injection only walks this behaviour. The carve lives on a plain class so ref structs
// and out parameters are not converted into IL2CPP methods (that conversion was aborting Load).
public sealed class FlashlightFogCut : MonoBehaviour
{
    FogCutWork _work;

    void LateUpdate()
    {
        if (_work == null)
            _work = new FogCutWork();
        _work.Tick();
    }

    void OnDestroy()
    {
        if (_work != null)
            _work.Dispose();
    }
}

sealed class FogCutWork
{
    const int MaxBeam = 12;
    // One chain per flashlight. Shared anti-fog keeps a second chain from carving where the first
    // already covers, so these slots are the parts of the other beams that do not overlap.
    const int MaxSamples = MaxBeam * 4;
    // The level has 32 fog spheres. 24 are for the beam plus the spots it just left; the rest stay
    // free for a repeller or turbine. A spot the light has left must keep its sphere, or the fog pops
    // back in one frame and pops out again when a slot frees.
    const int MaxDrawn = 24;
    // A one-frame miss (weapon bob, a wall hit on a spacing boundary) must not start the return.
    const float MissGrace = 0.12f;
    const float EstablishedCharge = 0.35f;
    const float OriginBias = 0.35f;
    const float MinRadius = 0.2f;
    const float ChainStart = 0.45f;
    // Width of the tunnel at radius modifier 1. Spheres overlap so the gap between them stays clear.
    const float TunnelRadius = 2.6f;
    const float Overlap = 1.2f;
    // The fog shader fades a sphere to nothing at its edge. A raw -4 only thins fog, which reads as a couple of meters.
    const float DensityScale = 8f;
    // Illuminance falls with distance squared, so extra brightness stretches the clear with sqrt(intensity).
    // Brightness 1 is a white light at intensity 1 and clears about 14 m before the length modifier.
    // Gun lights and the long range flashlight share a dim Unity intensity, so the throw also follows
    // the authored range. A short Unity range must not pull a normal light down to a couple of meters.
    const float ReferenceBrightness = 1f;
    const float ReferenceReach = 14f;
    const float MaxClear = 64f;
    const float MinClear = 4f;
    // Hits closer than this are the holder. The ray starts past them.
    const float NearSkip = 0.75f;
    // A still beam recasts at 10 Hz so a door or enemy can enter it. Turning and walking recast immediately.
    const float CastInterval = 0.1f;
    const float CastMoveSqr = 0.0001f;
    const float CastDirDot = 0.9998f;
    const float PositionMoveSqr = 0.0004f;
    const float RadiusEpsilon = 0.02f;
    const int MaxSites = 32;

    readonly List<FogSphereAllocator> _pool = new List<FogSphereAllocator>(MaxDrawn);
    readonly SphereWrite[] _written = new SphereWrite[MaxDrawn];
    readonly WallCast[] _casts = new WallCast[8];
    readonly Sample[] _samples = new Sample[MaxSamples];
    readonly Site[] _sites = new Site[MaxSites];

    int _sampleCount;
    float _openReach;
    float _beamRange;
    float _loggedBrightness;
    float _loggedRange;
    int _beamLogs;

    int _used;
    int _beamMask;
    bool _beamMaskReady;
    bool _loggedBudget;
    bool _loggedFailure;

    struct SphereWrite
    {
        public Vector3 Position;
        public float Radius;
        public float Density;
        public bool RadianceSet;
        public bool Valid;
    }

    struct WallCast
    {
        public int AgentId;
        public Vector3 Origin;
        public Vector3 Direction;
        public float Reach;
        public float Stopped;
        public float NextTime;
        public bool Valid;
    }

    struct Sample
    {
        public Vector3 Position;
        public float Radius;
        public float Density;
        public int Beam;
    }

    struct Site
    {
        public Vector3 Position;
        public float Radius;
        public float Density;
        public float Charge;
        public float AbsentFor;
        public float Missed;
        public float Fade;
        public bool Alive;
        public bool Covered;
        public bool Drawn;
        public int Beam;
    }

    public void Tick()
    {
        try
        {
            _used = 0;
            if (!Plugin.CutFog.Value)
            {
                ClearSites();
                ReleaseUnused();
                return;
            }

            var agents = PlayerManager.PlayerAgentsInLevel;
            if (agents == null || agents.Count == 0 || PreLitVolume.Current == null)
            {
                ClearSites();
                ReleaseUnused();
                return;
            }

            float density = Plugin.CutDensity.Value * DensityScale;
            if (Mathf.Abs(density) < 0.0001f)
            {
                ClearSites();
                ReleaseUnused();
                return;
            }

            LogBudgetOnce();
            _sampleCount = 0;
            bool shared = Plugin.CutShared.Value;
            int cap = shared ? MaxSamples : MaxBeam;
            int count = agents.Count;
            for (int pass = 0; pass < 2 && _sampleCount < cap; pass++)
            {
                for (int i = 0; i < count && _sampleCount < cap; i++)
                {
                    PlayerAgent agent = agents[i];
                    if (agent == null)
                        continue;
                    if (agent.IsLocallyOwned != (pass == 0))
                        continue;
                    PlayerInventoryBase inventory = agent.Inventory;
                    if (inventory == null)
                        continue;
                    if (!TryGetBeam(agent, inventory, out Vector3 origin, out Vector3 direction, out float reach, out float brightness))
                        continue;
                    int beam = agent.GetInstanceID();
                    if (beam == 0)
                        beam = i + 1;
                    PlaceChain(origin, direction, reach, brightness, density, beam, shared);
                }
            }

            float dt = Time.deltaTime;
            if (dt < 0f)
                dt = 0f;
            else if (dt > 0.1f)
                dt = 0.1f;
            float delay = Plugin.CutDelay.Value;
            if (delay < 0f)
                delay = 0f;
            float hold = Plugin.CutHold.Value;
            if (hold < 0f)
                hold = 0f;
            float recover = Plugin.CutReturn.Value;
            if (recover < 0f)
                recover = 0f;
            SettleSites(dt, delay, hold, recover, shared);
            FitTrail();
            WriteSites();
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

    public void Dispose()
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
        Plugin.Logger.LogInfo("Fog sphere slots: " + PreLitVolume.SUPPORTED_FOG_SPHERE_COUNT + ". Flashlight draws at most " + MaxDrawn + ".");
    }

    static bool IsFlashlightOn(PlayerInventoryBase inventory)
    {
        if (inventory.FlashlightEnabled)
            return true;

        MeshRenderer cone = inventory.m_flashlightConeRenderer;
        return cone != null && cone.enabled && cone.gameObject.activeInHierarchy;
    }

    bool TryGetBeam(PlayerAgent agent, PlayerInventoryBase inventory, out Vector3 origin, out Vector3 direction, out float reach, out float brightness)
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

        MeasureBeam(inventory, light, clustered, out brightness, out float hardwareRange);

        float length = Modifier(Plugin.CutLength.Value);
        float byBrightness = ReferenceReach * Mathf.Sqrt(brightness / ReferenceBrightness) * length;
        // The long range flashlight's Unity intensity matches a weapon light, so brightness alone
        // cleared both the same distance. Its throw is the longer authored range.
        float byThrow = hardwareRange * length;
        float clear = byBrightness > byThrow ? byBrightness : byThrow;
        float floor = MinClear * length;
        if (clear < floor)
            clear = floor;
        float ceiling = MaxClear * length;
        if (clear > ceiling)
            clear = ceiling;
        _beamRange = hardwareRange;

        origin += direction * OriginBias;
        reach = Mathf.Max(clear - OriginBias, 1f);
        _openReach = reach;
        reach = StopAtGeometry(agent.GetInstanceID(), agent.transform, origin, direction, reach);
        return reach > MinRadius;
    }

    static void MeasureBeam(PlayerInventoryBase inventory, Light light, C_Light clustered, out float brightness, out float hardwareRange)
    {
        brightness = ReferenceBrightness;
        hardwareRange = 20f;
        if (light != null)
        {
            if (light.range > 1f)
                hardwareRange = light.range;
            brightness = Luminance(light.color, light.intensity);
        }

        GearPartFlashlight part = inventory.m_currentGearPartFlashlight;
        if (part != null && FlashlightSettingsDataBlock.HasBlock(part.m_settingsID))
        {
            FlashlightSettingsDataBlock block = FlashlightSettingsDataBlock.GetBlock(part.m_settingsID);
            if (block != null)
            {
                float authored = Luminance(block.color, block.intensity);
                if (authored > brightness)
                    brightness = authored;
                if (block.range > hardwareRange)
                    hardwareRange = block.range;
            }
        }

        if (clustered == null || clustered.m_clusterLight == null)
            return;
        CL_SpotLight spot = clustered.m_clusterLight.TryCast<CL_SpotLight>();
        if (spot == null)
            return;

        sCL_SpotLight data = spot.Data;
        float irradiance = data.Irradiance.magnitude;
        if (irradiance > brightness)
            brightness = irradiance;
        else
        {
            float scale = spot.m_lightScale.x;
            if (scale > 1f)
                brightness *= scale;
        }

        if (data.InvRangeSqr > 0.000001f)
        {
            float range = 1f / Mathf.Sqrt(data.InvRangeSqr);
            if (range > hardwareRange && range < MaxClear)
                hardwareRange = range;
        }
    }

    static float Luminance(Color color, float intensity)
    {
        float luminance = color.grayscale;
        if (luminance < 0.05f)
            luminance = 0.05f;
        float power = intensity;
        if (power < 0.01f)
            power = 0.01f;
        return power * luminance;
    }

    static float Modifier(float value)
    {
        if (value < 0.05f)
            return 0.05f;
        return value;
    }

    int BeamMask()
    {
        if (_beamMaskReady)
            return _beamMask;

        // These layers are the player body and the held weapon. The ray also skips the casting
        // agent's own colliders, so a weapon on another layer cannot end the carve at the muzzle.
        int mask = ~(1 << 2);
        mask = ExcludeLayer(mask, "PlayerSynced");
        mask = ExcludeLayer(mask, "PlayerMover");
        mask = ExcludeLayer(mask, "FirstPersonItem");
        mask = ExcludeLayer(mask, "ThirdPersonItem");
        _beamMask = mask;
        _beamMaskReady = true;
        return mask;
    }

    static int ExcludeLayer(int mask, string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer < 0)
            return mask;
        return mask & ~(1 << layer);
    }

    int FindCast(int agentId)
    {
        int free = -1;
        for (int i = 0; i < _casts.Length; i++)
        {
            if (_casts[i].Valid && _casts[i].AgentId == agentId)
                return i;
            if (free < 0 && !_casts[i].Valid)
                free = i;
        }

        return free >= 0 ? free : 0;
    }

    float StopAtGeometry(int agentId, Transform holder, Vector3 origin, Vector3 direction, float reach)
    {
        int slot = FindCast(agentId);
        ref WallCast cache = ref _casts[slot];
        bool same = cache.Valid && cache.AgentId == agentId;
        bool moved = same && (
            (origin - cache.Origin).sqrMagnitude > CastMoveSqr
            || Vector3.Dot(direction, cache.Direction) < CastDirDot
            || Mathf.Abs(cache.Reach - reach) > 0.05f);
        if (same && !moved && Time.time < cache.NextTime)
            return Mathf.Min(cache.Stopped, reach);

        float stopped = CastPastHolder(holder, origin, direction, reach);

        cache.AgentId = agentId;
        cache.Origin = origin;
        cache.Direction = direction;
        cache.Reach = reach;
        cache.Stopped = stopped;
        cache.NextTime = Time.time + CastInterval;
        cache.Valid = true;
        return stopped;
    }

    float CastPastHolder(Transform holder, Vector3 origin, Vector3 direction, float reach)
    {
        float start = NearSkip;
        if (start > reach)
            start = 0f;
        float traveled = start;
        Vector3 pos = origin + direction * start;
        float left = reach - start;
        int mask = BeamMask();
        for (int n = 0; n < 6 && left > 0.05f; n++)
        {
            if (!Physics.Raycast(pos, direction, out RaycastHit hit, left, mask, QueryTriggerInteraction.Ignore))
                return reach;
            if (!IsUnder(hit.collider, holder))
                return traveled + hit.distance;
            float advance = hit.distance + 0.05f;
            if (advance < 0.05f)
                advance = 0.05f;
            traveled += advance;
            if (traveled >= reach)
                return reach;
            pos += direction * advance;
            left -= advance;
        }

        return Mathf.Min(traveled, reach);
    }

    static bool IsUnder(Collider collider, Transform holder)
    {
        if (collider == null || holder == null)
            return false;
        Transform node = collider.transform;
        for (int guard = 0; node != null && guard < 32; guard++)
        {
            if (node == holder)
                return true;
            node = node.parent;
        }

        return false;
    }

    void PlaceChain(Vector3 origin, Vector3 direction, float reach, float brightness, float density, int beam, bool shared)
    {
        float span = reach - ChainStart;
        // Without sharing, every light appends into one chain budget, matching the old cap.
        // With sharing, each light gets a full chain and the overlap is dropped when the holes are placed.
        int budget = shared ? MaxBeam : MaxBeam - _sampleCount;
        if (shared)
        {
            int room = MaxSamples - _sampleCount;
            if (budget > room)
                budget = room;
        }
        if (span < MinRadius || budget <= 0)
            return;
        int first = _sampleCount;

        float radius = TunnelRadius * Modifier(Plugin.CutRadius.Value);
        float spacing = radius * Overlap;
        if (spacing < MinRadius)
            spacing = MinRadius;
        // Centers stay on a fixed spacing. Redistributing them whenever the wall distance changes
        // moved every sphere off its cleared spot, spawned a new low-charge hole, and let the fog pop
        // back into the spot the light had just cleared.
        int count = Mathf.CeilToInt(span / spacing);
        if (count < 1)
            count = 1;
        if (count > budget)
            count = budget;
        float growCap = radius * 2.5f;

        bool changed = Mathf.Abs(_loggedBrightness - brightness) > 0.2f || Mathf.Abs(_loggedRange - _beamRange) > 2f;
        if (_beamLogs < 4 && (_beamLogs == 0 || changed))
        {
            _beamLogs++;
            _loggedBrightness = brightness;
            _loggedRange = _beamRange;
            Plugin.Logger.LogInfo(
                "Flashlight fog cut brightness " + brightness.ToString("0.00")
                + ", throw " + _beamRange.ToString("0.0")
                + " m. Open " + _openReach.ToString("0.0")
                + " m, after walls " + reach.ToString("0.0")
                + " m, spheres " + count
                + ", radius " + radius.ToString("0.0")
                + ", density " + density.ToString("0.0") + ".");
        }

        for (int i = 0; i < count; i++)
        {
            float center = ChainStart + spacing * (i + 0.5f);
            if (center >= reach)
            {
                if (i > 0)
                    break;
                center = ChainStart + span * 0.5f;
            }

            float sphere = radius;
            float room = reach - center;
            if (sphere > room)
                sphere = room;
            if (sphere < MinRadius)
                continue;

            _samples[_sampleCount] = new Sample
            {
                Position = origin + direction * center,
                Radius = sphere,
                Density = density,
                Beam = beam,
            };
            _sampleCount++;
        }

        if (_sampleCount > first)
        {
            int last = _sampleCount - 1;
            float along = Vector3.Dot(_samples[last].Position - origin, direction);
            float cover = reach - along;
            float sphere = _samples[last].Radius;
            if (sphere < cover)
                sphere = cover;
            if (sphere > growCap)
                sphere = growCap;
            float room = reach - along;
            if (sphere > room)
                sphere = room;
            if (sphere < MinRadius)
                sphere = MinRadius;
            _samples[last].Radius = sphere;
        }
    }

    float HitLimit(ref Site site)
    {
        float spacing = TunnelRadius * Modifier(Plugin.CutRadius.Value) * Overlap;
        if (spacing < MinRadius)
            spacing = MinRadius;
        // Wide enough that bob and a small reach change stay on the same spot, narrow enough
        // that the next sphere along the beam still gets its own spot.
        float cap = spacing * 0.62f;
        float limit = site.Radius * 0.75f;
        if (limit > cap)
            limit = cap;
        float floor = spacing * 0.35f;
        if (floor < 0.35f)
            floor = 0.35f;
        if (floor > cap)
            floor = cap;
        if (limit < floor)
            limit = floor;
        return limit;
    }

    bool Hits(ref Site site, Vector3 position)
    {
        float limit = HitLimit(ref site);
        return (site.Position - position).sqrMagnitude <= limit * limit;
    }

    bool Represents(ref Site site, Vector3 position)
    {
        if (!site.Alive)
            return false;
        if (Hits(ref site, position))
            return true;
        if (site.Missed >= MissGrace)
            return false;
        float limit = site.Radius;
        if (limit < 0.5f)
            limit = 0.5f;
        return (site.Position - position).sqrMagnitude <= limit * limit;
    }

    // True when another flashlight's hole already covers this sample.
    // Centers of one chain sit just outside each other's radius, so this does not collapse a single beam.
    static bool OtherBeamCovers(ref Site site, Vector3 position, float radius, int beam)
    {
        if (!site.Alive || site.Beam == 0 || beam == 0 || site.Beam == beam)
            return false;
        float reach = site.Radius;
        if (reach < radius)
            reach = radius;
        return (site.Position - position).sqrMagnitude <= reach * reach;
    }

    static bool InBeam(ref Site site)
    {
        return site.Alive && (site.Covered || site.Missed < MissGrace);
    }

    static float ChargeStep(float delay, float dt)
    {
        if (delay <= 0.001f)
            return 1f;
        return dt / delay;
    }

    void SettleSites(float dt, float delay, float hold, float recover, bool shared)
    {
        for (int i = 0; i < MaxSites; i++)
            _sites[i].Covered = false;

        for (int s = 0; s < _sampleCount; s++)
        {
            Vector3 position = _samples[s].Position;
            float density = _samples[s].Density;
            float radius = _samples[s].Radius;
            int beam = _samples[s].Beam;
            for (int i = 0; i < MaxSites; i++)
            {
                ref Site site = ref _sites[i];
                if (!site.Alive)
                    continue;
                bool own = Hits(ref site, position);
                bool other = shared && OtherBeamCovers(ref site, position, radius, beam);
                if (!own && !other)
                    continue;
                site.Covered = true;
                // The other beam keeps this hole lit. It does not widen it or cut any harder.
                if (other)
                    continue;
                site.Density = density;
                if (radius > site.Radius)
                    site.Radius = radius;
                if (site.Beam == 0)
                    site.Beam = beam;
            }
        }

        float step = ChargeStep(delay, dt);
        for (int i = 0; i < MaxSites; i++)
        {
            ref Site site = ref _sites[i];
            if (!site.Alive)
                continue;
            if (site.Covered)
            {
                site.Missed = 0f;
                // Bring a returning spot back over a short ramp. Snapping it to a full clear makes
                // the fog vanish and reappear when the beam grazes the edge of a cleared spot.
                float restore = dt / 0.2f;
                if (restore < step)
                    restore = step;
                if (restore > 1f)
                    restore = 1f;
                site.Fade += restore;
                if (site.Fade > 1f)
                    site.Fade = 1f;
                site.Charge += step;
                if (site.Charge > 1f)
                    site.Charge = 1f;
                if (site.Fade >= 1f)
                    site.AbsentFor = 0f;
            }
            else
            {
                site.Missed += dt;
                if (site.Missed < MissGrace)
                    continue;
                site.AbsentFor += dt;
                // Hold the full clear, then let fog fill the same spot over the return period.
                // Deleting the sphere on the deadline puts the fog back in one frame.
                if (site.AbsentFor > hold)
                {
                    float fadeTime = recover > 0.001f ? recover : 0.001f;
                    float t = (site.AbsentFor - hold) / fadeTime;
                    if (t >= 1f)
                    {
                        site.Fade = 0f;
                        site.Alive = false;
                    }
                    else
                        site.Fade = 1f - t;
                }
            }
        }

        for (int s = 0; s < _sampleCount; s++)
        {
            Vector3 position = _samples[s].Position;
            float radius = _samples[s].Radius;
            int beam = _samples[s].Beam;
            if (shared && CoveredByOtherBeam(position, radius, beam))
                continue;

            bool represented = false;
            for (int i = 0; i < MaxSites; i++)
            {
                if (Represents(ref _sites[i], position))
                {
                    represented = true;
                    break;
                }
            }

            if (represented)
                continue;

            int slot = MakeSlot();
            if (slot < 0)
                continue;

            _sites[slot] = new Site
            {
                Position = position,
                Radius = radius,
                Density = _samples[s].Density,
                Charge = step > 1f ? 1f : step,
                AbsentFor = 0f,
                Missed = 0f,
                Fade = 1f,
                Alive = true,
                Covered = true,
                Beam = beam,
            };
        }
    }

    bool CoveredByOtherBeam(Vector3 position, float radius, int beam)
    {
        for (int i = 0; i < MaxSites; i++)
        {
            if (OtherBeamCovers(ref _sites[i], position, radius, beam))
                return true;
        }

        return false;
    }

    int FreeSite()
    {
        for (int i = 0; i < MaxSites; i++)
        {
            if (!_sites[i].Alive)
                return i;
        }

        return -1;
    }

    int MakeSlot()
    {
        int slot = FreeSite();
        if (slot >= 0)
            return slot;
        if (TryMergeLingering())
        {
            slot = FreeSite();
            if (slot >= 0)
                return slot;
        }

        return EvictLingering();
    }

    int EvictLingering()
    {
        int best = -1;
        float oldest = 0f;
        for (int i = 0; i < MaxSites; i++)
        {
            if (!_sites[i].Alive || InBeam(ref _sites[i]))
                continue;
            if (best >= 0 && _sites[i].AbsentFor <= oldest)
                continue;
            best = i;
            oldest = _sites[i].AbsentFor;
        }

        if (best >= 0)
            _sites[best].Alive = false;
        return best;
    }

    static float DrawnRadius(ref Site site)
    {
        return site.Radius * site.Charge * Mathf.Lerp(0.65f, 1f, site.Fade);
    }

    static bool NeedsSphere(ref Site site)
    {
        return Visible(ref site) && DrawnRadius(ref site) >= 0.05f;
    }

    int BeamSlots()
    {
        int beam = 0;
        for (int i = 0; i < MaxSites; i++)
        {
            if (InBeam(ref _sites[i]) && NeedsSphere(ref _sites[i]))
                beam++;
        }

        return beam;
    }

    int LingeringSlots()
    {
        int lingering = 0;
        for (int i = 0; i < MaxSites; i++)
        {
            if (!InBeam(ref _sites[i]) && NeedsSphere(ref _sites[i]))
                lingering++;
        }

        return lingering;
    }

    void FitTrail()
    {
        int guard = MaxSites;
        while (guard-- > 0)
        {
            int spare = MaxDrawn - BeamSlots();
            if (spare < 0)
                spare = 0;
            if (LingeringSlots() <= spare)
                return;
            if (!TryMergeLingering())
                return;
        }
    }

    bool TryMergeLingering()
    {
        int a = -1;
        int b = -1;
        float best = 0f;
        for (int i = 0; i < MaxSites; i++)
        {
            if (InBeam(ref _sites[i]) || !NeedsSphere(ref _sites[i]))
                continue;
            for (int j = i + 1; j < MaxSites; j++)
            {
                if (InBeam(ref _sites[j]) || !NeedsSphere(ref _sites[j]))
                    continue;
                float dist = Vector3.Distance(_sites[i].Position, _sites[j].Position);
                float reach = _sites[i].Radius + _sites[j].Radius + 0.2f;
                if (dist > reach)
                    continue;
                if (a >= 0 && dist >= best)
                    continue;
                a = i;
                b = j;
                best = dist;
            }
        }

        if (a < 0)
            return false;
        Unite(a, b);
        return true;
    }

    void Unite(int keep, int drop)
    {
        ref Site a = ref _sites[keep];
        ref Site b = ref _sites[drop];
        Vector3 delta = b.Position - a.Position;
        float dist = delta.magnitude;
        bool dropCoversKeep = dist < 0.001f || dist + a.Radius <= b.Radius;
        bool separate = dist >= 0.001f && dist + b.Radius > a.Radius && dist + a.Radius > b.Radius;
        if (dropCoversKeep && b.Radius > a.Radius)
        {
            a.Position = b.Position;
            a.Radius = b.Radius;
        }
        else if (separate)
        {
            float radius = (dist + a.Radius + b.Radius) * 0.5f;
            a.Position += delta * ((radius - a.Radius) / dist);
            a.Radius = radius;
        }

        if (b.Charge > a.Charge)
            a.Charge = b.Charge;
        if (b.Fade > a.Fade)
            a.Fade = b.Fade;
        if (b.AbsentFor < a.AbsentFor)
            a.AbsentFor = b.AbsentFor;
        if (b.Missed < a.Missed)
            a.Missed = b.Missed;
        if (Mathf.Abs(b.Density) > Mathf.Abs(a.Density))
            a.Density = b.Density;
        if (b.Covered)
            a.Covered = true;
        b.Alive = false;
    }

    void WriteSites()
    {
        for (int i = 0; i < MaxSites; i++)
            _sites[i].Drawn = false;

        // Established clears go out before a brand-new low-charge hole. Otherwise the new beam
        // steals every sphere and the fog pops back into the tunnel the light just left.
        if (!DrawPass(true, true))
            return;
        if (!DrawPass(false, true))
            return;
        if (!DrawPass(true, false))
            return;
        DrawPass(false, false);
    }

    bool DrawPass(bool inBeam, bool established)
    {
        while (_used < MaxDrawn)
        {
            int best = -1;
            float freshest = 0f;
            for (int i = 0; i < MaxSites; i++)
            {
                ref Site site = ref _sites[i];
                if (site.Drawn || InBeam(ref site) != inBeam || !NeedsSphere(ref site))
                    continue;
                if ((site.Charge >= EstablishedCharge) != established)
                    continue;
                if (best >= 0 && site.AbsentFor >= freshest)
                    continue;
                best = i;
                freshest = site.AbsentFor;
            }

            if (best < 0)
                return true;
            if (!Draw(ref _sites[best]))
                return false;
        }

        return true;
    }

    static bool Visible(ref Site site)
    {
        return site.Alive && site.Charge > 0.02f && site.Fade > 0.02f;
    }

    bool Draw(ref Site site)
    {
        site.Drawn = true;
        float radius = DrawnRadius(ref site);
        if (radius < 0.05f)
            return true;
        if (!Rent(out FogSphereAllocator allocator, out int slot))
            return false;
        ApplySphere(allocator, slot, site.Position, radius, site.Density * site.Fade);
        return true;
    }

    void ClearSites()
    {
        for (int i = 0; i < MaxSites; i++)
            _sites[i].Alive = false;
        _sampleCount = 0;
    }

    void ApplySphere(FogSphereAllocator allocator, int slot, Vector3 position, float radius, float density)
    {
        ref SphereWrite written = ref _written[slot];
        if (!written.Valid || (written.Position - position).sqrMagnitude > PositionMoveSqr || Mathf.Abs(written.Radius - radius) > RadiusEpsilon)
        {
            allocator.SetPositionRange(position, radius);
            written.Position = position;
            written.Radius = radius;
        }

        if (!written.Valid || written.Density != density)
        {
            allocator.SetDensity(density);
            written.Density = density;
        }

        if (!written.Valid || !written.RadianceSet)
        {
            allocator.SetRadiance(Color.white, 0.02f);
            written.RadianceSet = true;
        }

        written.Valid = true;
    }

    bool Rent(out FogSphereAllocator allocator, out int slot)
    {
        slot = _used;
        if (_used < _pool.Count)
        {
            allocator = _pool[_used];
        }
        else
        {
            allocator = new FogSphereAllocator();
            _pool.Add(allocator);
        }

        bool fresh = !allocator.IsAllocated;
        if (fresh && !allocator.TryAllocate())
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Plugin.Logger.LogWarning("No free fog-sphere slot for the flashlight. A repeller or turbine is using the remaining slots.");
            }
            return false;
        }

        if (fresh)
        {
            _written[slot].Valid = false;
            _written[slot].RadianceSet = false;
        }

        _used++;
        return true;
    }

    void ReleaseUnused()
    {
        for (int i = _used; i < _pool.Count; i++)
        {
            FogSphereAllocator allocator = _pool[i];
            if (allocator != null && allocator.IsAllocated)
                allocator.Deallocate();
            if (i < _written.Length)
            {
                _written[i].Valid = false;
                _written[i].RadianceSet = false;
            }
        }
    }
}
