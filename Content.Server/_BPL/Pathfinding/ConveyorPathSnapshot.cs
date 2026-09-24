using System.Numerics;
using Content.Shared.Conveyor;
using Robust.Shared.Maths;

namespace Content.Server._BPL.Pathfinding;

/// <summary>
/// Thread-safe copy of a conveyor belt's live direction for A* workers.
/// </summary>
public readonly record struct ConveyorPathSnapshot(
    bool Running,
    Vector2 WorldDirection,
    float Speed,
    ConveyorState State,
    bool Powered)
{
    /// <summary>
    /// World-space unit vector matching <see cref="Content.Shared.Physics.Controllers.SharedConveyorController"/> conveyance.
    /// Reverse adds π.
    /// </summary>
    public static Vector2 ComputeWorldDirection(ConveyorComponent comp, Angle worldRotation)
    {
        var rot = worldRotation + comp.Angle;
        if (comp.State == ConveyorState.Reverse)
            rot += MathF.PI;

        return rot.ToWorldVec();
    }

    public static ConveyorPathSnapshot From(ConveyorComponent comp, Angle worldRotation)
    {
        var running = comp.State != ConveyorState.Off && comp.Powered;
        return new ConveyorPathSnapshot(
            running,
            ComputeWorldDirection(comp, worldRotation),
            comp.Speed,
            comp.State,
            comp.Powered);
    }
}
