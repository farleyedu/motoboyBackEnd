namespace APIBack.Service;

// Credenciais de login duram até revogação explícita. A presença do turno usa
// a janela própria de DeliveryTrackingOptions e pode vencer sem encerrar a fila.
public static class OperationalAvailability
{
    public static readonly DateTimeOffset UntilExplicitEnd = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
}
