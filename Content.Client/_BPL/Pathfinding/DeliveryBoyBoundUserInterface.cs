using Content.Shared._BPL.Pathfinding;
using Robust.Client.UserInterface;

namespace Content.Client._BPL.Pathfinding;

public sealed class DeliveryBoyBoundUserInterface : BoundUserInterface
{
    private DeliveryBoyWindow? _window;

    public DeliveryBoyBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<DeliveryBoyWindow>();
        _window.OnConfirm += beacon =>
        {
            SendMessage(new DeliveryBoyGoToBeaconMessage(beacon));
            Close();
        };
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is DeliveryBoyBoundUserInterfaceState list)
            _window?.Populate(list.Beacons);
    }
}
