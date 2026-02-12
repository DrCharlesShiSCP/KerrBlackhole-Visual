// NakedSingularityCoreFX.cs
// Unity 6000+ (URP/HDRP agnostic)
// Drives a "globally naked singularity" core look by:
// 1) Flickering HDR Emission on a Material (or MaterialPropertyBlock per-renderer)
// 2) Subtle scale pulse + position jitter
// 3) (Optional) broadcasts screen-space center to a Full Screen Pass shader via global properties
//
// Usage:
// - Put this on your SingularityCore GameObject.
// - Assign a Renderer (MeshRenderer) and either:
//   A) Use MaterialPropertyBlock (recommended) -> doesn't duplicate materials, better for performance
//   B) Or set usePropertyBlock = false to edit a specific Material instance.
//
// Notes:
// - Your shader must have an Emission color property (commonly "_EmissionColor") and emission enabled.
// - For URP Full Screen Pass lensing, you can read global shader properties:
//   _SingularityWorldPos (float4), _SingularityViewportPos (float4)

using UnityEngine;

[DisallowMultipleComponent]
public class SingularityFlicker: MonoBehaviour
{
    [Header("Target")]
    public Renderer targetRenderer;
    [Tooltip("If true, uses MaterialPropertyBlock (recommended). If false, edits targetMaterial directly.")]
    public bool usePropertyBlock = true;

    [Tooltip("Only used when usePropertyBlock = false. Assign the exact material you want to edit.")]
    public Material targetMaterial;

    [Header("Emission Flicker")]
    [Tooltip("Emission color at peak. Keep this bright; HDR recommended.")]
    public Color emissionColor = Color.white;

    [Tooltip("Name of the emission color property in your shader.")]
    public string emissionColorProp = "_EmissionColor";

    [Tooltip("Base emission intensity multiplier.")]
    public float baseEmission = 6f;

    [Tooltip("How much emission intensity varies (0 = no flicker).")]
    public float flickerAmplitude = 4f;

    [Tooltip("How fast the flicker changes.")]
    public float flickerSpeed = 3.0f;

    [Tooltip("Adds a second layer of higher-frequency flicker for a more unstable feel.")]
    public float microFlickerAmplitude = 1.2f;

    [Tooltip("Speed of micro flicker layer.")]
    public float microFlickerSpeed = 17f;

    [Tooltip("Random seed offset for noise so multiple cores don't sync.")]
    public float noiseSeed = 0.1234f;

    [Header("Scale Pulse")]
    public bool enableScalePulse = true;
    public float baseScale = 1.0f;
    public float pulseAmplitude = 0.05f;
    public float pulseSpeed = 2.0f;

    [Header("Position Jitter")]
    public bool enablePositionJitter = true;
    public float jitterAmplitude = 0.015f;
    public float jitterSpeed = 8.0f;

    [Header("Shader Globals (for Full Screen Pass lensing)")]
    [Tooltip("If true, writes global shader values: _SingularityWorldPos and _SingularityViewportPos")]
    public bool setGlobalShaderCenter = true;

    [Tooltip("Camera used to compute viewport center. If null, will use Camera.main.")]
    public Camera referenceCamera;

    [Tooltip("Global shader property name for world pos (float4).")]
    public string globalWorldPosName = "_SingularityWorldPos";

    [Tooltip("Global shader property name for viewport pos (float4). xy = viewport position.")]
    public string globalViewportPosName = "_SingularityViewportPos";

    MaterialPropertyBlock _mpb;
    Vector3 _startPos;
    Vector3 _startScale;

    void Reset()
    {
        targetRenderer = GetComponentInChildren<Renderer>();
        referenceCamera = Camera.main;
    }

    void Awake()
    {
        if (!targetRenderer) targetRenderer = GetComponentInChildren<Renderer>();
        if (!referenceCamera) referenceCamera = Camera.main;

        _startPos = transform.position;
        _startScale = transform.localScale;

        if (usePropertyBlock)
        {
            _mpb = new MaterialPropertyBlock();
        }
        else
        {
            // If they forgot to assign a material, try to grab the renderer's material (creates an instance).
            if (!targetMaterial && targetRenderer) targetMaterial = targetRenderer.material;
        }

        // Apply once so it looks correct immediately.
        ApplyEmission(ComputeEmissionIntensity(Time.time));
        ApplyScale(Time.time);
        ApplyPosition(Time.time);
        ApplyGlobals();
    }

    void Update()
    {
        float t = Time.time;

        ApplyEmission(ComputeEmissionIntensity(t));

        if (enableScalePulse) ApplyScale(t);
        if (enablePositionJitter) ApplyPosition(t);

        if (setGlobalShaderCenter) ApplyGlobals();
    }

    float ComputeEmissionIntensity(float t)
    {
        // Smooth-ish flicker using PerlinNoise (stable, non-sine feel)
        float n1 = Mathf.PerlinNoise(noiseSeed, t * flickerSpeed);
        float n2 = Mathf.PerlinNoise(noiseSeed + 10.0f, t * microFlickerSpeed);

        // Map Perlin [0..1] to [-1..1]
        n1 = (n1 * 2f) - 1f;
        n2 = (n2 * 2f) - 1f;

        float intensity = baseEmission
                        + n1 * flickerAmplitude
                        + n2 * microFlickerAmplitude;

        // Avoid negative or too dim emission
        return Mathf.Max(0f, intensity);
    }

    void ApplyEmission(float intensity)
    {
        // Emission color * intensity
        Color hdr = emissionColor * intensity;

        if (usePropertyBlock)
        {
            if (!targetRenderer) return;
            targetRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(emissionColorProp, hdr);
            targetRenderer.SetPropertyBlock(_mpb);
        }
        else
        {
            if (!targetMaterial) return;
            targetMaterial.SetColor(emissionColorProp, hdr);
        }
    }

    void ApplyScale(float t)
    {
        float pulse = Mathf.Sin(t * pulseSpeed) * pulseAmplitude;
        float s = baseScale + pulse;

        // Keep relative to initial scale so you can resize the object in-editor
        transform.localScale = _startScale * s;
    }

    void ApplyPosition(float t)
    {
        // Jitter via two Perlin channels for smoother “illegal” motion
        float jx = Mathf.PerlinNoise(noiseSeed + 20.0f, t * jitterSpeed) * 2f - 1f;
        float jy = Mathf.PerlinNoise(noiseSeed + 30.0f, t * jitterSpeed) * 2f - 1f;
        float jz = Mathf.PerlinNoise(noiseSeed + 40.0f, t * jitterSpeed) * 2f - 1f;

        Vector3 jitter = new Vector3(jx, jy, jz) * jitterAmplitude;

        // Keep it centered around starting position
        transform.position = _startPos + jitter;
    }

    void ApplyGlobals()
    {
        Camera cam = referenceCamera ? referenceCamera : Camera.main;
        if (!cam) return;

        Vector3 worldPos = transform.position;
        Vector3 vp = cam.WorldToViewportPoint(worldPos);

        // Pack as float4 so it’s easy to read in shaders:
        // world: xyz + 1
        // viewport: xy + zDepth + 1
        Shader.SetGlobalVector(globalWorldPosName, new Vector4(worldPos.x, worldPos.y, worldPos.z, 1f));
        Shader.SetGlobalVector(globalViewportPosName, new Vector4(vp.x, vp.y, vp.z, 1f));
    }

    // Optional: call this if you move the core via other scripts and want jitter centered on new location.
    public void RecenterJitterOrigin()
    {
        _startPos = transform.position;
    }
}
