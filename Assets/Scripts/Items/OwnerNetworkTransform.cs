using Unity.Netcode.Components;

/// NetworkTransform variant where the owning client (not the server) is authoritative
/// for sending transform updates. Used on holdable packages so the player holding an
/// object drives its movement locally with zero network round-trip.
public class OwnerNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative() => false;
}