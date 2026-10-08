using System;
using System.Collections.Generic;
using UnityEngine;

namespace AceModules.LiveSceneAuditor
{
    public enum CulpritType
    {
        MeshGeometry,
        TextureSize,
        MaterialBatchBreaker,
        MissingLOD
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
        public string shaderName;
        public bool isHighPoly;
        public bool isMultiMaterial;
        public bool isMissingLod;
        public List<string> flaggedReasons = new List<string>();
        public List<string> dependencyNames = new List<string>();
    }

    [Serializable]
    public class AtlasCandidateItem
    {
        public string rendererName;
        public int instanceId;
        public string textureName;
        public int width;
        public int height;
        public float textureScore;
    }

    [Serializable]
    public class AtlasGroupSuggestion
    {
        public string groupTitle;
        public string shaderName;
        public List<AtlasCandidateItem> candidates = new List<AtlasCandidateItem>();
        public int estimatedDrawCallSavings;
        public float currentCombinedTextureScore;
        public string suggestedAtlasSize;
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

        // Render & Batch Metrics
        public int estimatedBatches = 0;
        public int uniqueMaterialsCount = 0;
        public int savedByBatching = 0;
        public int rawUnbatchedBatches = 0;
        public int rawEditModeBatches = 0;
        public int staticBatchedObjectsCount = 0;

        // Texture & Model Metrics
        public float textureAreaScore = 0f;
        public int fbxModelCount = 0;

        // Culprits & Suggestions
        public List<CulpritItem> culprits = new List<CulpritItem>();
        public List<AtlasGroupSuggestion> atlasSuggestions = new List<AtlasGroupSuggestion>();

        // Overall Health Status
        public float overallHealthPercentage = 100f; // 100% means right at budget, < 100% within, > 100% over
        public bool isOverBudget = false;
        public string statusSummary = "Ready";
    }
}
