using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class RouteMove
{
    public int step;
    public string limb;
    public int hold_idx;
    public string hold_id;
    public float nx;
    public float ny;
}

[Serializable]
public class RouteFile
{
    public string algorithm;
    public int total_moves;
    public int total_holds;
    public float final_height;
    public RouteMove[] moves;
}

/// <summary>
/// Reads a climbing route JSON file and repositions humanoid limbs (via Animator IK) to match each hold.
/// </summary>
public class RouteFollower : MonoBehaviour
{
    [Header("Route Data")]
    public TextAsset routeJson;
    public bool playOnStart = true;

    [Header("Wall Mapping")]
    public Transform wallOrigin;
    public Vector2 wallSize = new Vector2(4f, 4f);

    [Header("Animation")]
    public Animator animator;
    public float moveDuration = 1.2f;
    public float settleDelay = 0.15f;

    [Header("Body Follow")]
    public Transform bodyRoot;
    public CharacterController bodyController;
    public PlayerController playerController;
    public Vector3 bodyOffset = new Vector3(0f, -0.9f, -0.35f);
    public float bodyFollowSpeed = 6f;
    public float bodyRotateSpeed = 7f;
    public bool alignBodyToWall = true;
    public bool disablePlayerController = true;

    static readonly AvatarIKGoal[] trackedGoals =
    {
        AvatarIKGoal.LeftHand,
        AvatarIKGoal.RightHand,
        AvatarIKGoal.LeftFoot,
        AvatarIKGoal.RightFoot
    };

    readonly Dictionary<AvatarIKGoal, HumanBodyBones> boneMap = new()
    {
        { AvatarIKGoal.LeftHand, HumanBodyBones.LeftHand },
        { AvatarIKGoal.RightHand, HumanBodyBones.RightHand },
        { AvatarIKGoal.LeftFoot, HumanBodyBones.LeftFoot },
        { AvatarIKGoal.RightFoot, HumanBodyBones.RightFoot }
    };

    readonly Dictionary<string, AvatarIKGoal> limbMap = new()
    {
        { "LF", AvatarIKGoal.LeftFoot },
        { "RF", AvatarIKGoal.RightFoot },
        { "LH", AvatarIKGoal.LeftHand },
        { "RH", AvatarIKGoal.RightHand }
    };

    readonly Dictionary<AvatarIKGoal, Vector3> ikTargets = new();
    RouteFile route;
    Coroutine playback;
    bool ikDirty;
    bool controllerSuppressed;
    bool previousStationary;
    bool previousControllerEnabled;

