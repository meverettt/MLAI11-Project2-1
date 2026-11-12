using System.Collections;
using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

public class DungeonEscapeEnvController : MonoBehaviour
{
    [System.Serializable]
    public class PlayerInfo
    {
        public PushAgentEscape Agent;
        [HideInInspector]
        public Vector3 StartingPos;
        [HideInInspector]
        public Quaternion StartingRot;
        [HideInInspector]
        public Rigidbody Rb;
        [HideInInspector]
        public Collider Col;
    }

    [System.Serializable]
    public class DragonInfo
    {
        public SimpleNPC Agent;
        [HideInInspector]
        public Vector3 StartingPos;
        [HideInInspector]
        public Quaternion StartingRot;
        [HideInInspector]
        public Rigidbody Rb;
        [HideInInspector]
        public Collider Col;
        public Transform T;
        public bool IsDead;
    }

    /// <summary>
    /// Max Academy steps before this platform resets
    /// </summary>
    /// <returns>The maximum steps before this platform resets.</returns>
    [Header("Max Environment Steps")] public int MaxEnvironmentSteps = 25000;
    private int m_ResetTimer;

    /// <summary>
    /// The area bounds.
    /// </summary>
    [HideInInspector]
    public Bounds areaBounds;
    /// <summary>
    /// The ground. The bounds are used to spawn the elements.
    /// </summary>
    public GameObject ground;

    Material m_GroundMaterial; //cached on Awake()

    /// <summary>
    /// We will be changing the ground material based on success/failue
    /// </summary>
    Renderer m_GroundRenderer;

    public List<PlayerInfo> AgentsList = new List<PlayerInfo>();
    public List<DragonInfo> DragonsList = new List<DragonInfo>();
    private Dictionary<PushAgentEscape, PlayerInfo> m_PlayerDict = new Dictionary<PushAgentEscape, PlayerInfo>();
    public bool UseRandomAgentRotation = true;
    public bool UseRandomAgentPosition = true;
    PushBlockSettings m_PushBlockSettings;

    private int m_NumberOfRemainingPlayers;
    public GameObject Key;
    public GameObject Tombstone;
    private SimpleMultiAgentGroup m_AgentGroup;

    // Our added reward settings (default values used for the settings if the YAML file doesn't specify).
    private float m_GoalReward = 1f; // points received for escaping
    private float m_StepPenalty = 0f; // points that are addded for every step (0 or a negative value)
    private float m_CheckpointBonus = 0f; // extra points received for grabbing the key 
    private bool m_UseShapedReward = false; // false = sparsed, and true = shaped
    private float m_ShapingScale = 0.01f; // used with shaped rewards only to get small extra points when an agent gets closer to next goal
    // below is for shaped rewards only
    private Transform m_Door; // this is the locked door found at the start
    private Transform m_CurrentGoal; // the current goal the team is working towards
    private float m_BestDistance; // the closest distance the team has ever been to the current goal

    
    void Start()
    {
        // finding the locked door in the dungeon (needed for shaped rewards)
        foreach(var t in GetComponentsInChildren<Transform>(true))
        {
            if(t.CompareTag("lock"))
            {
                m_Door = t;
                break;
            }
        }
        if(m_Door == null)
        {
            Debug.LogWarning("shaped rewards: no door (which has the tag 'lock') found so the door section skipped");
        }

        // Get the ground's bounds
        areaBounds = ground.GetComponent<Collider>().bounds;
        // Get the ground renderer so we can change the material when a goal is scored
        m_GroundRenderer = ground.GetComponent<Renderer>();
        // Starting material
        m_GroundMaterial = m_GroundRenderer.material;
        m_PushBlockSettings = FindFirstObjectByType<PushBlockSettings>();

        //Reset Players Remaining
        m_NumberOfRemainingPlayers = AgentsList.Count;

        //Hide The Key
        Key.SetActive(false);

        // Initialize TeamManager
        m_AgentGroup = new SimpleMultiAgentGroup();
        foreach (var item in AgentsList)
        {
            item.StartingPos = item.Agent.transform.position;
            item.StartingRot = item.Agent.transform.rotation;
            item.Rb = item.Agent.GetComponent<Rigidbody>();
            item.Col = item.Agent.GetComponent<Collider>();
            // Add to team manager
            m_AgentGroup.RegisterAgent(item.Agent);
        }
        foreach (var item in DragonsList)
        {
            item.StartingPos = item.Agent.transform.position;
            item.StartingRot = item.Agent.transform.rotation;
            item.T = item.Agent.transform;
            item.Col = item.Agent.GetComponent<Collider>();
        }

        ResetScene();
    }

