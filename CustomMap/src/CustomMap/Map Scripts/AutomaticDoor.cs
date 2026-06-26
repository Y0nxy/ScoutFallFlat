using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Zorro.Core;

public class AutomaticDoor : MonoBehaviour
{
    [SerializeField] GameObject Left;
    [SerializeField] GameObject Right;
    [SerializeField] List<Light> Indicators = new List<Light>();
    public List<PressurePlate> CombinedSignals = new List<PressurePlate>();
    public float moveSpeed = 2f; // How fast the door opens/closes
    public bool isOpen = false;
    public bool isCombinedSignal = false;
    private Coroutine doorMovementCoroutine;
    private void Awake()
    {
        if (Left == null || Right == null)
        {
            Left = transform.Find("Left").gameObject;
            Right = transform.Find("Right").gameObject;
        }
        //Find all Indicators for the door and add the to the list.
        foreach (Light Lit in transform.parent.GetComponentsInChildren<Light>())
        {
            if (Lit.transform.parent.name.Equals("Indicator") || Lit.transform.parent.name.Equals("ExitSign"))
            {
                Lit.enabled = isOpen; // Set initial state of the light
                Indicators.Add(Lit);
            }
        }
        foreach (Transform child in transform.parent)
        {
            if (child.name.Contains("SignalCombine"))
            {
                isCombinedSignal = true;
                break;
            }
        }
        if (isCombinedSignal) checkCombinedSignal();
    }

    public void ToggleDoor(bool toOpen = true)
    {
        if (isCombinedSignal)
        {
            if (CombinedSignals.Count < 2) checkCombinedSignal();
            foreach (PressurePlate plate in CombinedSignals)
            {
                if (!plate.isPressed) return;
            }
            foreach (PressurePlate plate in CombinedSignals)
            {
                Destroy(plate);
            }
        }
        
        isOpen = !isOpen;
        if (isOpen != toOpen) isOpen = toOpen;
        Vector3 leftTarget = isOpen ? new Vector3(0, 0, 0.8f) : Vector3.zero;
        Vector3 rightTarget = isOpen ? new Vector3(0, 0, -0.8f) : Vector3.zero;
        foreach (Light indicator in Indicators)
        {
            indicator.enabled = isOpen;
        }
        // If the door is currently moving, stop it so we can reverse direction cleanly
        if (doorMovementCoroutine != null)
        {
            StopCoroutine(doorMovementCoroutine);
        }
        doorMovementCoroutine = StartCoroutine(MoveDoors(leftTarget, rightTarget));
    }
    private IEnumerator MoveDoors(Vector3 leftTarget, Vector3 rightTarget)
    {
        if (Left == null || Right == null)
        {
            Left = transform.Find("Left").gameObject;
            Right = transform.Find("Right").gameObject;
        }
        // Loop runs as long as the doors haven't reached their targets
        while (Left.transform.localPosition != leftTarget || Right.transform.localPosition != rightTarget)
        {
            // MoveTowards gradually shifts the position at a constant speed
            Left.transform.localPosition = Vector3.MoveTowards(Left.transform.localPosition, leftTarget, moveSpeed * Time.deltaTime);
            Right.transform.localPosition = Vector3.MoveTowards(Right.transform.localPosition, rightTarget, moveSpeed * Time.deltaTime);

            // 'yield return null' tells Unity to pause this method here, render the frame, 
            // and come back to this spot on the next frame. This creates the animation effect.
            yield return null;
        }
    }
    private void checkCombinedSignal()
    {
        foreach (PressurePlate btn in transform.parent.GetComponentsInChildren<PressurePlate>())
        {
            CombinedSignals.Add(btn);
            btn.isDependant = true;
        }
    }
}
