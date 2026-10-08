using UnityEngine;

public class FollowMouse : MonoBehaviour {
    public Camera _camera;
    [SerializeField] private Transform target;
    [SerializeField] private float zDistance = 10f;


    private void Awake() {
        _camera = Camera.main;
    }

    private void Update() {
        if (target == null)
            return;

        Vector3 mousePosition = Input.mousePosition;
        mousePosition.z = zDistance;

        target.position = _camera.ScreenToWorldPoint(mousePosition);
    }
}