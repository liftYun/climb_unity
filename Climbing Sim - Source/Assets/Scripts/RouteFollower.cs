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

[Serializable]
public class HoldRouteFile
{
    public int image_width;
    public int image_height;
    public HoldEntry[] holds;
}

[Serializable]
public class HoldEntry
{
    public int id;
    public HoldCenter center;
    public string type;
    public string hand;
    public string foot;
    public bool is_start;
    public bool is_finish;
}

[Serializable]
public class HoldCenter
{
    public float x;
    public float y;
}

/// <summary>
/// Reads a climbing route JSON file and repositions humanoid limbs (via Animator IK) to match each hold.
/// </summary>
public class RouteFollower : MonoBehaviour
{
    [Header("Route Data")]
    public TextAsset routeJson;
    string runtimeRouteJson;
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
    public event Action RouteCompleted;
    RouteFile route;
    Coroutine playback;
    bool ikDirty;
    bool controllerSuppressed;
    bool previousStationary;
    bool previousControllerEnabled;
    bool ikActive;
    readonly Dictionary<AvatarIKGoal, Vector2> startHoldTargets = new();

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
        if ((routeJson == null && string.IsNullOrEmpty(runtimeRouteJson)) || animator == null || wallOrigin == null)
        {
            Debug.LogError("RouteFollower: Missing references (routeJson / animator / wallOrigin).");
            return;
        }

        startHoldTargets.Clear();
        string jsonText = !string.IsNullOrEmpty(runtimeRouteJson) ? runtimeRouteJson : routeJson.text;
        route = LoadRouteData(jsonText);
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
        startHoldTargets.Clear();
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
        RouteCompleted?.Invoke();
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
        Vector3 originPos = wallOrigin != null ? wallOrigin.position : transform.position;
        Vector3 right = PlayerRight() * x;
        Vector3 up = PlayerUp() * y;
        return originPos + right + up;
    }

    Vector3 ApplyGoalOffsets(AvatarIKGoal goal, Vector3 basePoint)
    {
        Vector3 forward = PlayerForward();
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
            Vector3 initial = SampleGoalPosition(goal);
            if (startHoldTargets.TryGetValue(goal, out var uv))
            {
                initial = ApplyGoalOffsets(goal, WallToWorld(uv.x, uv.y));
            }
            ikTargets[goal] = initial;
        }
    }

    RouteFile LoadRouteData(string json)
    {
        if (TryParseHoldRoute(json, out var holdRoute))
        {
            return ConvertHoldRoute(holdRoute);
        }
        return JsonUtility.FromJson<RouteFile>(json);
    }

    bool TryParseHoldRoute(string json, out HoldRouteFile holdRoute)
    {
        holdRoute = JsonUtility.FromJson<HoldRouteFile>(json);
        if (holdRoute == null || holdRoute.holds == null || holdRoute.holds.Length == 0)
        {
            holdRoute = null;
            return false;
        }
        return holdRoute.image_width > 0 && holdRoute.image_height > 0;
    }

    RouteFile ConvertHoldRoute(HoldRouteFile holdRoute)
    {
        List<RouteMove> moves = new();
        if (holdRoute?.holds == null)
        {
            return new RouteFile { moves = Array.Empty<RouteMove>() };
        }

        foreach (var hold in holdRoute.holds)
        {
            if (!TryResolveGoalFromHold(hold, out var limbCode, out var goal))
            {
                continue;
            }

            Vector2 normalized = NormalizeHoldPosition(holdRoute, hold.center);
            if (hold.is_start)
            {
                startHoldTargets[goal] = normalized;
                continue;
            }

            moves.Add(new RouteMove
            {
                step = moves.Count + 1,
                limb = limbCode,
                hold_idx = hold.id,
                hold_id = hold.id.ToString(),
                nx = normalized.x,
                ny = normalized.y
            });
        }

        return new RouteFile
        {
            algorithm = "generated_from_holds",
            total_holds = holdRoute.holds.Length,
            total_moves = moves.Count,
            final_height = moves.Count > 0 ? moves[moves.Count - 1].ny : 0f,
            moves = moves.ToArray()
        };
    }

    bool TryResolveGoalFromHold(HoldEntry hold, out string limbCode, out AvatarIKGoal goal)
    {
        limbCode = null;
        goal = AvatarIKGoal.LeftHand;
        if (hold == null)
        {
            return false;
        }

        string candidate = string.Empty;
        if (!string.IsNullOrEmpty(hold.type))
        {
            if (string.Equals(hold.type, "hand", StringComparison.OrdinalIgnoreCase))
            {
                candidate = hold.hand;
            }
            else if (string.Equals(hold.type, "foot", StringComparison.OrdinalIgnoreCase))
            {
                candidate = hold.foot;
            }
        }

        if (string.IsNullOrEmpty(candidate))
        {
            candidate = !string.IsNullOrEmpty(hold.hand) ? hold.hand : hold.foot;
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        limbCode = candidate;
        return TryResolveGoal(candidate, out goal);
    }

    Vector2 NormalizeHoldPosition(HoldRouteFile holdRoute, HoldCenter center)
    {
        float width = holdRoute != null && holdRoute.image_width > 0 ? holdRoute.image_width : 1f;
        float height = holdRoute != null && holdRoute.image_height > 0 ? holdRoute.image_height : 1f;
        float cx = center != null ? center.x : width * 0.5f;
        float cy = center != null ? center.y : height * 0.5f;
        float nx = Mathf.Clamp01(cx / width);
        float ny = Mathf.Clamp01(1f - (cy / height));
        return new Vector2(nx, ny);
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
        Vector3 up = PlayerUp();
        return (PlayerRight() * offset.x) + (up * offset.y) + (PlayerForward() * offset.z);
    }

    void AlignBodyRotation(bool instant)
    {
        if (bodyRoot == null)
        {
            return;
        }

        Vector3 forward = PlayerForward();
        forward.y = 0f;

        if (forward.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Quaternion target = Quaternion.LookRotation(forward.normalized, PlayerUp());
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

        Quaternion ikRotation = Quaternion.LookRotation(PlayerForward(), PlayerUp());

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

        Vector3 wallRight = PlayerRight();
        Vector3 wallForward = PlayerForward();

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

    Vector3 PlayerForward()
    {
        Vector3 forward = wallOrigin != null ? -wallOrigin.forward : Vector3.forward;
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }
        return forward.normalized;
    }

    Vector3 PlayerUp()
    {
        Vector3 up = wallOrigin != null ? wallOrigin.up : Vector3.up;
        if (up.sqrMagnitude < 0.0001f)
        {
            up = Vector3.up;
        }
        return up.normalized;
    }

    Vector3 PlayerRight()
    {
        Vector3 forward = PlayerForward();
        Vector3 right = Vector3.Cross(PlayerUp(), forward);
        if (right.sqrMagnitude < 0.0001f)
        {
            right = Vector3.Cross(Vector3.up, forward);
        }
        return right.normalized;
    }

    void OnDisable()
    {
        StopRoute();
    }

    public void LoadRouteFromJson(string json)
    {
        runtimeRouteJson = json;
    }
}
