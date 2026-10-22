using APIBack.Integrations.Ifood.Config;

namespace APIBack.Integrations.Ifood.Connections;

/// <param name="State">disconnected, awaiting_authorization, not_authorized_at_ifood, polling_forbidden, polling_error, polling_stale, unverified, operational.</param>
/// <param name="Actions">Acoes de correcao que o painel oferece: recheck, disconnect, authorize, transfer, remove.</param>
public sealed record IfoodConnectionHealthView(string State, string Message, string[] Actions);

/// <summary>
/// Cadastrada, autorizada e operacional sao coisas diferentes: o status gravado na Zippy ("authorized") so diz que a Gestao
/// aceitou o vinculo. Quem prova que funciona e o iFood: a conferencia do merchant e, principalmente, o polling dele.
/// </summary>
public static class IfoodConnectionState
{
    public static readonly TimeSpan PollFresh = TimeSpan.FromMinutes(3);

    public static IfoodConnectionHealthView Describe(IfoodConnectionRow row, DateTimeOffset now)
    {
        if (row.RevokedAtUtc is not null)
            return new("disconnected", "Conexão desfeita. O histórico de pedidos e eventos foi mantido.", new[] { "remove" });
        if (row.Status != "authorized")
            return new("awaiting_authorization", "O ID foi informado e aguarda a Zippy autorizar a loja no iFood.", new[] { "authorize", "recheck", "disconnect", "remove" });
        if (row.PollState == "forbidden")
            return new("polling_forbidden", "O iFood recusou a leitura de pedidos desta loja (403). Confirme a autorização da Zippy no Portal do Parceiro; a Zippy tenta de novo sozinha." + RetryText(row, now),
                new[] { "recheck", "transfer", "disconnect" });
        if (row.RemoteCheckCode == "NOT_AUTHORIZED")
            return new("not_authorized_at_ifood", "O iFood não reconhece esta loja como autorizada para a Zippy. Confira o ID e a autorização no Portal do Parceiro.",
                new[] { "recheck", "transfer", "disconnect" });
        if (row.PollState == "error")
            return new("polling_error", "A leitura de pedidos falhou (" + (row.PollErrorCode ?? "erro") + "). A Zippy tenta de novo sozinha." + RetryText(row, now),
                new[] { "recheck", "disconnect" });
        if (row.PollState == "ok" && row.LastPollOkAtUtc is { } ok)
            return now - ok <= PollFresh
                ? new("operational", "Recebendo pedidos do iFood normalmente.", new[] { "recheck", "transfer", "disconnect" })
                : new("polling_stale", "Sem resposta do iFood há mais de " + (int)PollFresh.TotalMinutes + " minutos. Confira se a integração e o polling estão ligados.", new[] { "recheck", "disconnect" });
        return new("unverified", "Autorizada na Zippy, ainda sem confirmação do iFood. Use “Conferir agora”.", new[] { "recheck", "disconnect" });
    }

    private static string RetryText(IfoodConnectionRow row, DateTimeOffset now)
        => row.PollBlockedUntilUtc is { } until && until > now ? $" Próxima tentativa em {(int)Math.Ceiling((until - now).TotalMinutes)} min." : "";
}