    // Update is called once per frame
    void FixedUpdate()
    {
        m_ResetTimer += 1;
        // If step_penalty = 0 then do nothing. Otherwise add it to the team's points (hence lose a little every step)
        if (m_StepPenalty != 0f)
        {
            m_AgentGroup.AddGroupReward(m_StepPenalty);
        }
        if (m_UseShapedReward)
        {
            GiveShapedReward();
        }
        if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
        {
            RecordAttempt(false); // the timer ran out, so could not escape
            m_AgentGroup.GroupEpisodeInterrupted();
            ResetScene();
        }
    }

    public void TouchedHazard(PushAgentEscape agent)
    {
        m_NumberOfRemainingPlayers--;
        if (m_NumberOfRemainingPlayers == 0 || agent.IHaveAKey)
        {
            RecordAttempt(false); // everyone in the game died or the agent that had the key died
            m_AgentGroup.EndGroupEpisode();
            ResetScene();
        }
        else
        {
            agent.gameObject.SetActive(false);
        }
    }

    public void UnlockDoor()
    {
        RecordAttempt(true); // means that the agents in the game have escaped
        m_AgentGroup.AddGroupReward(m_GoalReward); //instead of hardcoding goal reward to 1, we read escape reward from the YAML file
        StartCoroutine(GoalScoredSwapGroundMaterial(m_PushBlockSettings.goalScoredMaterial, 0.5f));

        print("Unlocked Door");
        m_AgentGroup.EndGroupEpisode();

        ResetScene();
    }

    // Method is called by an agent when it grabs the key and gives the team the checkpoint bonus (if checkpoint bonus = 0, then it does nothing)
    public void KeyPickedUp()
    {
        if(m_CheckpointBonus != 0f)
        {
            m_AgentGroup.AddGroupReward(m_CheckpointBonus);
        }
    }

    // gives a small reward whenever the team reaches closer to its next goal than reached before
    void GiveShapedReward()
    {
        // function to determine which goal is the team working towards (dragon, key, or door)
        Transform goal;
        if(Key.activeSelf) goal = Key.transform;
        else if (SomeoneHasKey()) goal = m_Door;
        else goal = DragonsList[0].Agent.transform;

        if(goal == null) return;

        // determining how close the nearest agent is to that goal
        float distance = float.MaxValue;
        foreach(var item in AgentsList)
        {
            if (item.Agent.gameObject.activeSelf)
            {
                distance = Mathf.Min(distance, Vector3.Distance(item.Agent.transform.position, goal.position));
            }
        }

        // if it is a new goal, then we start measuring from this point
        if(goal != m_CurrentGoal)
        {
            m_CurrentGoal = goal;
            m_BestDistance = distance; 
            return;
        }

        // if we are closer to the goal than ever reached before, the team gets a small reward for its progress
        if(distance < m_BestDistance)
        {
            m_AgentGroup.AddGroupReward(m_ShapingScale * (m_BestDistance - distance));
            m_BestDistance = distance;
        }
    }

    // helper function set to true if any agent currently alive in the game is carrying the key 
    bool SomeoneHasKey()
    {
        foreach(var item in AgentsList)
        {
            if(item.Agent.gameObject.activeSelf && item.Agent.IHaveAKey) return true;
        }
        return false;
    }

    void RecordAttempt(bool escaped)
    {
        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Custom/Success", escaped ? 1f : 0f); // if agents escaped, write 1 or otherwise write 0
        if(escaped)
        {
            stats.Add("Custom/StepsToEscape", m_ResetTimer); // m_ResetTimer already calculates the steps taken in current attempt (it uses it for the time limit)
        }
    }

    public void KilledByBaddie(PushAgentEscape agent, Collision baddieCol)
    {
        baddieCol.gameObject.SetActive(false);
        m_NumberOfRemainingPlayers--;
        agent.gameObject.SetActive(false);
        print($"{baddieCol.gameObject.name} ate {agent.transform.name}");

        //Spawn Tombstone
        Tombstone.transform.SetPositionAndRotation(agent.transform.position, agent.transform.rotation);
        Tombstone.SetActive(true);

        //Spawn the Key Pickup
        Key.transform.SetPositionAndRotation(baddieCol.collider.transform.position, baddieCol.collider.transform.rotation);
        Key.SetActive(true);
    }

