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

    [Header("IK Hints")]
    [Range(0f, 1f)] public float handHintWeight = 0.85f;
    [Range(0f, 1f)] public float footHintWeight = 0.7f;
    public float elbowLateralOffset = 0.25f;
    public float elbowForwardOffset = 0.1f;
    public float kneeLateralOffset = 0.18f;
    public float kneeForwardOffset = 0.05f;

    [Header("Body Follow")]
    public Transform bodyRoot;
    public CharacterController bodyController;
    public PlayerController playerController;
    public Vector3 bodyOffset = new Vector3(0f, -0.9f, -0.35f);
    public float bodyFollowSpeed = 6f;
    public float bodyRotateSpeed = 7f;
    public bool alignBodyToWall = true;
    public bool disablePlayerController = true;

    [Header("Debug")]
    public bool logMoves;

    [Header("Hold Placement")]
    public float handDepth = 0.22f;
    public float footDepth = 0.12f;

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

    readonly Dictionary<AvatarIKGoal, Vector3> ikTargets = new();
    readonly Dictionary<AvatarIKHint, Vector3> ikHintTargets = new();
    readonly Dictionary<AvatarIKGoal, AvatarIKHint> hintMap = new()
    {
        { AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow },
        { AvatarIKGoal.RightHand, AvatarIKHint.RightElbow },
        { AvatarIKGoal.LeftFoot, AvatarIKHint.LeftKnee },
        { AvatarIKGoal.RightFoot, AvatarIKHint.RightKnee }
    };

    readonly Dictionary<AvatarIKGoal, HumanBodyBones> limbRootBones = new()
    {
        { AvatarIKGoal.LeftHand, HumanBodyBones.LeftUpperArm },
        { AvatarIKGoal.RightHand, HumanBodyBones.RightUpperArm },
        { AvatarIKGoal.LeftFoot, HumanBodyBones.LeftUpperLeg },
        { AvatarIKGoal.RightFoot, HumanBodyBones.RightUpperLeg }
    };
    RouteFile route;
    Coroutine playback;
    bool ikDirty;
    bool controllerSuppressed;
    bool previousStationary;
    bool previousControllerEnabled;
    bool ikActive;

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
        ikActive = true;

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
        ikActive = false;
        ikTargets.Clear();
        RestoreManualController();
    }

    IEnumerator PlayRoutine()
    {
        foreach (var move in route.moves)
        {
            if (!TryResolveGoal(move.limb, out var goal))
            {
                Debug.LogWarning($"RouteFollower: Unknown limb '{move.limb}' at step {move.step}.");
                continue;
            }

            Vector3 start = ikTargets.TryGetValue(goal, out var current)
                ? current
                : SampleGoalPosition(goal);
            Vector3 target = ApplyGoalOffsets(goal, WallToWorld(move.nx, move.ny));
            if (logMoves)
            {
                Debug.Log($"RouteFollower: Step {move.step} - {move.limb} -> {goal} target {target}");
            }
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
        ikActive = false;
        ikTargets.Clear();
    }

    bool TryResolveGoal(string limbCode, out AvatarIKGoal goal)
    {
        goal = AvatarIKGoal.LeftHand;
        if (string.IsNullOrWhiteSpace(limbCode))
        {
            return false;
        }

        string normalized = limbCode.Trim().ToUpperInvariant();
        switch (normalized)
        {
            case "LF":
            case "LEFTFOOT":
            case "LEFT_FOOT":
            case "LFOOT":
                goal = AvatarIKGoal.LeftFoot;
                return true;
            case "RF":
            case "RIGHTFOOT":
            case "RIGHT_FOOT":
            case "RFOOT":
                goal = AvatarIKGoal.RightFoot;
                return true;
            case "LH":
            case "LEFTHAND":
            case "LEFT_HAND":
            case "LHAND":
                goal = AvatarIKGoal.LeftHand;
                return true;
            case "RH":
            case "RIGHTHAND":
            case "RIGHT_HAND":
            case "RHAND":
                goal = AvatarIKGoal.RightHand;
                return true;
            default:
                return false;
        }
    }

    Vector3 WallToWorld(float nx, float ny)
    {
        float x = (nx - 0.5f) * wallSize.x;
        float y = ny * wallSize.y;
        Vector3 right = wallOrigin.right * x;
        Vector3 up = wallOrigin.up * y;
        return wallOrigin.position + right + up;
    }

    Vector3 ApplyGoalOffsets(AvatarIKGoal goal, Vector3 basePoint)
    {
        if (wallOrigin == null)
        {
            return basePoint;
        }

        Vector3 forward = wallOrigin.forward.sqrMagnitude > 0.0001f
            ? wallOrigin.forward.normalized
            : Vector3.forward;
        float depth = (goal == AvatarIKGoal.LeftHand || goal == AvatarIKGoal.RightHand)
            ? handDepth
            : footDepth;
        return basePoint + (forward * depth);
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
        if (animator == null || !ikActive)
        {
            return;
        }

        if (ikTargets.Count == 0)
        {
            CacheInitialTargets();
        }

        Quaternion ikRotation = wallOrigin != null
            ? Quaternion.LookRotation(wallOrigin.forward, wallOrigin.up)
            : animator.transform.rotation;

        foreach (var goal in trackedGoals)
        {
            if (ikTargets.TryGetValue(goal, out var pos))
            {
                animator.SetIKPositionWeight(goal, 1f);
                animator.SetIKRotationWeight(goal, 1f);
                animator.SetIKPosition(goal, pos);
                animator.SetIKRotation(goal, ikRotation);
            }
            else
            {
                animator.SetIKPositionWeight(goal, 0f);
                animator.SetIKRotationWeight(goal, 0f);
            }
        }

        UpdateIkHints();
        ApplyIkHints();

        ikDirty = false;
    }

    void UpdateIkHints()
    {
        if (wallOrigin == null || !animator.isHuman)
        {
            ikHintTargets.Clear();
            return;
        }

        Vector3 wallRight = wallOrigin.right.sqrMagnitude > 0.0001f ? wallOrigin.right.normalized : Vector3.right;
        Vector3 wallForward = wallOrigin.forward.sqrMagnitude > 0.0001f ? wallOrigin.forward.normalized : Vector3.forward;

        ikHintTargets.Clear();

        foreach (var goal in trackedGoals)
        {
            if (!ikTargets.TryGetValue(goal, out var effectorPos) || !hintMap.TryGetValue(goal, out var hint) ||
                !limbRootBones.TryGetValue(goal, out var rootBone))
            {
                continue;
            }

            Vector3 root = SampleBonePosition(rootBone);
            bool isHand = goal == AvatarIKGoal.LeftHand || goal == AvatarIKGoal.RightHand;
            bool isLeft = goal == AvatarIKGoal.LeftHand || goal == AvatarIKGoal.LeftFoot;
            float lateralOffset = isHand ? elbowLateralOffset : kneeLateralOffset;
            float forwardOffset = isHand ? elbowForwardOffset : kneeForwardOffset;

            Vector3 lateralDir = wallRight * (isLeft ? -1f : 1f);
            Vector3 midPoint = Vector3.Lerp(root, effectorPos, isHand ? 0.65f : 0.55f);
            Vector3 hintPos = midPoint + (lateralDir * lateralOffset) + (wallForward * forwardOffset);
            ikHintTargets[hint] = hintPos;
        }
    }

    void ApplyIkHints()
    {
        foreach (var kv in ikHintTargets)
        {
            bool isElbow = kv.Key == AvatarIKHint.LeftElbow || kv.Key == AvatarIKHint.RightElbow;
            float weight = isElbow ? handHintWeight : footHintWeight;
            animator.SetIKHintPositionWeight(kv.Key, weight);
            animator.SetIKHintPosition(kv.Key, kv.Value);
        }
    }

    Vector3 SampleBonePosition(HumanBodyBones bone)
    {
        if (animator != null && animator.isHuman)
        {
            Transform boneTransform = animator.GetBoneTransform(bone);
            if (boneTransform != null)
            {
                return boneTransform.position;
            }
        }
        return bodyRoot != null ? bodyRoot.position : transform.position;
    }

    void OnDisable()
    {
        StopRoute();
    }
}
