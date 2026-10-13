namespace APIBack.Integrations.Ifood.Contracts;

/// <summary>Estado interno de uma operação; não representa o lifecycle remoto do pedido.</summary>
public enum IfoodOperationState { Unknown, Queued, Processing, Uncertain, Succeeded, Rejected, Review }

public static class IfoodOperationStateCodes
{
    public static IfoodOperationState FromCode(string? code) => code switch
    {
        "queued" => IfoodOperationState.Queued,
        "processing" => IfoodOperationState.Processing,
        "uncertain" => IfoodOperationState.Uncertain,
        "succeeded" => IfoodOperationState.Succeeded,
        "rejected" => IfoodOperationState.Rejected,
        "review" => IfoodOperationState.Review,
        _ => IfoodOperationState.Unknown
    };

    public static string ToCode(this IfoodOperationState state) => state switch
    {
        IfoodOperationState.Queued => "queued",
        IfoodOperationState.Processing => "processing",
        IfoodOperationState.Uncertain => "uncertain",
        IfoodOperationState.Succeeded => "succeeded",
        IfoodOperationState.Rejected => "rejected",
        IfoodOperationState.Review => "review",
        _ => throw new ArgumentOutOfRangeException(nameof(state), "Estado desconhecido não pode ser persistido como sucesso ou pendência.")
    };
}
