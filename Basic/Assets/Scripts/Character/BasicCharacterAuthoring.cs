using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics.Authoring;
using UnityEngine;
using Unity.CharacterController;
using Unity.Physics;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
[RequireComponent(typeof(PhysicsShapeAuthoring))]
public class BasicCharacterAuthoring : MonoBehaviour
{
    public AuthoringKinematicCharacterProperties CharacterProperties = AuthoringKinematicCharacterProperties.GetDefault();
    public BasicCharacterComponent Character = BasicCharacterComponent.GetDefault();

    public class Baker : Baker<BasicCharacterAuthoring>
    {
        public override void Bake(BasicCharacterAuthoring authoring)
        {
            KinematicCharacterUtilities.BakeCharacter(this, authoring, authoring.CharacterProperties);

            AddComponent(authoring.Character);
            AddComponent(new BasicCharacterControl());
        }
    }
}