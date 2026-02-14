using UnityEngine;

public class Rotator : MonoBehaviour
{
    [Tooltip("Degrees per second rotation around each axis")]
    public Vector3 degreesPerSecond = new Vector3(0f, 90f, 0f);

    void Update()
    {
        // Continuous rotation using unscaled time
        transform.Rotate(degreesPerSecond * Time.unscaledDeltaTime, Space.Self);
    }
}
