using UnityEngine;
using UnityEngine.PlayerLoop;

public class ElevatorButton : MonoBehaviour
{
    public ElevatorLinear elevator;
    Vector3 ogPos;
    int objectsOnPlate = 0;
    float timeSincePressed = 0;
    private void Awake()
    {
        ogPos = transform.localPosition;
    }
    void Update()
    {
        timeSincePressed += Time.deltaTime;
    }
    private void OnTriggerEnter(Collider other)
    {
        if (elevator == null)
        {
            elevator = transform.parent.parent.GetComponent<ElevatorLinear>();
            if (elevator == null)
                elevator = transform.parent.parent.GetComponentInChildren<ElevatorLinear>();
        }
        ;
        if (other.gameObject.layer == LayerMask.NameToLayer("Character"))
        {
            objectsOnPlate++;
            Debug.LogWarning("Valid Elevator button pressed by: " + other.gameObject.name);
            if (objectsOnPlate != 1) return;
            transform.localPosition = transform.localPosition - new Vector3(0.06f, 0, 0);
            if (timeSincePressed < 0.5f) return;
            elevator.ToggleElevator();
            timeSincePressed = 0;
        }
    }
    private void OnTriggerExit(Collider other)
    {
        if (other.gameObject.layer == LayerMask.NameToLayer("Character"))
        {
            objectsOnPlate--;
            if (objectsOnPlate < 0) objectsOnPlate = 0;
            if (objectsOnPlate != 0) return;
            Debug.Log("Valid Exit other is " + other.gameObject.name);
            transform.localPosition = ogPos;
        }
    }
}