    /// <summary>
    /// Use the ground's bounds to pick a random spawn position.
    /// </summary>
    public Vector3 GetRandomSpawnPos()
    {
        var foundNewSpawnLocation = false;
        var randomSpawnPos = Vector3.zero;
        while (foundNewSpawnLocation == false)
        {
            var randomPosX = Random.Range(-areaBounds.extents.x * m_PushBlockSettings.spawnAreaMarginMultiplier,
                areaBounds.extents.x * m_PushBlockSettings.spawnAreaMarginMultiplier);

            var randomPosZ = Random.Range(-areaBounds.extents.z * m_PushBlockSettings.spawnAreaMarginMultiplier,
                areaBounds.extents.z * m_PushBlockSettings.spawnAreaMarginMultiplier);
            randomSpawnPos = ground.transform.position + new Vector3(randomPosX, 1f, randomPosZ);
            if (Physics.CheckBox(randomSpawnPos, new Vector3(2.5f, 0.01f, 2.5f)) == false)
            {
                foundNewSpawnLocation = true;
            }
        }
        return randomSpawnPos;
    }

    /// <summary>
    /// Swap ground material, wait time seconds, then swap back to the regular material.
    /// </summary>
    IEnumerator GoalScoredSwapGroundMaterial(Material mat, float time)
    {
        m_GroundRenderer.material = mat;
        yield return new WaitForSeconds(time); // Wait for 2 sec
        m_GroundRenderer.material = m_GroundMaterial;
    }

    public void BaddieTouchedBlock()
    {
        RecordAttempt(false); // the dragon escaped
        m_AgentGroup.EndGroupEpisode();

        // Swap ground material for a bit to indicate we scored.
        StartCoroutine(GoalScoredSwapGroundMaterial(m_PushBlockSettings.failMaterial, 0.5f));
        ResetScene();
    }

    Quaternion GetRandomRot()
    {
        return Quaternion.Euler(0, Random.Range(0.0f, 360.0f), 0);
    }

    void ReadRewardSettings()
    {
        var settings = Academy.Instance.EnvironmentParameters;
        m_GoalReward = settings.GetWithDefault("goal_reward", 1f);
        m_StepPenalty = settings.GetWithDefault("step_penalty", 0f);
        m_CheckpointBonus = settings.GetWithDefault("checkpoint_bonus", 0f);
        m_UseShapedReward = settings.GetWithDefault("reward_type", 0f) > 0.5f; // 0 = sparse rewards, and 1 = shaped rewards
        m_ShapingScale = settings.GetWithDefault("shaping_scale", 0.01f);
    }

    void ResetScene()
    {
        //Read our reward settings before beginning each training attempt
        ReadRewardSettings();
        m_CurrentGoal = null; //whenever new training attempt, shaped rewards should also be reset to fresh

        //Reset counter
        m_ResetTimer = 0;

        //Reset Players Remaining
        m_NumberOfRemainingPlayers = AgentsList.Count;

        //Random platform rot
        var rotation = Random.Range(0, 4);
        var rotationAngle = rotation * 90f;
        transform.Rotate(new Vector3(0f, rotationAngle, 0f));

        //Reset Agents
        foreach (var item in AgentsList)
        {
            var pos = UseRandomAgentPosition ? GetRandomSpawnPos() : item.StartingPos;
            var rot = UseRandomAgentRotation ? GetRandomRot() : item.StartingRot;

            item.Agent.transform.SetPositionAndRotation(pos, rot);
            item.Rb.linearVelocity = Vector3.zero;
            item.Rb.angularVelocity = Vector3.zero;
            item.Agent.MyKey.SetActive(false);
            item.Agent.IHaveAKey = false;
            item.Agent.gameObject.SetActive(true);
            m_AgentGroup.RegisterAgent(item.Agent);
        }

        //Reset Key
        Key.SetActive(false);

        //Reset Tombstone
        Tombstone.SetActive(false);

        //End Episode
        foreach (var item in DragonsList)
        {
            if (!item.Agent)
            {
                return;
            }
            item.Agent.transform.SetPositionAndRotation(item.StartingPos, item.StartingRot);
            item.Agent.SetRandomWalkSpeed();
            item.Agent.gameObject.SetActive(true);
        }
    }
}
