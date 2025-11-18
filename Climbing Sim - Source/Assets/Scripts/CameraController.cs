using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public class CameraController : MonoBehaviour
{

    public Transform target;
    public Transform wallTarget;
    public float rotateSpeed;
    public float heading;
    public float pitch;
    public float playerDistance = 8;
    public float targetHeightOffset = 1f;
    public float minStartDistance = 6f;
    public float minVerticalOffset = 0f;
    public float startVerticalOffset = 1.5f;
    public float wallFramingPadding = 0.5f;

    public float minViewAngle = 45;
    public float maxViewAngle = 315;

    public bool invertY;

    private Camera cachedCamera;

    // Start is called before the first frame update
    private void Awake()
    {
        cachedCamera = GetComponentInChildren<Camera>();
    }

    void Start()
    {
        if (targetHeightOffset <= 0f)
        {
            targetHeightOffset = 1f;
        }

        if (target != null && wallTarget != null)
        {
            AlignCameraToWall();
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
    private void LateUpdate()
    {

        Cursor.lockState = CursorLockMode.Locked;
        //Get the x position of the mouse at rotate the target. 
        float horizontal = Input.GetAxis("Mouse X") * rotateSpeed; // Mouse X is horizontal.

        //Get Y position of the mouse and rotate the pivot
        float vertical = Input.GetAxis("Mouse Y") * rotateSpeed;

        //inputs
        heading += Input.GetAxis("Mouse X") * Time.deltaTime * 180;
        pitch -= Input.GetAxis("Mouse Y") * Time.deltaTime * 180;

        if (pitch < -20) pitch = -20;
        else if (pitch > 70) pitch = 70;

        Vector3 parentRot = Vector3.zero;
        Vector3 cameraRot = Vector3.zero;

        parentRot.y = heading - (float)System.Math.PI;
        cameraRot.x = pitch;

        transform.rotation = Quaternion.Euler(parentRot);
        transform.GetChild(0).localRotation = Quaternion.Euler(cameraRot);

        Vector3 newPos = target.position + Vector3.up * targetHeightOffset;

        float headingRadians = heading * ((float)System.Math.PI / 180) - (float)System.Math.PI;
        float pitchRadians = pitch * ((float)System.Math.PI / 180) /*- (float)System.Math.PI*/;

        float newPosX = 0;
        float newPosZ = 0;
        newPosX = (float)System.Math.Sin(headingRadians) * playerDistance;
        newPosZ = (float)System.Math.Cos(headingRadians) * playerDistance;

        newPosX *= (float)System.Math.Cos(pitchRadians);
        newPosZ *= (float)System.Math.Cos(pitchRadians);
        newPos.y += (float)System.Math.Sin(pitchRadians) * playerDistance + 0;
        newPos.x += newPosX;
        newPos.z += newPosZ;
        gameObject.transform.position = newPos;

    }

    private void AlignCameraToWall()
    {
        Vector3 pivot = target.position + Vector3.up * targetHeightOffset;
        Bounds wallBounds = CalculateBounds(wallTarget);
        Vector3 wallCenter = wallBounds.center;
        float wallHeight = Mathf.Max(wallBounds.size.y, 1f);

        Vector3 toWall = wallCenter - pivot;
        Vector3 horizontalToWall = Vector3.ProjectOnPlane(toWall, Vector3.up);
        float distancePlayerToWall = horizontalToWall.magnitude;

        Vector3 wallForward = Vector3.ProjectOnPlane(wallTarget.forward, Vector3.up);
        if (wallForward.sqrMagnitude < 0.0001f)
        {
            wallForward = distancePlayerToWall > 0.0001f ? -horizontalToWall : Vector3.forward;
        }

        float playerSide = Mathf.Sign(Vector3.Dot(pivot - wallCenter, wallForward));
        if (Mathf.Abs(playerSide) < 0.0001f)
        {
            playerSide = 1f;
        }

        Vector3 facingDir = wallForward.normalized * playerSide;
        float desiredDistance = RequiredDistanceForHeight(wallHeight);
        float horizontalBack = Mathf.Max(minStartDistance, desiredDistance - distancePlayerToWall);
        Vector3 desiredOrbit = facingDir * horizontalBack;
        float verticalOffset = Mathf.Max(minVerticalOffset, startVerticalOffset);
        float clampBase = Mathf.Max(horizontalBack, 0.0001f);
        desiredOrbit.y = Mathf.Clamp(verticalOffset, -clampBase * 0.75f, clampBase * 0.75f);

        ApplyOrbit(desiredOrbit);
    }

    private void ApplyOrbit(Vector3 desiredOrbit)
    {
        float orbitMagnitude = desiredOrbit.magnitude;
        if (orbitMagnitude < 0.001f)
        {
            return;
        }

        playerDistance = orbitMagnitude;
        Vector3 dir = desiredOrbit / orbitMagnitude;
        pitch = Mathf.Asin(Mathf.Clamp(dir.y, -0.999f, 0.999f)) * Mathf.Rad2Deg;
        float headingRadians = Mathf.Atan2(dir.x, dir.z);
        heading = (headingRadians + Mathf.PI) * Mathf.Rad2Deg;
    }

    private Bounds CalculateBounds(Transform reference)
    {
        Bounds bounds = new Bounds(reference.position, Vector3.zero);
        bool hasBounds = false;

        Renderer[] renderers = reference.GetComponentsInChildren<Renderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            if (!hasBounds)
            {
                bounds = renderers[i].bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
        }

        if (!hasBounds)
        {
            Collider[] colliders = reference.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!hasBounds)
                {
                    bounds = colliders[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(colliders[i].bounds);
                }
            }
        }

        if (!hasBounds)
        {
            bounds = new Bounds(reference.position, Vector3.one);
        }

        bounds.Expand(wallFramingPadding * 2f);
        return bounds;
    }

    private float RequiredDistanceForHeight(float height)
    {
        float halfHeight = (height * 0.5f) + wallFramingPadding;
        float fov = cachedCamera != null ? cachedCamera.fieldOfView : 60f;
        float halfFovRad = Mathf.Clamp(fov, 1f, 179f) * Mathf.Deg2Rad * 0.5f;
        float tanValue = Mathf.Tan(halfFovRad);
        if (tanValue <= 0.0001f)
        {
            tanValue = 0.0001f;
        }
        return halfHeight / tanValue;
    }
}
