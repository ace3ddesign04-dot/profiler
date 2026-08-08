using UnityEngine;

namespace Julliretta
{
    public class RotateObject : MonoBehaviour
    {
        public float rotationSpeed = 30.0f;

        void Update()
        {

            float deltaTime = Time.deltaTime;

            float angle = rotationSpeed * deltaTime;

            transform.Rotate(Vector3.up, angle);
        }
    }
}
