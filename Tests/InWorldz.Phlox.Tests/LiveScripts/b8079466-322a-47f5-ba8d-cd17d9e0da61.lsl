//  Sit & recline, Copyright 2013, Balpien Hammerer
//  This script is licensed under Creative Commons, CC4 BY-SA
//  http://creativecommons.org/licenses/by-sa/4.0/

// 2013/10/16 - fix altitude lock
// 2014/09/20 - Add OwnerOnly switch
// 2014/09/23 - Fix motion control
// 2023/02/22 - Adapt to OpenSim, add opensim bug mitigations.
//  

//
// The balloon script is set to not engage when sat upon. Because balloons are
// often large structures, it is better to set up a child prim that is the sit spot.
// Use the provided sit script to engage this vehicle script.
//
// Balloons usually slip and slide through the air, but that can make the task of level
// flight difficult. Set the altitude lock to TRUE to have the script lock the altitude after
// the altitude control keys are released.
//
// Controls are:    left-arrow (or A) to turn left
//                  right-arrow (or D) to turn right
//                  up-arrow (or W) to move forward
//                  down-arrow (or S) to move backward
//                  PgUp (or E) to gain altitude
//                  PgDn (or C) to lose altitude


// These are the settings for the dynamic camera. If you do not
// want any scripted camera action, or if you have another script
// doing that, then set all of these values to 0.0
float   CameraPitch     = 15.0;
float   CameraDistance  =  2.0;
float   CameraLag       =  0.1;

// When set to TRUE, the balloon will lock itself to a fixed altitude.
// The lock goes off when going up or down, and the time to relock, after
// the up or down key is released, determined by the LockTime variable (in seconds).
integer AltitudeLock    = TRUE;
float   AltitudeLockTime= 2.0;

// Set this to TRUE if you want only you, the owner, to use the balloon.
integer OwnerOnly       = TRUE;

// This is the sit target, the offset and rotation from the center of the root
// prim. If you have another script doing that, then set these values to ZERO_VECTOR.
// This is the recommended default.
vector  SitPosition     = ZERO_VECTOR;
vector  SitRotation     = ZERO_VECTOR;

// These value set the maximum forward, reverse and sideways speeds (in meters/second).
float   MaxFwdSpeed     =  27.0;
float   MaxRevSpeed     = -4.0;
float   MaxSideSpeed    = 12.0;
float   MaxVertSpeed    = 30.0;

// This adjusts the turning rate (yaw)
float   MaxYawSpeed     = PI/2;


// ------------------------------------------------------------------------------

integer Engaged = FALSE;
integer Debounce;
key     SittingAgent = NULL_KEY;

SetCameraParams()
{
    if (llGetPermissions() & PERMISSION_CONTROL_CAMERA)
    {
        if (CameraDistance == 0 && CameraPitch == 0)
        {
            llSetCameraParams( [CAMERA_ACTIVE, FALSE]);
            return;
        }
    
        // These parameters keeps the camera close in on slow movement but backs off
        // when traqvelling quickly. What this does is make steering vastly easier during
        // high speed runs.
        llSetCameraParams( [CAMERA_ACTIVE, TRUE, 
                            CAMERA_FOCUS_LOCKED, FALSE,
                            CAMERA_FOCUS_THRESHOLD, 0.3,
                            CAMERA_FOCUS_OFFSET, <0.0, 0.0, 1.4>,
                            CAMERA_PITCH, CameraPitch,
                            CAMERA_DISTANCE, CameraDistance,
                            CAMERA_BEHINDNESS_ANGLE, 0.6,
                            CAMERA_POSITION_THRESHOLD, 0.2,
                            CAMERA_POSITION_LAG, CameraLag]);
    }
}

