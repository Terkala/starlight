using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.Administration.Managers;
using Content.Shared._BPL.Pathfinding;
using Content.Shared.Interaction;
using Content.Shared.NPC;
using Content.Shared.Pinpointer;
using Content.Shared.Popups;
using Robust.Server.GameObjects;

namespace Content.Server._BPL.Pathfinding;

public sealed partial class DeliveryBoySystem : EntitySystem
{
    [Dependency] private ISharedAdminManager _admin = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DeliveryBoyComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<DeliveryBoyComponent, InteractHandEvent>(OnInteractHand);

        Subs.BuiEvents<DeliveryBoyComponent>(DeliveryBoyUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<DeliveryBoyGoToBeaconMessage>(OnGoToBeacon);
        });
    }

    private void OnMapInit(Entity<DeliveryBoyComponent> ent, ref MapInitEvent args)
    {
        EnsureComp<ActiveNPCComponent>(ent);
    }

    private void OnInteractHand(Entity<DeliveryBoyComponent> ent, ref InteractHandEvent args)
    {
        if (args.Handled || !_admin.IsAdmin(args.User))
            return;

        try
        {
            if (_ui.TryOpenUi(ent.Owner, DeliveryBoyUiKey.Key, args.User))
                args.Handled = true;
        }
        catch (Exception e)
        {
            Log.Error($"Delivery boy UI open failed for {ToPrettyString(ent)}: {e}");
        }
    }

    private void OnUiOpened(Entity<DeliveryBoyComponent> ent, ref BoundUIOpenedEvent args)
    {
        try
        {
            _ui.SetUiState(ent.Owner, DeliveryBoyUiKey.Key, new DeliveryBoyBoundUserInterfaceState(CollectBeacons(ent)));
        }
        catch (Exception e)
        {
            Log.Error($"Delivery boy UI state failed for {ToPrettyString(ent)}: {e}");
        }
    }

    private void OnGoToBeacon(Entity<DeliveryBoyComponent> ent, ref DeliveryBoyGoToBeaconMessage args)
    {
        try
        {
            TryGoToBeacon(ent, ref args);
        }
        catch (Exception e)
        {
            Log.Error($"Delivery boy dispatch failed for {ToPrettyString(ent)}: {e}");
            if (args.Actor is { Valid: true } actor)
                _popup.PopupEntity(Loc.GetString("delivery-boy-popup-invalid"), ent, actor);
        }
    }

    private void TryGoToBeacon(Entity<DeliveryBoyComponent> ent, ref DeliveryBoyGoToBeaconMessage args)
    {
        if (args.Actor is not { Valid: true } actor || !_admin.IsAdmin(actor))
            return;

        if (!TryGetEntity(args.Beacon, out var beacon) ||
            !TryComp(beacon, out NavMapBeaconComponent? nav) ||
            !nav.Enabled)
        {
            _popup.PopupEntity(Loc.GetString("delivery-boy-popup-invalid"), ent, actor);
            return;
        }

        var boyXform = Transform(ent);
        var beaconUid = beacon.Value;
        var beaconXform = Transform(beaconUid);
        if (boyXform.GridUid is not { } grid || beaconXform.GridUid != grid)
        {
            _popup.PopupEntity(Loc.GetString("delivery-boy-popup-wrong-grid"), ent, actor);
            return;
        }

        EnsureComp<ActiveNPCComponent>(ent);
        var flags = PathFlags.Interact | _pathfinding.GetFlags(ent.Owner);
        var steering = _steering.Register(ent, beaconXform.Coordinates);
        steering.Flags = flags;
        steering.Range = ent.Comp.ArriveRange;

        var label = BeaconLabel(beaconUid, nav, beaconXform);
        _popup.PopupEntity(Loc.GetString("delivery-boy-popup-going", ("beacon", label)), ent, actor);
        _ui.CloseUi(ent.Owner, DeliveryBoyUiKey.Key, actor);
    }

    private List<DeliveryBoyBeaconEntry> CollectBeacons(EntityUid boy)
    {
        var list = new List<DeliveryBoyBeaconEntry>();
        if (Transform(boy).GridUid is not { } grid)
            return list;

        var query = EntityQueryEnumerator<NavMapBeaconComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var beacon, out var xform))
        {
            if (!beacon.Enabled || !xform.Anchored || xform.GridUid != grid)
                continue;

            list.Add(new DeliveryBoyBeaconEntry(GetNetEntity(uid), BeaconLabel(uid, beacon, xform)));
        }

        list.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    private string BeaconLabel(EntityUid uid, NavMapBeaconComponent beacon, TransformComponent xform)
    {
        var name = beacon.Text;
        if (string.IsNullOrWhiteSpace(name))
            name = Name(uid);

        var pos = xform.Coordinates.Position;
        return Loc.GetString("delivery-boy-beacon-label",
            ("name", name),
            ("x", (int)MathF.Round(pos.X)),
            ("y", (int)MathF.Round(pos.Y)));
    }
}
