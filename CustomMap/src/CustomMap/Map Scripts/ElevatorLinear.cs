using System.Collections;
using UnityEngine;

public class ElevatorLinear : MonoBehaviour
{
    [SerializeField] GameObject Platform;
    [SerializeField] GameObject ElevatorControl;
    bool isUp = false;
    public float moveSpeed = 2f;
    Vector3 OriginalPosPlatform;
    Vector3 OriginalPosElevatorControl;
    private Coroutine ElevMovementCoroutine;
    float Height= -1f;
    private void Awake()
    {
        if (Platform == null) Platform = transform.Find("Platform").gameObject;
        if (ElevatorControl == null) ElevatorControl = transform.Find("ElevatorControl").gameObject;
        Height = Platform.GetComponentInChildren<CapsuleCollider>().height;
        Platform.GetComponent<Rigidbody>().isKinematic = true;
        ElevatorControl.GetComponent<Rigidbody>().isKinematic = true;
        OriginalPosPlatform = Platform.transform.position;
        OriginalPosElevatorControl = ElevatorControl.transform.position;
    }
    public void ToggleElevator()
    {
        isUp = !isUp;
        Vector3 PlatformTarget = isUp ? OriginalPosPlatform+ new Vector3(0, Height, 0) : OriginalPosPlatform;
        Vector3 ElevatorControlTarget = isUp ? OriginalPosElevatorControl+ new Vector3(0, Height, 0) : OriginalPosElevatorControl;
        // If the door is currently moving, stop it so we can reverse direction cleanly
        if (ElevMovementCoroutine != null)
        {
            StopCoroutine(ElevMovementCoroutine);
        }
        ElevMovementCoroutine = StartCoroutine(MoveElevator(PlatformTarget, ElevatorControlTarget));
    }

    private IEnumerator MoveElevator(Vector3 TargetPosPlatform, Vector3 TargetPosElevatorControl)
    {
        // Loop runs as long as the doors haven't reached their targets
        while (Platform.transform.position != TargetPosPlatform && ElevatorControl.transform.position != TargetPosElevatorControl)
        {
            // MoveTowards gradually shifts the position at a constant speed
            Platform.transform.position = Vector3.MoveTowards(Platform.transform.position, TargetPosPlatform, moveSpeed * Time.deltaTime);
            ElevatorControl.transform.position = Vector3.MoveTowards(ElevatorControl.transform.position, TargetPosElevatorControl, moveSpeed * Time.deltaTime);
            // 'yield return null' tells Unity to pause this method here, render the frame, 
            // and come back to this spot on the next frame. This creates the animation effect.
            yield return null;
        }
    }
}