// The default vehicle properties. Adjust to your needs.
SetVehicleSettings()
{
    SetVehicleSettings("");
}
SetVehicleSettings(string filter)
{
    if (filter == "")
    {
        llSetVehicleType(VEHICLE_TYPE_BALLOON);
        
        llSetVehicleVectorParam(VEHICLE_ANGULAR_MOTOR_TIMESCALE,<2.0, 2.0, 1.0>);
        llSetVehicleVectorParam(VEHICLE_ANGULAR_MOTOR_DECAY_TIMESCALE,<0.3, 0.3, 3.0>);
    }
    
    if (filter == "" || filter == "friction")
    {
        llSetVehicleVectorParam(VEHICLE_ANGULAR_FRICTION_TIMESCALE, <2.0, 0.5, 1.0>);
        llSetVehicleVectorParam(VEHICLE_LINEAR_FRICTION_TIMESCALE, <500.0, 0.5, 2.0>);
    }
    
    if (filter == "")
    {
        llSetVehicleFloatParam(VEHICLE_ANGULAR_DEFLECTION_TIMESCALE, 1000.0);
        
        llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_TIMESCALE,<2.0, 2.0, 0.5>);
        llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DECAY_TIMESCALE,<500, 0.3, 1.0>);
        
        llSetVehicleFloatParam(VEHICLE_VERTICAL_ATTRACTION_TIMESCALE, 4.0);
        llSetVehicleFloatParam(VEHICLE_VERTICAL_ATTRACTION_EFFICIENCY, 0.5);
        
        llSetVehicleFloatParam(VEHICLE_BANKING_EFFICIENCY, 0.05);
        llSetVehicleFloatParam(VEHICLE_BANKING_MIX, 0.0);
        
        llSetVehicleFloatParam(VEHICLE_BUOYANCY, 1.0);
        llSetVehicleFlags(VEHICLE_FLAG_HOVER_GLOBAL_HEIGHT);
        vector  pos = llGetPos();
        llSetVehicleFloatParam(VEHICLE_HOVER_HEIGHT, pos.z);
        llSetVehicleFloatParam(VEHICLE_HOVER_TIMESCALE, 100.0);
    }
}

Engage(key id)
{
    // Got on
    if (id != NULL_KEY)
    {
        // If owneronly check sitting agent is owner.
        if (!OwnerOnly || id == llGetOwner())
        {
            llRequestPermissions(id, PERMISSION_TAKE_CONTROLS | PERMISSION_CONTROL_CAMERA);
            llMessageLinked(LINK_SET, TRUE, "engage", id);
            llSetVehicleFloatParam(VEHICLE_HOVER_HEIGHT, 0);
            Engaged = TRUE;
            llTriggerSound("SteamRelease", 1.0);
        }
        else
        {
            // Not permitted to use this vehicle
            Engaged = FALSE;
            llTriggerSound("Error", 1.0);
            llUnSit(id);
        }
    }
    
    // Got off
    else
    {
        llSetStatus(STATUS_PHYSICS, FALSE);
        llSetStatus(STATUS_PHANTOM, FALSE);
        llReleaseControls();
        llClearCameraParams();
        llMessageLinked(LINK_SET, FALSE, "engage", id);
        llSetTimerEvent(0);
        llSetVehicleFloatParam(VEHICLE_HOVER_HEIGHT, 0);
        Engaged = FALSE;
        llTriggerSound("Stopping", 1.0);
    }
}

