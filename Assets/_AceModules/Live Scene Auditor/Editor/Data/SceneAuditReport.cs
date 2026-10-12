using System;
using System.Collections.Generic;
using UnityEngine;

namespace AceModules.LiveSceneAuditor
{
    public enum CulpritType
    {
        MeshGeometry,
        TextureSize,
        MultiMaterial,
        MissingLOD,
        Light
    }

    [Serializable]
    public class CulpritItem
    {
        public string name;
        public int instanceId;
        public CulpritType type;
        public string primaryMetricText;
        public string secondaryDetailText;
        public float severityScore; // For sorting highest impact to lowest
        public int triangleCount;
        public int vertexCount;
        public float textureScore;
        public int textureWidth;
        public int textureHeight;
        public string shaderName;
        public bool isHighPoly;
        public bool isMultiMaterial;
        public bool isMissingLod;
        public List<string> flaggedReasons = new List<string>();
        public List<string> dependencyNames = new List<string>();
        public string fbxAssetPath;
        public string fbxName;
        public int fbxOccurrences;
        public int fbxVisibleOccurrences;
        public List<int> fbxInstanceRootIds = new List<int>();

        // Light Metrics
        public LightType lightType;
        public LightShadows lightShadows;
        public float lightRange;
        public float lightIntensity;
        public bool lightIsRealtime;
        public bool lightIsBaked;
        public bool lightIsMixed;
        public int lightOccurrences = 1;
        public int lightVisibleOccurrences = 0;
        public List<int> lightInstanceIds = new List<int>();
        public List<string> lightNames = new List<string>();
    }

    [Serializable]
    public class AtlasCandidateItem
    {
        public string materialName;
        public int materialInstanceId;
        public string rendererName;
        public int instanceId; // Backwards compatible / primary instance ID
        public string textureName;
        public int textureInstanceId;
        public int width;
        public int height;
        public float textureScore;
        public int rendererUsageCount;
        public List<int> rendererInstanceIds = new List<int>();
        public List<string> rendererNames = new List<string>();
        public bool isVisibleInView;
        public int visibleRendererUsageCount;
        public List<int> visibleRendererInstanceIds = new List<int>();
        public List<string> visibleRendererNames = new List<string>();
    }

    [Serializable]
    public class AtlasGroupSuggestion
    {
        public string groupTitle;
        public string shaderName;
        public List<AtlasCandidateItem> candidates = new List<AtlasCandidateItem>();
        public int potentialMaterialSavings;
        public float currentCombinedTextureScore;
        public string suggestedAtlasSize;
        public int totalRenderersCount;
        public bool isFoldedOut = true;
    }

    [Serializable]
    public class SceneAuditReport
    {
        public DateTime scanTimestamp = DateTime.Now;
        public float scanDurationMs = 0f;
        public bool isFrustumViewOnly = false;

        // Geometry Metrics
        public int visibleTriangles = 0;
        public int visibleVertices = 0;
        public int totalSceneTriangles = 0;
        public int totalSceneVertices = 0;

        // Material Metrics
        public int uniqueMaterialsCount = 0;

        // Texture & Model Metrics
        public float textureAreaScore = 0f;
        public int fbxModelCount = 0;

        // Lighting Metrics
        public int totalLightsCount = 0;
        public int directionalLightsCount = 0;
        public int pointLightsCount = 0;
        public int spotLightsCount = 0;
        public int areaLightsCount = 0;
        public int otherLightsCount = 0;

        public int realtimeLightsCount = 0;
        public int bakedLightsCount = 0;
        public int mixedLightsCount = 0;
        public int shadowCasterLightsCount = 0;

        public int visibleLightsCount = 0;
        public int inactiveLightsCount = 0;

        // Culprits & Suggestions
        public List<CulpritItem> culprits = new List<CulpritItem>();
        public List<AtlasGroupSuggestion> atlasSuggestions = new List<AtlasGroupSuggestion>();

        // Viewpoints / Vantage Points Metrics
        public List<ViewpointAuditStat> viewpointStats = new List<ViewpointAuditStat>();

        // Overall Health Status
        public float overallHealthPercentage = 100f; // 100% means right at budget, < 100% within, > 100% over
        public bool isOverBudget = false;
        public string statusSummary = "Ready";
    }

    [Serializable]
    public class ViewpointAuditStat
    {
        public string viewpointName;
        public int transformInstanceId;
        public Vector3 position;
        public Vector3 rotationEuler;

        // Geometry Metrics in View Frustum
        public int visibleTriangles = 0;
        public int visibleVertices = 0;
        public int visibleRenderersCount = 0;

        // Material Metrics in View Frustum
        public int uniqueMaterialsCount = 0;
        public List<string> materialNames = new List<string>();

        // Lighting Metrics in View Frustum
        public int visibleLightsCount = 0;
        public int directionalLightsCount = 0;
        public int pointLightsCount = 0;
        public int spotLightsCount = 0;
        public int shadowCasterLightsCount = 0;

        // Budget & Status for this viewpoint
        public float healthPercentage = 100f;
        public bool isOverBudget = false;
        public string statusSummary = "PASS";

        // Top Culprit visible in this viewpoint
        public string topCulpritName = "None";
        public int topCulpritTris = 0;
        public float topCulpritSeverity = 0f;

        // Contributing objects inside this view frustum (for expandable rows & pinging)
        public List<ViewpointContributingObject> contributingRenderers = new List<ViewpointContributingObject>();
        public List<ViewpointContributingLight> contributingLights = new List<ViewpointContributingLight>();
    }

    [Serializable]
    public class ViewpointContributingObject
    {
        public int instanceId;
        public string name;
        public int triangleCount;
        public int vertexCount;
        public int materialCount;
        public List<int> materialInstanceIds = new List<int>();
        public List<string> materialNames = new List<string>();
        public List<int> textureInstanceIds = new List<int>();
        public List<string> textureNames = new List<string>();
        public bool isModelAsset;
        public string fbxName;
        public string fbxAssetPath;
    }

    [Serializable]
    public class ViewpointContributingLight
    {
        public int instanceId;
        public string name;
        public LightType type;
        public LightShadows shadows;
        public bool isRealtime;
        public bool isBaked;
        public bool isMixed;
        public float range;
        public float intensity;
    }
}
