namespace PaymentSandbox.Permits.Workflow;

/// <summary>Stable workflow conflict without signature-bearing diagnostics.</summary>
public sealed class PermitWorkflowException : Exception
{
    public PermitWorkflowException(string message)
        : base(message)
    {
    }
}