HandleControls(key id, integer level, integer edge)
{
    vector  lvel = llGetVel() / llGetRot();   // Current local velocity
    vector  angular;
    vector  linear;
    integer pressed  = level & edge;
    integer released = ~level & edge;
    integer mouselook = llGetAgentInfo(id) & AGENT_MOUSELOOK;
    
    //llOwnerSay("controls " + (string)level + " " + (string)edge);
    
    //Send control message to other scripts (useful for FX behaviors)
    if (pressed)    llMessageLinked(LINK_SET, pressed, "controls-pressed", id);
    if (released)   llMessageLinked(LINK_SET, released, "controls-released", id);
    
    // If the brakes are released, restore the vehicle friction. 
    if (Debounce && (level & (CONTROL_FWD | CONTROL_BACK))!=(CONTROL_FWD | CONTROL_BACK) )
    {
        Debounce = FALSE;
        SetVehicleSettings("friction");
    }
    
    // If the brakes are engaged (up & down arrow keys or W & S keys pressed), stop all movement
    // smoothly and quickly.
    if (level ==(CONTROL_FWD | CONTROL_BACK))
    {
        level = level & ~(CONTROL_FWD | CONTROL_BACK);
        if (!Debounce) llTriggerSound("Stopping", 1.0);
        Debounce = TRUE;
        llSetVehicleVectorParam(VEHICLE_LINEAR_FRICTION_TIMESCALE, <2,2,2>);
        llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, <0,0,0>);
        llSetVehicleVectorParam(VEHICLE_ANGULAR_MOTOR_DIRECTION, <0,0,0>);
    }
    
    if (level & CONTROL_FWD)
    {
        linear += <MaxFwdSpeed,0,0>;
    }
    
    if (level & CONTROL_BACK)
    {
        linear += <MaxRevSpeed,0,0>;
    }
    
    if (level & CONTROL_LEFT && !mouselook)
    {
        linear += <0,MaxSideSpeed,0>;
    }
    
    if (level & CONTROL_RIGHT && !mouselook)
    {
        linear += <0,-MaxSideSpeed,0>;
    }
    
    if (level & CONTROL_UP)
    {
        linear += <0,0,MaxVertSpeed>; 
        if (AltitudeLock)
        {
            llSetTimerEvent(AltitudeLockTime);
            llSetVehicleFloatParam(VEHICLE_HOVER_TIMESCALE, 1000.0);
        }
    }
    
    if (level & CONTROL_DOWN)
    {
        linear += <0,0,-MaxVertSpeed>;
        if (AltitudeLock)
        {
            llSetTimerEvent(AltitudeLockTime);
            llSetVehicleFloatParam(VEHICLE_HOVER_TIMESCALE, 1000.0);
        }            
    }
    
    if (mouselook)
    {
        if (released & CONTROL_LEFT)
        {
            angular = <0,0,0.0001>;
        }
        else if (level & CONTROL_LEFT)
        {
            angular += <-PI/64, 0, MaxYawSpeed>;
        }
    }
    else
    {
        if (released & CONTROL_ROT_LEFT)
        {
            angular = <0,0,0.0001>;
        }
        else if (level & CONTROL_ROT_LEFT)
        {
            angular += <-PI/64, 0, MaxYawSpeed>;
        }
    }
    
    if (mouselook)
    {
        if (released & CONTROL_RIGHT)
        {
            angular = <0,0,0.0001>;
        }
        else if (level & CONTROL_RIGHT)
        {
            angular += <PI/64, 0, -MaxYawSpeed>;
        }
    }
    else
    {
        if (released & CONTROL_ROT_RIGHT)
        {
            angular = <0,0,0.0001>;
        }
        else if (level & CONTROL_ROT_RIGHT)
        {
            angular += <PI/64, 0, -MaxYawSpeed>;
        }
    }
    
    
    // Linear speed adjustments
    // A flaw in the motors (fixed in IW) is that when a motor is set, all axes are live. There is no
    // magic value thtr indicates to leave an axis runnning unaltered. To mitigate this
    // flaw, massume that for any motor not set, that the vehicle should continue
    // to go at the current speed in the respective axis.
    if (linear.x == 0) linear.x = lvel.x;
    if (linear.y == 0) linear.y = lvel.y;
    if (linear.z == 0) linear.z = lvel.z;
    
    // Apply motors
    if (linear != ZERO_VECTOR)
    {
        llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, linear);
    }
    
    if (angular != ZERO_VECTOR)
    {
        llSetVehicleVectorParam(VEHICLE_ANGULAR_MOTOR_DIRECTION, angular);
    }
}

