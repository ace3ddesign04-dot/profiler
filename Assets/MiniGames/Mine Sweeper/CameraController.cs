using UnityEngine;

public class CameraController : MonoBehaviour {

    [Header("References")]
    public Camera mainCamera;
    public Transform target;
    public Transform cameraContainer;

    [Header("Board Plane")]
    public float boardHeight = 0f; // Y level of your board surface

    [Header("Optional: Clamp target to board bounds")]
    public bool clampToBounds = true;
    public Vector2 xBounds = new Vector2(-10f, 10f);
    public Vector2 zBounds = new Vector2(-10f, 10f);
    public Vector2 camXBounds = new Vector2(-10f, 10f);
    public Vector2 camZBounds = new Vector2(-10f, 10f);
    public float lerpSpeed = 2f;
    public float deadZoneDistance = 3f;

    public Vector3 mousePosition;
    public Vector3 lastMousePosition;
    public float finalLerpSpeed;
    public bool updateRay = true;
    public Vector3 point;

    void Start() {
        if (mainCamera == null)
            mainCamera = Camera.main;
    }

    void Update() {
        mousePosition = Input.mousePosition;
        Ray ray = mainCamera.ScreenPointToRay(mousePosition);
        Plane plane = new Plane(Vector3.up, new Vector3(0f, boardHeight, 0f));

        if (mousePosition == lastMousePosition) {
            updateRay = false;
        }
        else {
            updateRay = true;
        }

        if (plane.Raycast(ray, out float enter)) {
            //if (updateRay)
                point = ray.GetPoint(enter);

            if (clampToBounds) {
                point.x = Mathf.Clamp(point.x, xBounds.x, xBounds.y);
                point.z = Mathf.Clamp(point.z, zBounds.x, zBounds.y);
            }

            if (Vector3.Distance(cameraContainer.transform.position, target.transform.position) > deadZoneDistance) {
                target.position = Vector3.Lerp(target.position, new Vector3(point.x, boardHeight, point.z), lerpSpeed * Time.deltaTime);
            }
            else {
                target.position = Vector3.Lerp(target.position, new Vector3(point.x, boardHeight, point.z), lerpSpeed / 2 * Time.deltaTime);
            }

            Vector3 camTargetFinalPosition = target.position;
            camTargetFinalPosition.x = Mathf.Clamp(target.position.x, camXBounds.x, camXBounds.y);
            camTargetFinalPosition.z = Mathf.Clamp(target.position.z, camZBounds.x, camZBounds.y);
        }
        lastMousePosition = mousePosition;
    }
}