    void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }
        if (bodyRoot == null && animator != null)
        {
            bodyRoot = animator.transform;
        }
        if (playerController == null)
        {
            playerController = GetComponent<PlayerController>();
        }
        if (bodyController == null && bodyRoot != null)
        {
            bodyController = bodyRoot.GetComponent<CharacterController>();
        }
    }

    void Start()
    {
        if (playOnStart)
        {
            PlayRoute();
        }
    }

    public void PlayRoute()
    {
        if (routeJson == null || animator == null || wallOrigin == null)
        {
            Debug.LogError("RouteFollower: Missing references (routeJson / animator / wallOrigin).");
            return;
        }

        route = JsonUtility.FromJson<RouteFile>(routeJson.text);
        if (route?.moves == null || route.moves.Length == 0)
        {
            Debug.LogWarning("RouteFollower: Route JSON has no moves.");
            return;
        }

        StopRoute();

        CacheInitialTargets();
        UpdateBodyPose(true);
        DisableManualController();

        if (playback != null)
        {
            StopCoroutine(playback);
        }
        playback = StartCoroutine(PlayRoutine());
    }

    public void StopRoute()
    {
        if (playback != null)
        {
            StopCoroutine(playback);
            playback = null;
        }
        RestoreManualController();
    }

    IEnumerator PlayRoutine()
    {
        foreach (var move in route.moves)
        {
            if (!limbMap.TryGetValue(move.limb, out var goal))
            {
                continue;
            }

            Vector3 start = ikTargets.TryGetValue(goal, out var current)
                ? current
                : SampleGoalPosition(goal);
            Vector3 target = WallToWorld(move.nx, move.ny);
            float elapsed = 0f;

            while (elapsed < moveDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / moveDuration);
                ikTargets[goal] = Vector3.Lerp(start, target, t);
                ikDirty = true;
                UpdateBodyPose();
                yield return null;
            }

            float settleTimer = 0f;
            while (settleTimer < settleDelay)
            {
                settleTimer += Time.deltaTime;
                UpdateBodyPose();
                yield return null;
            }
        }
        playback = null;
        RestoreManualController();
    }

    Vector3 WallToWorld(float nx, float ny)
    {
        float x = (nx - 0.5f) * wallSize.x;
        float y = ny * wallSize.y;
        Vector3 right = wallOrigin.right * x;
        Vector3 up = wallOrigin.up * y;
        return wallOrigin.position + right + up;
    }

    void CacheInitialTargets()
    {
        ikTargets.Clear();
        foreach (var goal in trackedGoals)
        {
            ikTargets[goal] = SampleGoalPosition(goal);
        }
    }

    void UpdateBodyPose(bool instant = false)
    {
        if (bodyRoot == null || wallOrigin == null || ikTargets.Count == 0)
        {
            return;
        }

        Vector3 current = bodyRoot.position;
        Vector3 target = ComputeBodyTarget();
        float lerp = instant ? 1f : Mathf.Clamp01(Time.deltaTime * bodyFollowSpeed);
        Vector3 next = Vector3.Lerp(current, target, lerp);
        Vector3 delta = next - current;

        if (bodyController != null)
        {
            bodyController.Move(delta);
        }
        else
        {
            bodyRoot.position = next;
        }

        if (alignBodyToWall)
        {
            AlignBodyRotation(instant);
        }
    }

    Vector3 ComputeBodyTarget()
    {
        Vector3 sum = Vector3.zero;
        int count = 0;
        foreach (var goal in trackedGoals)
        {
            if (ikTargets.TryGetValue(goal, out var pos))
            {
                sum += pos;
                count++;
            }
        }
        if (count == 0)
        {
            return bodyRoot.position;
        }
        return (sum / count) + WallSpaceOffset(bodyOffset);
    }

    Vector3 WallSpaceOffset(Vector3 offset)
    {
        if (wallOrigin == null)
        {
            return offset;
        }
        return (wallOrigin.right * offset.x) + (wallOrigin.up * offset.y) + (wallOrigin.forward * offset.z);
    }

    void AlignBodyRotation(bool instant)
    {
        if (bodyRoot == null || wallOrigin == null)
        {
            return;
        }

        Vector3 forward = wallOrigin.forward;
        forward.y = 0f;

        if (forward.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Quaternion target = Quaternion.LookRotation(forward.normalized, Vector3.up);
        float lerp = instant ? 1f : Mathf.Clamp01(Time.deltaTime * bodyRotateSpeed);
        bodyRoot.rotation = Quaternion.Slerp(bodyRoot.rotation, target, lerp);
    }

    void DisableManualController()
    {
        if (!disablePlayerController || playerController == null || controllerSuppressed)
        {
            return;
        }

        previousStationary = playerController.stationary;
        previousControllerEnabled = playerController.enabled;
        playerController.stationary = true;
        playerController.enabled = false;
        controllerSuppressed = true;
    }

    void RestoreManualController()
    {
        if (!controllerSuppressed || playerController == null)
        {
            return;
        }

        playerController.stationary = previousStationary;
        playerController.enabled = previousControllerEnabled;
        controllerSuppressed = false;
    }

    Vector3 SampleGoalPosition(AvatarIKGoal goal)
    {
        if (animator == null)
        {
            return bodyRoot != null ? bodyRoot.position : transform.position;
        }

        Vector3 pos = animator.GetIKPosition(goal);
        bool invalid = float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) || pos == Vector3.zero;
        if (!invalid)
        {
            return pos;
        }

        if (animator.isHuman && boneMap.TryGetValue(goal, out var bone))
        {
            Transform boneTransform = animator.GetBoneTransform(bone);
            if (boneTransform != null)
            {
                return boneTransform.position;
            }
        }

        return bodyRoot != null ? bodyRoot.position : transform.position;
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (!ikDirty || animator == null)
        {
            return;
        }

        Quaternion ikRotation = wallOrigin != null
            ? Quaternion.LookRotation(wallOrigin.forward, wallOrigin.up)
            : animator.transform.rotation;

        foreach (var kv in ikTargets)
        {
            animator.SetIKPositionWeight(kv.Key, 1f);
            animator.SetIKRotationWeight(kv.Key, 1f);
            animator.SetIKPosition(kv.Key, kv.Value);
            animator.SetIKRotation(kv.Key, ikRotation);
        }

        ikDirty = false;
    }

    void OnDisable()
    {
        StopRoute();
    }
}
