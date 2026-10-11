namespace APIBack.Service;

// Disponibilidade é uma escolha do motoboy. Heartbeat e GPS medem atualização,
// sem encerrar o turno quando o sistema suspende o aplicativo.
public static class OperationalAvailability
{
    public static readonly DateTimeOffset UntilExplicitEnd = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
}
