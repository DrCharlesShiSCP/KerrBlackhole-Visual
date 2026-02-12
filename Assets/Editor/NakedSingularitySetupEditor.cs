// Assets/Editor/NakedSingularitySetupEditor.cs
// Tools > Naked Singularity > Setup Scene (URP)
//
// Fixes Unity 6 URP renderer data access (no scriptableRendererData).
// Avoids huge @"verbatim" shader strings to prevent copy/paste breaking into C#.

#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class NakedSingularitySetupEditor
{
    private const string RootFolder = "Assets/Generated/NakedSingularity";
    private const string ShaderPath = RootFolder + "/SingularityFullscreenDistortion.shader";
    private const string MatPath = RootFolder + "/MAT_SingularityFullscreenDistortion.mat";
    private const string CoreMatPath = RootFolder + "/MAT_SingularityCore_UnlitBright.mat";
    private const string SkyMatPath = RootFolder + "/MAT_Starfield_Unlit.mat";

    [MenuItem("Tools/Naked Singularity/Setup Scene (URP)")]
    public static void SetupSceneURP()
    {
        EnsureFolders();

        var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urpAsset == null)
        {
            EditorUtility.DisplayDialog(
                "URP not active",
                "Current Render Pipeline is not URP.\nProject Settings > Graphics > assign a UniversalRenderPipelineAsset.",
                "OK");
            return;
        }

        EnableOpaqueTexture(urpAsset);

        CreateOrUpdateFullscreenShader();
        var fsMat = CreateOrUpdateFullscreenMaterial();

        var rendererData = GetDefaultUniversalRendererData(urpAsset);
        if (rendererData == null)
        {
            EditorUtility.DisplayDialog(
                "Renderer Data not found",
                "Couldn't find UniversalRendererData from your URP Asset.\n" +
                "This tool tries m_RendererDataList[0], then m_RendererData.\n",
                "OK");
            return;
        }

        bool addedFeature = AddOrUpdateFullScreenPassFeature(rendererData, fsMat);

        var core = CreateOrUpdateSingularityCore();
        var sky = CreateOrUpdateStarfieldSphere();
        var vfx = CreateOrUpdateBrokenAccretionPlaceholder();

        Selection.activeGameObject = core;
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        EditorUtility.DisplayDialog(
            "Done",
            "Setup complete:\n" +
            "- URP Opaque Texture enabled\n" +
            "- Fullscreen distortion shader+material created\n" +
            (addedFeature ? "- Full Screen Pass Renderer Feature added\n" : "- Full Screen Pass Renderer Feature updated\n") +
            "- SingularityCore + StarfieldSphere created/updated\n" +
            "- BrokenAccretionVFX placeholder created/updated\n\n" +
            "Next:\n" +
            "1) Assign star texture to MAT_Starfield_Unlit (_BaseMap)\n" +
            "2) Build VFX Graph for broken accretion & assign to BrokenAccretionVFX\n",
            "OK");
    }

    // ---------- URP toggles ----------
    private static void EnableOpaqueTexture(UniversalRenderPipelineAsset urpAsset)
    {
        var so = new SerializedObject(urpAsset);

        var pRequire = so.FindProperty("m_RequireOpaqueTexture");
        if (pRequire != null && pRequire.propertyType == SerializedPropertyType.Boolean)
        {
            if (!pRequire.boolValue)
            {
                pRequire.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(urpAsset);
            }
            return;
        }

        var pSupports = so.FindProperty("m_SupportsCameraOpaqueTexture");
        if (pSupports != null && pSupports.propertyType == SerializedPropertyType.Boolean)
        {
            if (!pSupports.boolValue)
            {
                pSupports.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(urpAsset);
            }
        }
    }

    // Unity 6 URP: renderer data access via SerializedObject
    private static UniversalRendererData GetDefaultUniversalRendererData(UniversalRenderPipelineAsset urpAsset)
    {
        var so = new SerializedObject(urpAsset);

        var listProp = so.FindProperty("m_RendererDataList");
        if (listProp != null && listProp.isArray && listProp.arraySize > 0)
        {
            var element = listProp.GetArrayElementAtIndex(0);
            return element.objectReferenceValue as UniversalRendererData;
        }

        var singleProp = so.FindProperty("m_RendererData");
        if (singleProp != null)
            return singleProp.objectReferenceValue as UniversalRendererData;

        var altProp = so.FindProperty("m_ScriptableRendererData");
        if (altProp != null)
            return altProp.objectReferenceValue as UniversalRendererData;

        return null;
    }

    // ---------- Shader & material ----------
    private static void CreateOrUpdateFullscreenShader()
    {
        string shaderCode = GetFullscreenShaderCodeSafe();

        File.WriteAllText(ShaderPath, shaderCode);
        AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceUpdate);
    }

    private static Material CreateOrUpdateFullscreenMaterial()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null)
            throw new Exception("Failed to load shader at: " + ShaderPath);

        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null)
        {
            mat = new Material(shader) { name = "MAT_SingularityFullscreenDistortion" };
            AssetDatabase.CreateAsset(mat, MatPath);
        }
        else
        {
            mat.shader = shader;
            EditorUtility.SetDirty(mat);
        }

        mat.SetVector("_CenterUV", new Vector4(0.5f, 0.5f, 0, 0));
        mat.SetFloat("_LensStrength", 0.18f);
        mat.SetFloat("_Power", 1.75f);
        mat.SetFloat("_Epsilon", 0.002f);
        mat.SetFloat("_SwirlStrength", 0.35f);
        mat.SetFloat("_SwirlFalloff", 4.0f);
        mat.SetFloat("_Chromatic", 0.0035f);
        mat.SetFloat("_RingFreq", 18.0f);
        mat.SetFloat("_RingAmp", 0.012f);
        mat.SetFloat("_RingFalloff", 4.5f);
        mat.SetFloat("_UseGlobalCenter", 1.0f);

        AssetDatabase.SaveAssets();
        return mat;
    }

    // ---------- Renderer Feature ----------
    private static bool AddOrUpdateFullScreenPassFeature(UniversalRendererData rendererData, Material mat)
    {
        FullScreenPassRendererFeature feature = null;
        foreach (var rf in rendererData.rendererFeatures)
        {
            if (rf is FullScreenPassRendererFeature fsp)
            {
                feature = fsp;
                break;
            }
        }

        bool added = false;
        if (feature == null)
        {
            feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
            feature.name = "RF_SingularityFullscreenDistortion";
            rendererData.rendererFeatures.Add(feature);
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            added = true;
        }

        var so = new SerializedObject(feature);

        TrySetEnum(so, "m_InjectionPoint", (int)RenderPassEvent.AfterRenderingPostProcessing);
        TrySetEnum(so, "injectionPoint", (int)RenderPassEvent.AfterRenderingPostProcessing);

        TrySetObject(so, "m_PassMaterial", mat);
        TrySetObject(so, "passMaterial", mat);

        TrySetBool(so, "m_RequireColor", true);
        TrySetBool(so, "requireColor", true);

        TrySetBool(so, "m_IsActive", true);

        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(feature);
        EditorUtility.SetDirty(rendererData);
        AssetDatabase.SaveAssets();

        rendererData.SetDirty();
        return added;
    }

    private static void TrySetEnum(SerializedObject so, string propName, int enumValue)
    {
        var p = so.FindProperty(propName);
        if (p != null && p.propertyType == SerializedPropertyType.Enum)
            p.enumValueIndex = enumValue;
    }

    private static void TrySetObject(SerializedObject so, string propName, UnityEngine.Object obj)
    {
        var p = so.FindProperty(propName);
        if (p != null && p.propertyType == SerializedPropertyType.ObjectReference)
            p.objectReferenceValue = obj;
    }

    private static void TrySetBool(SerializedObject so, string propName, bool value)
    {
        var p = so.FindProperty(propName);
        if (p != null && p.propertyType == SerializedPropertyType.Boolean)
            p.boolValue = value;
    }

    // ---------- Scene objects ----------
    private static GameObject CreateOrUpdateSingularityCore()
    {
        var go = GameObject.Find("SingularityCore");
        if (go == null)
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "SingularityCore";
            go.transform.position = Vector3.zero;
            go.transform.localScale = new Vector3(0.35f, 1.25f, 0.35f);
        }

        var col = go.GetComponent<Collider>();
        if (col) UnityEngine.Object.DestroyImmediate(col);

        var coreMat = AssetDatabase.LoadAssetAtPath<Material>(CoreMatPath);
        if (coreMat == null)
        {
            coreMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"))
            {
                name = "MAT_SingularityCore_UnlitBright"
            };
            coreMat.SetColor("_BaseColor", Color.white);
            AssetDatabase.CreateAsset(coreMat, CoreMatPath);
        }

        var rend = go.GetComponent<Renderer>();
        if (rend) rend.sharedMaterial = coreMat;

        // Your runtime script
        var fx = go.GetComponent<SingularityFlicker>();
        if (fx == null) fx = go.AddComponent<SingularityFlicker>();

        fx.targetRenderer = rend;
        fx.usePropertyBlock = true;

        // URP/Unlit uses _BaseColor (not _EmissionColor)
        fx.emissionColorProp = "_BaseColor";
        fx.emissionColor = Color.white;

        fx.baseEmission = 4.5f;
        fx.flickerAmplitude = 3.0f;
        fx.flickerSpeed = 2.2f;
        fx.microFlickerAmplitude = 1.2f;
        fx.microFlickerSpeed = 17f;

        fx.enableScalePulse = true;
        fx.baseScale = 1f;
        fx.pulseAmplitude = 0.06f;
        fx.pulseSpeed = 2.2f;

        fx.enablePositionJitter = true;
        fx.jitterAmplitude = 0.02f;
        fx.jitterSpeed = 8f;

        fx.setGlobalShaderCenter = true;
        fx.referenceCamera = Camera.main;

        fx.globalViewportPosName = "_SingularityViewportPos";
        fx.globalWorldPosName = "_SingularityWorldPos";

        EditorUtility.SetDirty(go);
        return go;
    }

    private static GameObject CreateOrUpdateStarfieldSphere()
    {
        var go = GameObject.Find("StarfieldSphere");
        if (go == null)
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "StarfieldSphere";
            go.transform.position = Vector3.zero;
            go.transform.localScale = new Vector3(-200f, 200f, 200f); // invert

            var col = go.GetComponent<Collider>();
            if (col) UnityEngine.Object.DestroyImmediate(col);
        }

        var skyMat = AssetDatabase.LoadAssetAtPath<Material>(SkyMatPath);
        if (skyMat == null)
        {
            skyMat = new Material(Shader.Find("Universal Render Pipeline/Unlit"))
            {
                name = "MAT_Starfield_Unlit"
            };
            skyMat.SetColor("_BaseColor", new Color(0.02f, 0.02f, 0.03f, 1f));
            AssetDatabase.CreateAsset(skyMat, SkyMatPath);
        }

        var rend = go.GetComponent<Renderer>();
        if (rend) rend.sharedMaterial = skyMat;

        return go;
    }

    private static GameObject CreateOrUpdateBrokenAccretionPlaceholder()
    {
        var go = GameObject.Find("BrokenAccretionVFX");
        if (go == null)
        {
            go = new GameObject("BrokenAccretionVFX");
            go.transform.position = Vector3.zero;
        }

        var vfxType = Type.GetType("UnityEngine.VFX.VisualEffect, Unity.VisualEffectGraph.Runtime");
        if (vfxType == null)
        {
            Debug.LogWarning("Visual Effect Graph package not found. Install 'Visual Effect Graph' to use broken accretion.");
            return go;
        }

        if (go.GetComponent(vfxType) == null)
            go.AddComponent(vfxType);

        return go;
    }

    // ---------- Folder helpers ----------
    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Generated"))
            AssetDatabase.CreateFolder("Assets", "Generated");

        if (!AssetDatabase.IsValidFolder(RootFolder))
            AssetDatabase.CreateFolder("Assets/Generated", "NakedSingularity");
    }

    // ---------- Safe shader code builder (no verbatim string) ----------
    private static string GetFullscreenShaderCodeSafe()
    {
        // IMPORTANT: This assumes URP Full Screen Pass provides _BlitTexture.
        // If your URP version uses a different name, tell me your URP version and I’ll adjust.
        return string.Join("\n", new[]
        {
            "Shader \"Hidden/NakedSingularity/FullscreenDistortion\"",
            "{",
            "    Properties",
            "    {",
            "        _CenterUV (\"Center UV\", Vector) = (0.5, 0.5, 0, 0)",
            "        _LensStrength (\"Lens Strength\", Float) = 0.18",
            "        _Power (\"Power\", Float) = 1.75",
            "        _Epsilon (\"Epsilon\", Float) = 0.002",
            "        _SwirlStrength (\"Swirl Strength\", Float) = 0.35",
            "        _SwirlFalloff (\"Swirl Falloff\", Float) = 4.0",
            "        _Chromatic (\"Chromatic\", Float) = 0.0035",
            "        _RingFreq (\"Ring Freq\", Float) = 18.0",
            "        _RingAmp (\"Ring Amp\", Float) = 0.012",
            "        _RingFalloff (\"Ring Falloff\", Float) = 4.5",
            "        _UseGlobalCenter (\"Use Global Center (1/0)\", Float) = 1",
            "    }",
            "",
            "    SubShader",
            "    {",
            "        Tags { \"RenderPipeline\"=\"UniversalPipeline\" }",
            "        Pass",
            "        {",
            "            Name \"FullscreenDistortion\"",
            "            ZTest Always ZWrite Off Cull Off",
            "",
            "            HLSLPROGRAM",
            "            #pragma vertex Vert",
            "            #pragma fragment Frag",
            "",
            "            #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"",
            "",
            "            TEXTURE2D(_BlitTexture);",
            "            SAMPLER(sampler_BlitTexture);",
            "",
            "            float4 _CenterUV;",
            "            float _LensStrength;",
            "            float _Power;",
            "            float _Epsilon;",
            "            float _SwirlStrength;",
            "            float _SwirlFalloff;",
            "            float _Chromatic;",
            "            float _RingFreq;",
            "            float _RingAmp;",
            "            float _RingFalloff;",
            "            float _UseGlobalCenter;",
            "",
            "            float4 _SingularityViewportPos;",
            "",
            "            struct Attributes",
            "            {",
            "                float4 positionOS : POSITION;",
            "                float2 uv         : TEXCOORD0;",
            "            };",
            "",
            "            struct Varyings",
            "            {",
            "                float4 positionHCS : SV_POSITION;",
            "                float2 uv          : TEXCOORD0;",
            "            };",
            "",
            "            Varyings Vert (Attributes IN)",
            "            {",
            "                Varyings OUT;",
            "                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);",
            "                OUT.uv = IN.uv;",
            "                return OUT;",
            "            }",
            "",
            "            float2 Rotate2D(float2 p, float a)",
            "            {",
            "                float s = sin(a);",
            "                float c = cos(a);",
            "                return float2(p.x * c - p.y * s, p.x * s + p.y * c);",
            "            }",
            "",
            "            half4 SampleScene(float2 uv)",
            "            {",
            "                return SAMPLE_TEXTURE2D(_BlitTexture, sampler_BlitTexture, uv);",
            "            }",
            "",
            "            half4 Frag (Varyings IN) : SV_Target",
            "            {",
            "                float2 uv = IN.uv;",
            "",
            "                float2 center = _CenterUV.xy;",
            "                if (_UseGlobalCenter > 0.5)",
            "                {",
            "                    center = _SingularityViewportPos.xy;",
            "                }",
            "",
            "                float2 dir = uv - center;",
            "                float r = length(dir);",
            "                float inv = 1.0 / (pow(max(r, 1e-6), _Power) + _Epsilon);",
            "                float2 nDir = (r > 1e-6) ? (dir / r) : float2(0, 0);",
            "",
            "                float2 radialOffset = nDir * (_LensStrength * inv);",
            "                float2 uv1 = uv + radialOffset;",
            "",
            "                float swirlMask = saturate(1.0 - r * _SwirlFalloff);",
            "                float angle = _SwirlStrength * inv * swirlMask;",
            "                float2 dirRot = Rotate2D(dir, angle);",
            "                float2 uv2 = center + dirRot;",
            "",
            "                float2 uvFinal = lerp(uv1, uv2, swirlMask);",
            "",
            "                float ring = sin(r * _RingFreq) * _RingAmp;",
            "                float ringFade = 1.0 / (1.0 + r * _RingFalloff);",
            "                uvFinal += nDir * (ring * ringFade);",
            "",
            "                float ch = _Chromatic * inv;",
            "                float2 uvR = uvFinal + nDir * ch;",
            "                float2 uvB = uvFinal - nDir * ch;",
            "",
            "                half4 colR = SampleScene(uvR);",
            "                half4 colG = SampleScene(uvFinal);",
            "                half4 colB = SampleScene(uvB);",
            "",
            "                half3 outRGB = half3(colR.r, colG.g, colB.b);",
            "                return half4(outRGB, 1);",
            "            }",
            "            ENDHLSL",
            "        }",
            "    }",
            "}",
        });
    }
}
#endif
