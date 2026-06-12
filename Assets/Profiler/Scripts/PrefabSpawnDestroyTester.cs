using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PrefabSpawnDestroyTester : MonoBehaviour {
    [Header("Prefab")]
    public GameObject prefab;

    [Header("Spawn Settings")]
    public bool autoStart = true;
    public float spawnInterval = 0.05f;
    public int spawnCountPerTick = 1;
    public int maxAliveObjects = 100;

    [Header("Destroy Settings")]
    public float destroyAfterSeconds = 2f;

    [Header("Position Randomization")]
    public Vector3 spawnCenter = Vector3.zero;
    public Vector3 randomRange = new Vector3(5f, 0f, 5f);

    private readonly List<GameObject> aliveObjects = new List<GameObject>();
    private Coroutine spawnRoutine;

    void Start() {
        if (autoStart)
            StartSpawning();
    }

    public void StartSpawning() {
        if (spawnRoutine == null)
            spawnRoutine = StartCoroutine(SpawnLoop());
    }

    public void StopSpawning() {
        if (spawnRoutine != null) {
            StopCoroutine(spawnRoutine);
            spawnRoutine = null;
        }
    }

    IEnumerator SpawnLoop() {
        while (true) {
            for (int i = 0; i < spawnCountPerTick; i++) {
                SpawnOne();
            }

            yield return new WaitForSeconds(spawnInterval);
        }
    }

    void SpawnOne() {
        if (prefab == null) {
            Debug.LogWarning("Prefab is not assigned.");
            return;
        }

        CleanupNullReferences();

        if (aliveObjects.Count >= maxAliveObjects)
            return;

        Vector3 randomOffset = new Vector3(
            Random.Range(-randomRange.x, randomRange.x),
            Random.Range(-randomRange.y, randomRange.y),
            Random.Range(-randomRange.z, randomRange.z)
        );

        Vector3 spawnPos = spawnCenter + randomOffset;

        GameObject obj = Instantiate(prefab, spawnPos, Quaternion.identity);
        aliveObjects.Add(obj);

        StartCoroutine(DestroyAfterDelay(obj, destroyAfterSeconds));
    }

    IEnumerator DestroyAfterDelay(GameObject obj, float delay) {
        yield return new WaitForSeconds(delay);

        if (obj != null) {
            aliveObjects.Remove(obj);
            Destroy(obj);
        }
    }

    void CleanupNullReferences() {
        aliveObjects.RemoveAll(item => item == null);
    }

    public void DestroyAllAlive() {
        for (int i = aliveObjects.Count - 1; i >= 0; i--) {
            if (aliveObjects[i] != null)
                Destroy(aliveObjects[i]);
        }

        aliveObjects.Clear();
    }

    void OnDisable() {
        StopSpawning();
    }
}