namespace PaymentSandbox.Finality.Evaluation;

public sealed class FinalityEvaluationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
