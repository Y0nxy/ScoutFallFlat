using pworld.Scripts;
using UnityEngine;

public class RigidBodyStandable : MonoBehaviour
{
    float originalMass = 0f;
    Vector3 originalPos;
    float standableMass = 700f;
    Rigidbody rb = null;
    private int playersOnTop = 0;
    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) this.GetComponent<RigidBodyStandable>().enabled = false;
        originalPos = transform.position;
        originalMass = rb.mass;
    }
    private void OnCollisionEnter(Collision other)
    {
        if (other.gameObject.layer == LayerMask.NameToLayer("Character") &&
            other.gameObject.GetComponent<RigidBodyStandable>() == null)
        {
            foreach (ContactPoint contact in other.contacts)
            {
                // If the collision normal points downward, something is pressing down from above
                if (contact.normal.y < -0.6f)
                {
                    playersOnTop++;
                    rb.mass = standableMass;
                    break;
                }
            }

        }
    }
    private void OnCollisionExit(Collision other)
    {
        if (other.gameObject.layer == LayerMask.NameToLayer("Character") &&
            other.gameObject.GetComponent<RigidBodyStandable>() == null)
        {
            playersOnTop--;
            if (playersOnTop < 0) playersOnTop = 0;
            rb.mass = originalMass;
        }
    }
}
