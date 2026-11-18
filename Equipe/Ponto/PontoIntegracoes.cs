using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Equipe;
using APIBack.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace APIBack.Equipe.Ponto;

/// <summary>O que o ponto pede da Equipe: o fuso da loja e preencher o turno pelo caminho de sempre (livro, histórico, revisão).</summary>
public interface IPontoEquipe
{
    Task<TimeZoneInfo> Fuso(Guid loja, CancellationToken ct);
    Task PreencherTurno(Guid loja, int usuario, Guid pessoaId, DateOnly dia, string entrada, string saida, string? motivo, CancellationToken ct);
}

public sealed class PontoEquipe(IEquipeService equipe, IAcontecimentosService livro) : IPontoEquipe
{
    public Task<TimeZoneInfo> Fuso(Guid loja, CancellationToken ct) => livro.Fuso(loja, ct);

    /// <summary>
    /// Marca o turno como realizado com o horário real. Primeiro como turno da escala; se a pessoa não tem escala nesse dia, vira
    /// jornada avulsa (mesma regra de pagamento da pessoa). O dinheiro segue a Equipe: este caminho não calcula nada sozinho.
    /// </summary>
    public async Task PreencherTurno(Guid loja, int usuario, Guid pessoaId, DateOnly dia, string entrada, string saida, string? motivo,
        CancellationToken ct)
    {
        var req = new MudarTurnoRequest(pessoaId, dia, StatusTurno.Realizado, entrada, saida, Motivo: motivo);
        try
        {
            await equipe.MudarTurno(loja, usuario, req, ct);
        }
        catch (GestaoException ex) when (ex.Message.Contains("não tem turno na escala", StringComparison.Ordinal))
        {
            await equipe.MudarTurno(loja, usuario, req with { Avulso = true }, ct);
        }
    }
}

/// <summary>Evento <c>equipe.ponto.mudou</c> {pessoaId} para o grupo da loja no DeliveryHub. Sem dado sensível: quem ouve relê.</summary>
public interface IPontoTempoReal
{
    Task Mudou(Guid loja, Guid pessoaId);
}

public sealed class PontoTempoReal(IHubContext<DeliveryHub> hub, ILogger<PontoTempoReal> log) : IPontoTempoReal
{
    public const string Evento = "equipe.ponto.mudou";

    public async Task Mudou(Guid loja, Guid pessoaId)
    {
        try
        {
            await hub.Clients.Group(DeliveryRealtimeEvents.EstablishmentGroup(loja)).SendAsync(Evento, new { pessoaId });
        }
        catch (Exception ex)
        {
            // O ponto já foi gravado; quem perdeu o aviso relê pelo polling.
            log.LogWarning(ex, "Falha ao avisar ponto da pessoa {PessoaId} na loja {Loja}", pessoaId, loja);
        }
    }
}
