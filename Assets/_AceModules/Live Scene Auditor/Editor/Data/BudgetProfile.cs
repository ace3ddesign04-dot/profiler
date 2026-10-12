using System;
using UnityEngine;

namespace AceModules.LiveSceneAuditor
{
    [Serializable]
    public class BudgetProfile
    {
        [Header("Geometry Budgets (Visible View)")]
        public int maxTriangles = 200000;
        public int maxVertices = 200000;
        
        [Header("Materials & Assets Budgets")]
        public int maxMaterialsInScene = 40;
        public int maxFbxInScene = 50;
        
        [Header("Texture Budgets")]
        [Tooltip("Based on 512x512 = 1.0 score. 1024x1024 = 4.0, 2048x2048 = 16.0")]
        public float maxTextureScore = 40.0f;

        [Header("Single Asset Thresholds")]
        public int singleMeshHeavyTriangles = 8000;
        public int maxMaterialsPerObject = 2;
        public int requireLodAboveTriangles = 4000;

        [Header("Lighting Budgets")]
        public int maxDirectionalLights = 1;
        public int maxPointLights = 4;
        public int maxSpotLights = 4;
        public int maxTotalLights = 8;
        public int maxRealtimeShadowCasters = 1;

        public static BudgetProfile GetDefault()
        {
            return new BudgetProfile();
        }
    }
}