default
{
    state_entry()
    {
        llSetStatus(STATUS_PHYSICS, FALSE);
        llSetVehicleType(VEHICLE_TYPE_NONE);
        llSitTarget(ZERO_VECTOR, ZERO_ROTATION);
        
        // Set a sit target only if specified
        llSitTarget(ZERO_VECTOR, ZERO_ROTATION);
        if (SitPosition != ZERO_VECTOR)
        {
            llSitTarget(SitPosition, llEuler2Rot(SitRotation));
        }
        
        llMessageLinked(LINK_SET, FALSE, "engage", NULL_KEY);
        llUnSit(llAvatarOnSitTarget());
    }
    
    changed(integer chg)
    {
        if (chg & CHANGED_LINK)
        {
            // If a sit target is specified in this script, process it.
            if (SitPosition != ZERO_VECTOR)
            {
                // The changed_link event is sent to all scripts no matter where
                // anyone sat or stood.
                key id = llAvatarOnSitTarget(); 
                if (SittingAgent != NULL_KEY) 
                {
                    // Someone was sitting on me now.
                    if (id == NULL_KEY)
                    {
                        SittingAgent = id;
                        Engage(id);
                    }
                }
                else
                {
                    // No one was sitting on me presently.
                    if (id != NULL_KEY)
                    {
                        // They are sitting on me now.
                        SittingAgent = id;
                        Engage(id);
                    }
                }
            }          
        }
        
        // Region crossing mitigationd of OpenSim bugs.
        if (chg & CHANGED_REGION)
        {
            if (Engaged)
            {
                // Re-engage the motors
                llSetTimerEvent(0.5);
                
                vector lvel = llGetVel() / llGetRot();
                llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, <lvel.x,lvel.y,0>);
                
                // Realign the camera
                llSleep(0.5);
                llClearCameraParams();
                SetCameraParams();
                
                // Reopen listen channels
                // Add code here.
            }
        }
        
        if (chg & CHANGED_OWNER)
        {
            llResetScript();
        }
    }
    
    run_time_permissions(integer perms)
    {
        if (perms & PERMISSION_TAKE_CONTROLS)
        {
            llTakeControls(CONTROL_FWD | CONTROL_BACK | CONTROL_ROT_LEFT | CONTROL_ROT_RIGHT | 
                           CONTROL_LEFT | CONTROL_RIGHT | CONTROL_UP | CONTROL_DOWN, TRUE, FALSE);
            llSetStatus(STATUS_PHYSICS, TRUE);
            SetVehicleSettings();
            llSetTimerEvent(0.2);
        }
        
        if (perms & PERMISSION_CONTROL_CAMERA)
        {
            if (CameraDistance == 0 && CameraPitch == 0)
            {
                // Dynamic camera disabled.
                llSetCameraParams( [CAMERA_ACTIVE, FALSE]);
            }
            else
            {
                // Dynamic camera enabled.
                SetCameraParams();
            }
        }
    }
    
    // This timer is called when altitude lock is enabled. Usually this is off for 
    // hot air balloons but enabled for airships like blimps or zepellins.
    timer()
    {
        vector  pos = llGetPos();
        llSetVehicleFloatParam(VEHICLE_HOVER_HEIGHT, pos.z);
        llSetVehicleFloatParam(VEHICLE_HOVER_TIMESCALE, 100.0);
        llSetTimerEvent(0);
    }

    // Control keys from the pilot
    control(key id, integer level, integer edge)
    {
        HandleControls(id, level, edge);
    }
    
    link_message(integer sender, integer num, string msg, key id)
    {
        if (msg == "sit-target")
        {
            Engage(id);
        }
        
        if (msg == "controls")
        {
            integer edge = (integer)id;
            HandleControls(id, num, edge);
        }
    }
} 
