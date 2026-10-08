using System;
using UnityEngine;

namespace AceModules.LiveSceneAuditor
{
    [Serializable]
    public class BudgetProfile
    {
        public string profileName = "Low-End Mobile";
        
        [Header("Geometry Budgets (Visible View)")]
        public int maxTriangles = 200000;
        public int maxVertices = 200000;
        
        [Header("Rendering Budgets")]
        public int maxBatches = 80;
        public int maxMaterialsInScene = 40;
        
        [Header("Texture Budgets")]
        [Tooltip("Based on 512x512 = 1.0 score. 1024x1024 = 4.0, 2048x2048 = 16.0")]
        public float maxTextureScore = 40.0f;
        
        [Header("Asset File Counts")]
        public int maxFbxInScene = 50;

        [Header("Single Asset Thresholds")]
        public int singleMeshHeavyTriangles = 8000;
        public int maxMaterialsPerObject = 2;
        public int requireLodAboveTriangles = 4000;

        public static BudgetProfile GetDefaultLowEnd()
        {
            return new BudgetProfile
            {
                profileName = "Low-End Mobile (Adreno 610 / Mali-G52)",
                maxTriangles = 180000,
                maxVertices = 180000,
                maxBatches = 70,
                maxMaterialsInScene = 35,
                maxTextureScore = 35f,
                maxFbxInScene = 40,
                singleMeshHeavyTriangles = 8000,
                maxMaterialsPerObject = 2,
                requireLodAboveTriangles = 4000
            };
        }

        public static BudgetProfile GetDefaultMidRange()
        {
            return new BudgetProfile
            {
                profileName = "Mid-Range Mobile (Adreno 640 / Mali-G77)",
                maxTriangles = 350000,
                maxVertices = 350000,
                maxBatches = 140,
                maxMaterialsInScene = 60,
                maxTextureScore = 80f,
                maxFbxInScene = 80,
                singleMeshHeavyTriangles = 18000,
                maxMaterialsPerObject = 3,
                requireLodAboveTriangles = 8000
            };
        }

        public static BudgetProfile GetDefaultHighEnd()
        {
            return new BudgetProfile
            {
                profileName = "High-End Mobile (Adreno 730+ / Mali-G715+)",
                maxTriangles = 600000,
                maxVertices = 600000,
                maxBatches = 250,
                maxMaterialsInScene = 100,
                maxTextureScore = 150f,
                maxFbxInScene = 120,
                singleMeshHeavyTriangles = 30000,
                maxMaterialsPerObject = 4,
                requireLodAboveTriangles = 15000
            };
        }
    }
}
