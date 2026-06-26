using UnityEngine;

public class PressurePlate : MonoBehaviour
{
    [SerializeField] Light lit = null;
    public bool isPressed = false;
    public bool isDependant = false;
    public int objectsOnPlate = 0;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Awake()
    {
        if (lit != null) return;
        lit = transform.GetComponentInChildren<Light>();
        lit.enabled = isPressed;
        lit.intensity = 25f;
        if (!gameObject.name.Contains("Button_Apply"))
            GetComponent<Renderer>().material.DisableKeyword("_EMISSION");
        //lit.enabled = isPressed;
    }
    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.layer == LayerMask.NameToLayer("Character"))
        {
            objectsOnPlate++;
            Debug.LogWarning("Pressure plate pressed by: " + other.gameObject.name);
            if (objectsOnPlate != 1) return;
            isPressed = true;
            lit.enabled = isPressed;
            ToggleDoors(isPressed);
            if (gameObject.name.Contains("Button_Apply"))
            {
                transform.localPosition = new Vector3(0.06f, 0, 0);
                if (!isDependant) Destroy(this);
            }
            else
            {
                GetComponent<Renderer>().material.EnableKeyword("_EMISSION");
                transform.localPosition = new Vector3(0, -0.05f, 0);
            }
        }
    }
    private void OnTriggerExit(Collider other)
    {
        if (other.gameObject.layer == LayerMask.NameToLayer("Character"))
        {
            objectsOnPlate--;
            if (objectsOnPlate < 0) objectsOnPlate = 0;
            Debug.LogWarning("Pressure plate released by: " + other.gameObject.name);
            if (objectsOnPlate != 0) return;
            isPressed = false;
            ToggleDoors(isPressed);
            lit.enabled = isPressed;
            if (!gameObject.name.Contains("Button_Apply"))
            {
                GetComponent<Renderer>().material.DisableKeyword("_EMISSION");
                transform.localPosition = new Vector3(0, 0.1f, 0);
            }
            else
            {
                transform.localPosition = new Vector3(0, 0, 0);
            }
        }
    }
    void ToggleDoors(bool toOpen = true)
    {
        foreach (AutomaticDoor door in transform.parent.parent.GetComponentsInChildren<AutomaticDoor>())
        {
            door.ToggleDoor(toOpen);
        }
    }
}
