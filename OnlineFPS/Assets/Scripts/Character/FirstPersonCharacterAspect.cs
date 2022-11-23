using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Physics.Authoring;
using Unity.Physics.Extensions;
using Unity.Physics.Systems;
using Unity.Transforms;
using Rival;
using Unity.NetCode;
using UnityEngine;

public struct FirstPersonCharacterUpdateContext
{
    // Here, you may add additional global data for your character updates, such as ComponentLookups, Singletons, NativeCollections, etc...
    // The data you add here will be accessible in your character updates and all of your character "callbacks".
    [ReadOnly]
    public ComponentLookup<WeaponVisualFeedback> WeaponVisualFeedbackLookup;
    [ReadOnly]
    public ComponentLookup<WeaponControl> WeaponControlLookup;

    public void OnSystemCreate(ref SystemState state)
    {
        WeaponVisualFeedbackLookup = state.GetComponentLookup<WeaponVisualFeedback>(true);
        WeaponControlLookup = state.GetComponentLookup<WeaponControl>(true);
    }

    public void OnSystemUpdate(ref SystemState state)
    {
        WeaponVisualFeedbackLookup.Update(ref state);
        WeaponControlLookup.Update(ref state);
    }
}

public readonly partial struct FirstPersonCharacterAspect : IAspect, IKinematicCharacterProcessor<FirstPersonCharacterUpdateContext>
{
    public readonly KinematicCharacterAspect CharacterAspect;
    public readonly RefRW<FirstPersonCharacterComponent> CharacterComponent;
    public readonly RefRW<FirstPersonCharacterControl> CharacterControl;
    public readonly RefRW<ActiveWeapon> ActiveWeapon;

    public void PhysicsUpdate(ref FirstPersonCharacterUpdateContext context, ref KinematicCharacterUpdateContext baseContext)
    {
        ref FirstPersonCharacterComponent characterComponent = ref CharacterComponent.ValueRW;
        ref KinematicCharacterBody characterBody = ref CharacterAspect.CharacterBody.ValueRW;

        // First phase of default character update
        CharacterAspect.Update_Initialize(baseContext.Time.DeltaTime);
        UpdateGroundingUp();
        CharacterAspect.Update_ParentMovement(in this, ref context, ref baseContext, characterBody.WasGroundedBeforeCharacterUpdate);
        CharacterAspect.Update_Grounding(in this, ref context, ref baseContext);
        
        // Update desired character velocity after grounding was detected, but before doing additional processing that depends on velocity
        HandleVelocityControl(ref context, ref baseContext);

        // Second phase of default character update
        CharacterAspect.Update_PreventGroundingFromFutureSlopeChange(in this, ref context, ref baseContext, in characterComponent.StepAndSlopeHandling);
        CharacterAspect.Update_GroundPushing(in this, ref context, ref baseContext, characterComponent.Gravity);
        CharacterAspect.Update_MovementAndDecollisions(in this, ref context, ref baseContext);
        CharacterAspect.Update_MovingPlatformDetection(ref baseContext); 
        CharacterAspect.Update_ParentMomentum(ref baseContext);
        CharacterAspect.Update_ProcessStatefulCharacterHits();
    }

    private void HandleVelocityControl(ref FirstPersonCharacterUpdateContext context, ref KinematicCharacterUpdateContext baseContext)
    { 
        float deltaTime = baseContext.Time.DeltaTime;
        ref KinematicCharacterBody characterBody = ref CharacterAspect.CharacterBody.ValueRW;
        ref FirstPersonCharacterComponent characterComponent = ref CharacterComponent.ValueRW;
        ref FirstPersonCharacterControl characterControl = ref CharacterControl.ValueRW;
        
        if (characterBody.IsGrounded)
        {
            // Move on ground
            float3 targetVelocity = characterControl.MoveVector * characterComponent.GroundMaxSpeed;
            CharacterControlUtilities.StandardGroundMove_Interpolated(ref characterBody.RelativeVelocity, targetVelocity, characterComponent.GroundedMovementSharpness, deltaTime, characterBody.GroundingUp, characterBody.GroundHit.Normal);

            // Jump
            if (characterControl.Jump)
            {
                CharacterControlUtilities.StandardJump(ref characterBody, characterBody.GroundingUp * characterComponent.JumpSpeed, true, characterBody.GroundingUp);
            }
        }
        else
        {
            // Move in air
            float3 airAcceleration = characterControl.MoveVector * characterComponent.AirAcceleration;
            if (math.lengthsq(airAcceleration) > 0f)
            {
                float3 tmpVelocity = characterBody.RelativeVelocity;
                CharacterControlUtilities.StandardAirMove(ref characterBody.RelativeVelocity, airAcceleration, characterComponent.AirMaxSpeed, characterBody.GroundingUp, deltaTime, false);

                // Cancel air acceleration from input if we would hit a non-grounded surface (prevents air-climbing slopes at high air accelerations)
                if (characterComponent.PreventAirAccelerationAgainstUngroundedHits && CharacterAspect.MovementWouldHitNonGroundedObstruction(in this, ref context, ref baseContext, characterBody.RelativeVelocity * deltaTime, out ColliderCastHit hit))
                {
                    characterBody.RelativeVelocity = tmpVelocity;
                }
            }
            
            // Gravity
            CharacterControlUtilities.AccelerateVelocity(ref characterBody.RelativeVelocity, characterComponent.Gravity, deltaTime);

            // Drag
            CharacterControlUtilities.ApplyDragToVelocity(ref characterBody.RelativeVelocity, deltaTime, characterComponent.AirDrag);
        }
    }

    public void VariableUpdate(ref FirstPersonCharacterUpdateContext context, ref KinematicCharacterUpdateContext baseContext)
    {
        ref KinematicCharacterBody characterBody = ref CharacterAspect.CharacterBody.ValueRW;
        ref FirstPersonCharacterComponent characterComponent = ref CharacterComponent.ValueRW;
        ref quaternion characterRotation = ref CharacterAspect.LocalTransform.ValueRW.Rotation;
        FirstPersonCharacterControl characterControl = CharacterControl.ValueRO;
        ActiveWeapon activeWeapon = ActiveWeapon.ValueRO;

        // Add rotation from parent body to the character rotation
        // (this is for allowing a rotating moving platform to rotate your character as well, and handle interpolation properly)
        KinematicCharacterUtilities.AddVariableRateRotationFromFixedRateRotation(ref characterRotation, characterBody.RotationFromParent, baseContext.Time.DeltaTime, characterBody.LastPhysicsUpdateDeltaTime);

        // View roll angles
        {
            float3 characterRight = MathUtilities.GetRightFromRotation(characterRotation);
            float characterMaxSpeed = characterBody.IsGrounded ? characterComponent.GroundMaxSpeed : characterComponent.AirMaxSpeed;
            float3 characterLateralVelocity = math.projectsafe(characterBody.RelativeVelocity, characterRight);
            float characterLateralVelocityRatio = math.clamp(math.length(characterLateralVelocity) / characterMaxSpeed, 0f, 1f);
            bool velocityIsRight = math.dot(characterBody.RelativeVelocity, characterRight) > 0f;
            float targetTiltAngle = math.lerp(0f, characterComponent.ViewRollAmount, characterLateralVelocityRatio);
            targetTiltAngle = velocityIsRight ? -targetTiltAngle : targetTiltAngle;
            characterComponent.ViewRollDegrees = math.lerp(characterComponent.ViewRollDegrees, targetTiltAngle, math.saturate(characterComponent.ViewRollSharpness * baseContext.Time.DeltaTime));
        }
        
        // Handle aiming look sensitivity
        if (context.WeaponControlLookup.TryGetComponent(activeWeapon.Entity, out WeaponControl weaponControl))
        {
            if (weaponControl.AimHeld)
            {
                if (context.WeaponVisualFeedbackLookup.TryGetComponent(activeWeapon.Entity, out WeaponVisualFeedback weaponFeedback))
                {
                    characterControl.LookYawPitchDegrees *= weaponFeedback.LookSensitivityMultiplierWhileAiming;
                }
            }
        }
        
        // Compute character & view rotations from rotation input
        FirstPersonCharacterUtilities.ComputeFinalRotationsFromRotationDelta(
            ref characterComponent.ViewPitchDegrees,
            ref characterComponent.CharacterYDegrees,
            math.up(),
            characterControl.LookYawPitchDegrees,
            characterComponent.ViewRollDegrees,
            characterComponent.MinViewAngle, 
            characterComponent.MaxViewAngle,
            out characterRotation,
            out float canceledPitchDegrees,
            out characterComponent.ViewLocalRotation);
    }
    
    #region Character Processor Callbacks
    public void UpdateGroundingUp()
    {
        CharacterAspect.Default_UpdateGroundingUp();
    }
    
    public bool CanCollideWithHit(
        ref FirstPersonCharacterUpdateContext context, 
        ref KinematicCharacterUpdateContext baseContext,
        in BasicHit hit)
    {
        return KinematicCharacterUtilities.IsHitCollidableOrCharacter(
            in baseContext.StoredCharacterBodyPropertiesLookup, 
            hit.Material, 
            hit.Entity);
    }

    public bool IsGroundedOnHit(
        ref FirstPersonCharacterUpdateContext context, 
        ref KinematicCharacterUpdateContext baseContext,
        in BasicHit hit, 
        int groundingEvaluationType)
    {
        FirstPersonCharacterComponent characterComponent = CharacterComponent.ValueRO;
        
        return CharacterAspect.Default_IsGroundedOnHit(
            in this,
            ref context,
            ref baseContext,
            in hit,
            in characterComponent.StepAndSlopeHandling,
            groundingEvaluationType);
    }

    public void OnMovementHit(
            ref FirstPersonCharacterUpdateContext context,
            ref KinematicCharacterUpdateContext baseContext,
            ref KinematicCharacterHit hit,
            ref float3 remainingMovementDirection,
            ref float remainingMovementLength,
            float3 originalVelocityDirection,
            float hitDistance)
    {
        FirstPersonCharacterComponent characterComponent = CharacterComponent.ValueRO;
        
        CharacterAspect.Default_OnMovementHit(
            in this,
            ref context,
            ref baseContext,
            ref hit,
            ref remainingMovementDirection,
            ref remainingMovementLength,
            originalVelocityDirection,
            hitDistance,
            characterComponent.StepAndSlopeHandling.StepHandling,
            characterComponent.StepAndSlopeHandling.MaxStepHeight);
    }

    public void OverrideDynamicHitMasses(
        ref FirstPersonCharacterUpdateContext context,
        ref KinematicCharacterUpdateContext baseContext,
        ref PhysicsMass characterMass,
        ref PhysicsMass otherMass,
        BasicHit hit)
    {
    }

    public void ProjectVelocityOnHits(
        ref FirstPersonCharacterUpdateContext context,
        ref KinematicCharacterUpdateContext baseContext,
        ref float3 velocity,
        ref bool characterIsGrounded,
        ref BasicHit characterGroundHit,
        in DynamicBuffer<KinematicVelocityProjectionHit> velocityProjectionHits,
        float3 originalVelocityDirection)
    {
        FirstPersonCharacterComponent characterComponent = CharacterComponent.ValueRO;
        
        CharacterAspect.Default_ProjectVelocityOnHits(
            ref velocity,
            ref characterIsGrounded,
            ref characterGroundHit,
            in velocityProjectionHits,
            originalVelocityDirection,
            characterComponent.StepAndSlopeHandling.ConstrainVelocityToGroundPlane);
    }
    #endregion
}