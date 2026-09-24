using Robust.Shared.Serialization;

namespace Content.Shared._BPL.Pathfinding;

[Serializable, NetSerializable]
public enum DeliveryBoyUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public readonly record struct DeliveryBoyBeaconEntry(NetEntity Beacon, string Label);

[Serializable, NetSerializable]
public sealed class DeliveryBoyBoundUserInterfaceState(List<DeliveryBoyBeaconEntry> beacons)
    : BoundUserInterfaceState
{
    public List<DeliveryBoyBeaconEntry> Beacons { get; } = beacons;
}

[Serializable, NetSerializable]
public sealed class DeliveryBoyGoToBeaconMessage(NetEntity beacon) : BoundUserInterfaceMessage
{
    public NetEntity Beacon { get; } = beacon;
}
