namespace PaymentSandbox.Permits.Erc2612;

/// <summary>A canonical permit whose EOA signature recovered to its owner.</summary>
public sealed class VerifiedErc2612Permit
{
    private readonly byte[] _r;
    private readonly byte[] _s;

    internal VerifiedErc2612Permit(Erc2612PermitDraft draft, byte v, byte[] r, byte[] s)
    {
        Draft = draft;
        V = v;
        _r = r;
        _s = s;
    }

    public Erc2612PermitDraft Draft { get; }
    public byte V { get; }
    public byte[] R => _r.ToArray();
    public byte[] S => _s.ToArray();

    public override string ToString() =>
        $"Verified ERC-2612 permit {Draft.Digest} for {Draft.Owner.Value} " +
        "(signature redacted)";
}
