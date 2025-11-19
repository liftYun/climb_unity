using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    public double heading = 0;
    public Transform cam; //the primary camera that follows the player character 
    public Vector3 velocity;
    public float speed;
    public float acceleration;
    public float turnSpeedLow;
    public float turnSpeedHigh;
    public float rollSpeed;
    public Animator anim;
    bool doubleJump;
    public float jumpForce;
    public float gravityModifier;
    public float crouchSpeedModifier = 0.8f;
    bool rolling;
    bool climbing;
    bool crouching;
    public Vector2 climbHopDistance;
    public Vector2 climbSpeed;
    float ledgeHeight;
    Collider ledge;
    Collider nextLedge;
    int destination;
    public float ledgeOffset = 1;
    bool grabbingLedge;
    float lastInput;
    Vector3 jumpingPoint;

    public Quaternion targetDirection;

    public bool stationary; //If the player is allowed to move


    CharacterController controller;

    // Start is called before the first frame update
    void Start()
    {
        controller = GetComponent<CharacterController>();
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetButtonDown("Fire3") && controller.isGrounded) 
        {
            rolling = true;
            float yStore = velocity.y;
            if (Input.GetAxis("Horizontal") != 0 || Input.GetAxis("Vertical") != 0)
            {
                velocity = (cam.forward * Input.GetAxis("Vertical")) + (cam.right * Input.GetAxis("Horizontal"));
                velocity = velocity.normalized * speed;
                controller.center = new Vector3(0,0.46f,0);
                controller.height = 0.915f;
            }
            else
            {
                velocity = cam.forward * rollSpeed;
            }
            if (velocity.sqrMagnitude > 0.0001f)
            {
                targetDirection = Quaternion.LookRotation(velocity);
            }
            velocity.y = yStore;
        }//Begin Rolling
        if (!stationary && !rolling && !climbing)  //Regular Walking
        {
            StandardMovement();
        } 
        else if (rolling) //Executing Roll Movement 
        {
            Roll();
        }else if (climbing)
        {
            Climb();
        }
    }

    void StandardMovement()
    {

        if (Input.GetButtonDown("Fire2"))
        {
            if (crouching)
            {
                crouching = false;
                anim.SetBool("IsCrouching", false);
                speed /= crouchSpeedModifier;
            }
            else
            {
                crouching = true;
                anim.SetBool("IsCrouching", true);
                speed *= crouchSpeedModifier;
            }
        }

        float yStore = velocity.y;
        velocity = (cam.forward * Input.GetAxis("Vertical")) + (cam.right * Input.GetAxis("Horizontal"));
        velocity = velocity.normalized * speed;
        if (velocity.sqrMagnitude > 0.0001f)
        {
            targetDirection = Quaternion.LookRotation(velocity);
        }

        velocity.y = yStore;

        if (controller.isGrounded)
        {
            //anim.SetBool("DoubleJump", false);
            doubleJump = false;
            velocity.y = -1; //when grounded, gravity will not accumulate.
            if (Input.GetButtonDown("Jump"))
            {
                velocity.y = jumpForce;
                if (crouching)
                {
                    crouching = false;
                    anim.SetBool("IsCrouching", false);
                    speed /= crouchSpeedModifier;
                }
            }
        }
        else if (!doubleJump && Input.GetButtonDown("Jump"))
        {
            doubleJump = true;
            velocity.y = jumpForce * 1.1f;
            //anim.SetBool("DoubleJump", true);
        }


        velocity.y += Physics.gravity.y * Time.deltaTime * gravityModifier;

        controller.Move(velocity * Time.deltaTime);
        if (Input.GetAxis("Horizontal") != 0 || Input.GetAxis("Vertical") != 0)
        {
            float t = velocity.magnitude / 4;
            float turnSpeed = Mathf.Lerp(turnSpeedHigh, turnSpeedLow, t);
            transform.rotation = Quaternion.Lerp(transform.rotation, targetDirection, turnSpeed * Time.deltaTime);

        }

        transform.eulerAngles.Set(0, transform.eulerAngles.y, transform.eulerAngles.z);
        anim.SetBool("IsGrounded", controller.isGrounded);
        anim.SetFloat("Speed", (Mathf.Abs(Input.GetAxis("Vertical")) + Mathf.Abs(Input.GetAxis("Horizontal"))));
        anim.SetBool("IsJumping", velocity.y > 1);
        anim.SetBool("IsDoubleJumping", doubleJump);

    }

    void Roll()
    {
        anim.SetBool("IsRolling", true);
        

        velocity.y += Physics.gravity.y * Time.deltaTime * gravityModifier;

        controller.Move(velocity * Time.deltaTime);
        transform.rotation = targetDirection;
    }

    void EndRoll()
    {
        anim.SetBool("IsRolling", false);
        rolling = false;
        controller.center = new Vector3(0,0.92f,0);
        controller.height = 1.83f;
    }

    void Climb()
    {

        if (grabbingLedge && Input.GetButton("Fire2")) //change to whatever crouch is asap
        {
            if (Input.GetAxis("Vertical") >= 0 && TestLedgeDown())
            {
                if (anim.GetInteger("ClimbState") == 1)
                {
                    anim.SetInteger("ClimbState", -1);
                    destination = 1;
                }
                //grabbingLedge = false;
            }
            else
            {
                if (anim.GetInteger("ClimbState") == 1)
                {
                    destination = 0;
                    anim.SetInteger("ClimbState", -2);
                }
                //grabbingLedge = false;
            }        
            
        }
        if (grabbingLedge && Input.GetButton("Jump")&& Input.GetAxisRaw("Horizontal")==0)
        {
            if (TestLedgeForward())
            {
                print("go up");
                destination = 5;
                anim.SetInteger("ClimbState", 7);
            }
            else if (TestLedgeUp())
            {
                print("jump up");
                destination = 4;
                anim.SetInteger("ClimbState", 6);
            } 
        }else if (grabbingLedge && Input.GetAxisRaw("Horizontal") != 0)
        {
            Vector3 leftEdge = ledge.bounds.center + (ledge.GetComponent<BoxCollider>().size.x * .5f * ledge.transform.localScale.x * ledge.transform.right);
            bool onLeftEdge = Vector3.Distance(transform.position, leftEdge) < 1.985f && Input.GetAxis("Horizontal") < 0;
            Vector3 rightEdge = ledge.bounds.center - (ledge.GetComponent<BoxCollider>().size.x * .5f * ledge.transform.localScale.x * ledge.transform.right);
            bool onRightEdge = Vector3.Distance(transform.position, rightEdge) < 1.985f && Input.GetAxis("Horizontal") > 0;
            if (Input.GetAxisRaw("Horizontal") < 0 && (onLeftEdge || Input.GetButton("Jump"))){

                if (TestLedgeSides(1))
                {
                    destination = 2;
                    jumpingPoint = transform.position;
                    anim.SetInteger("ClimbState", 4);
                }

            }
            if (Input.GetAxisRaw("Horizontal") > 0 && (onRightEdge || Input.GetButton("Jump"))){

                if (TestLedgeSides(-1))
                {
                    destination = 3;
                    jumpingPoint = transform.position;
                    anim.SetInteger("ClimbState", 5);
                }
            }

        }


        if (!grabbingLedge)
        {
            if (destination == 0)
            {
                float storeY = velocity.y;
                velocity = ledge.transform.forward * climbSpeed.x;
                velocity.y = storeY + Physics.gravity.y * Time.deltaTime * gravityModifier;
                if (controller.isGrounded) climbing = false;

            }
            else if (destination == 1)
            {
                float storeY = velocity.y;
                velocity.y = storeY + Physics.gravity.y * Time.deltaTime * gravityModifier;
                float heightDifference = nextLedge.bounds.max.y - transform.position.y;
                if (heightDifference < 1.8 && heightDifference > 1.4)
                {
                    grabbingLedge = true;
                    ledge = nextLedge;
                    anim.SetInteger("ClimbState", 1);
                }
            }
            else if (destination == 2)
            {
                
                Vector3 leftEdge = nextLedge.bounds.center + (nextLedge.GetComponent<BoxCollider>().size.x * .5f * nextLedge.transform.localScale.x * nextLedge.transform.right);
                Vector3 rightEdge = nextLedge.bounds.center - (nextLedge.GetComponent<BoxCollider>().size.x * .5f * nextLedge.transform.localScale.x * nextLedge.transform.right);
                velocity = ledge.transform.right * climbSpeed.x * Vector3.Distance(rightEdge, jumpingPoint);
                velocity.y = 0;
                bool onRightEdge = Vector3.Distance(transform.position, rightEdge) < 1.955f || Vector3.Distance(transform.position + new Vector3(0, 2, 0), leftEdge) > Vector3.Distance(leftEdge, rightEdge);
                if (!onRightEdge )
                {
                    ledge = nextLedge;
                    grabbingLedge = true;
                    anim.SetInteger("ClimbState", 1);
                }
            }
            else if (destination == 3)
            {
                velocity.y = 0;
                Vector3 leftEdge = nextLedge.bounds.center + (nextLedge.GetComponent<BoxCollider>().size.x * .5f * nextLedge.transform.localScale.x * nextLedge.transform.right);
                Vector3 rightEdge = nextLedge.bounds.center - (nextLedge.GetComponent<BoxCollider>().size.x * .5f * nextLedge.transform.localScale.x * nextLedge.transform.right);
                velocity = ledge.transform.right * climbSpeed.x * Vector3.Distance(leftEdge, jumpingPoint);
                velocity.y = 0;
                bool onLeftEdge = Vector3.Distance(transform.position, leftEdge) < 1.955f || Vector3.Distance(transform.position + new Vector3(0, 2, 0), rightEdge) > Vector3.Distance(leftEdge, rightEdge);
                if (!onLeftEdge)
                {
                    ledge = nextLedge; 
                    grabbingLedge = true;
                    anim.SetInteger("ClimbState", 1);
                }
            }
            else if (destination == 4)
            {
                print("To the destination");
                float ledgeDistY = Mathf.Abs(ledge.bounds.center.y - nextLedge.bounds.center.y)/2.12f;
                print(ledgeDistY + " distance of the Y" );
                velocity = ledge.transform.forward * climbSpeed.x ;
                velocity.y = climbSpeed.y * ledgeDistY;
                
                float heightDifference = nextLedge.bounds.max.y - transform.position.y;
                if (heightDifference < 2.06f && heightDifference > 1.4)
                {
                    grabbingLedge = true;
                    ledge = nextLedge;
                    //anim.SetInteger("ClimbState", 1);
                }
            }else if (destination == 5)
            {
                velocity = -ledge.transform.forward * climbSpeed.x;

                velocity.y = climbSpeed.y;
                print("vel " + velocity.y);
            }


        }
        else
        {
            if (destination == 4 && anim.GetCurrentAnimatorClipInfo(0)[0].clip.name == "Upward Hop")
            {
                
                if (climbSpeed.x == 0)
                {
                    print("name: " + anim.GetCurrentAnimatorClipInfo(0)[0].clip.name);
                    anim.SetInteger("ClimbState", 1);
                }
                else
                {
                    velocity = ledge.transform.forward * climbSpeed.x;
                }
            }
            if (transform.position.y - (ledge.bounds.max.y + ledgeOffset) > 0)
            {
                Vector3 movement = new Vector3(0, -7, 0) * Time.deltaTime;
                if (movement.y < -(transform.position.y - (ledge.bounds.max.y + ledgeOffset))) movement.y = -(transform.position.y - (ledge.bounds.max.y + ledgeOffset));
                controller.Move(movement);
            }
            if (transform.position.y - (ledge.bounds.max.y + ledgeOffset) < 0)
            {
                Vector3 movement = new Vector3(0, 7, 0) * Time.deltaTime;
                if (movement.y > Mathf.Abs(transform.position.y - (ledge.bounds.max.y + ledgeOffset))) movement.y = Mathf.Abs(transform.position.y - (ledge.bounds.max.y + ledgeOffset));
                controller.Move(movement);
            }
            Vector3 rot = new Vector3(0, 180 + ledge.transform.eulerAngles.y, 0);
            transform.rotation = Quaternion.Lerp(transform.rotation, Quaternion.Euler(rot), 12 * Time.deltaTime);

            velocity.y = 0;

            Vector3 leftEdge = ledge.bounds.center + (ledge.GetComponent<BoxCollider>().size.x * .5f * ledge.transform.localScale.x * ledge.transform.right);
            bool onLeftEdge = Vector3.Distance(transform.position, leftEdge) < 1.985f && Input.GetAxis("Horizontal") < 0;
            Vector3 rightEdge = ledge.bounds.center - (ledge.GetComponent<BoxCollider>().size.x * .5f * ledge.transform.localScale.x * ledge.transform.right);
            bool onRightEdge = Vector3.Distance(transform.position, rightEdge) < 1.985f && Input.GetAxis("Horizontal") > 0;

            if (Input.GetAxis("Horizontal") != 0)
            {
                if (!onLeftEdge && !onRightEdge)
                {

                    velocity = (ledge.transform.right) * -Input.GetAxis("Horizontal") * climbSpeed.x;
                    if (Input.GetAxis("Horizontal") < 0) anim.SetInteger("ClimbState", 2);
                    else anim.SetInteger("ClimbState", 3);
                }
                else
                {
                    if(anim.GetInteger("ClimbState")<4) anim.SetInteger("ClimbState", 1);
                    velocity = Vector3.zero;
                }

            }
            else 
            {
                if(destination!=4)velocity = Vector3.zero;
                if (anim.GetInteger("ClimbState") > 1 && anim.GetInteger("ClimbState") < 4 && lastInput == 0) anim.SetInteger("ClimbState", 1);
            }

            
        }
        lastInput = Input.GetAxis("Horizontal");
        if(destination>=2) gameObject.layer = 9;

        controller.Move(velocity * Time.deltaTime);
        gameObject.layer = 0;

    }

    bool TestLedgeDown()
    {
        int layerMask = 1 << 8;
        Vector3 pos = transform.position + (transform.forward * .15f);
        RaycastHit hit;
        Physics.Raycast(pos, (Vector3.down), out hit, climbHopDistance.y * 2, layerMask, QueryTriggerInteraction.Collide);
        print(pos);
        if (hit.collider != null)
        {
            nextLedge = hit.collider;
            return true;
        }
        else
        {
            return false;
        }
    }
    bool TestLedgeUp()
    {
        int layerMask = 1 << 8;
        Vector3 pos = transform.position + (transform.forward * .15f) + (Vector3.up *2.1f);
        RaycastHit hit;
        Physics.Raycast(pos, (Vector3.up), out hit, climbHopDistance.y * 2, layerMask, QueryTriggerInteraction.Collide);

        if (hit.collider != null)
        {
            nextLedge = hit.collider;
            return true;
        }
        else
        {
            return false;
        }
    }
    bool TestLedgeSides(int left)
    {
        int layerMask = 1 << 8;
        Vector3 pos = transform.position + (transform.forward * .15f) + (Vector3.up * 1.9f);
        RaycastHit hit;
        Physics.Raycast(pos, (ledge.transform.right* left), out hit, climbHopDistance.x * 2, layerMask, QueryTriggerInteraction.Collide);

        if (hit.collider != null)
        {
            nextLedge = hit.collider;
            return true;
        }
        else
        {
            return false;
        }
    }
    bool TestLedgeForward()
    {
       
        Vector3 pos = transform.position + (transform.forward * .15f) + (Vector3.up * 2.5f);
        RaycastHit hit;
        Physics.Raycast(pos, (-ledge.transform.forward), out hit, climbHopDistance.x * 2, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);

        print(pos + " forward");
        if (hit.collider != null)
        {
            return false;
        }
        else
        {
            return true;
        }
    }
    public void LetGo()
    {
        grabbingLedge = false;
    }

    public void ClimbedUp()
    {
        climbing = false;
        velocity.y = 0;
    }

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
    }
    
    private void OnTriggerEnter(Collider other)
    {
        if (other.tag == "Ledge") GrabLedge(other);
    }

    private void OnTriggerStay(Collider other)
    {
        if(other.tag=="Ledge")GrabLedge(other);
    }

    void GrabLedge(Collider collider)
    {
        float heightDifference = collider.bounds.max.y - transform.position.y;
        bool correctAngle = Mathf.Abs(180 + targetDirection.eulerAngles.y - collider.transform.eulerAngles.y) < 25;
        if (!correctAngle) correctAngle = Mathf.Abs(-180 + targetDirection.eulerAngles.y - collider.transform.eulerAngles.y) < 25;
        
        if (!controller.isGrounded && !climbing && heightDifference < 1.8 && heightDifference > 1.4 && correctAngle)
        {
            climbing = true;
            anim.SetInteger("ClimbState", 1);
            ledge = collider;
            grabbingLedge = true;
            velocity = Vector3.zero;
            lastInput = 0;
        }
    }

    public void Respawn(Transform respawn)
    {
        print("please work");
        gameObject.layer = 9;
        controller.Move(respawn.position - transform.position);
        velocity = Vector3.zero;
        gameObject.layer = 0;
        //      transform.poition = respawn.position;
    }
     
}
    
