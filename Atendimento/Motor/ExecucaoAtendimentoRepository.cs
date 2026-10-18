using System.Text.Json;
using Dapper;
using Npgsql;

namespace APIBack.Atendimento.Motor;

public sealed record ExecucaoSalva(ResultadoMotor Decisao, bool Concluida);
public sealed record AcaoPreparada(bool Executar, Guid MensagemId);

public interface IExecucaoAtendimentoRepository
{
    Task<ExecucaoSalva?> ObterAsync(string chave);
    Task SalvarAsync(string chave, Guid conversa, ResultadoMotor decisao);
    Task<AcaoPreparada> PrepararAcaoAsync(string chave, int indice, bool encaminhamento);
    Task ConcluirAcaoAsync(string chave, int indice);
    Task ConcluirAsync(string chave);
}

/// <summary>Checkpoint persistente. Envio externo sem confirmacao nunca e repetido cegamente.</summary>
public sealed class ExecucaoAtendimentoRepository(NpgsqlDataSource source) : IExecucaoAtendimentoRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<ExecucaoSalva?> ObterAsync(string chave)
    {
        await using var cx = await source.OpenConnectionAsync();
        var row = await cx.QuerySingleOrDefaultAsync<(string? Decisao, bool Concluida)>(
            "SELECT decisao::text AS Decisao, concluida AS Concluida FROM atendimento_execucao WHERE wa_chave=@Chave;", new { Chave = chave });
        return row.Decisao == null ? null : new(JsonSerializer.Deserialize<ResultadoMotor>(row.Decisao, Json)!, row.Concluida);
    }
    public async Task SalvarAsync(string chave, Guid conversa, ResultadoMotor decisao)
    {
        await using var cx = await source.OpenConnectionAsync();
        await cx.ExecuteAsync(@"INSERT INTO atendimento_execucao(wa_chave,conversa_id,decisao)
VALUES (@Chave,@Conversa,CAST(@Decisao AS jsonb)) ON CONFLICT DO NOTHING;",
            new { Chave = chave, Conversa = conversa, Decisao = JsonSerializer.Serialize(decisao, Json) });
    }
    public async Task<AcaoPreparada> PrepararAcaoAsync(string chave, int indice, bool encaminhamento)
    {
        await using var cx = await source.OpenConnectionAsync();
        var id = Guid.NewGuid();
        var nova = await cx.ExecuteAsync(@"INSERT INTO atendimento_execucao_acao(wa_chave,indice,mensagem_id)
VALUES (@Chave,@Indice,@Id) ON CONFLICT DO NOTHING;", new { Chave = chave, Indice = indice, Id = id });
        if (nova > 0) return new(true, id);
        var atual = await cx.QuerySingleAsync<(Guid Id, bool Concluida)>(@"
SELECT mensagem_id AS Id, concluida AS Concluida FROM atendimento_execucao_acao WHERE wa_chave=@Chave AND indice=@Indice;",
            new { Chave = chave, Indice = indice });
        if (atual.Concluida) return new(false, atual.Id);
        if (encaminhamento) return new(true, atual.Id); // Operacao idempotente; pode terminar o encaminhamento.
        var mensagem = await cx.QuerySingleOrDefaultAsync<(Guid? Id, string? Status, string? Provedor)>(
            "SELECT id AS Id, status::text AS Status, id_provedor AS Provedor FROM mensagens WHERE id=@Id;", new { atual.Id });
        if (mensagem.Id == null) return new(true, atual.Id); // Caiu antes de persistir/enviar.
        if (mensagem.Status is not ("enviada" or "entregue" or "lida" or "falhou") && (string.IsNullOrWhiteSpace(mensagem.Provedor) || mensagem.Provedor.StartsWith("local-", StringComparison.Ordinal)))
        {
            await cx.ExecuteAsync(@"UPDATE mensagens SET status='falhou'::status_mensagem_enum,
codigo_erro='envio_incerto', mensagem_erro='Processamento interrompido. Confira a entrega antes de reenviar.' WHERE id=@Id;", new { atual.Id });
        }
        return new(false, atual.Id);
    }
    public async Task ConcluirAcaoAsync(string chave, int indice)
    {
        await using var cx = await source.OpenConnectionAsync();
        await cx.ExecuteAsync("UPDATE atendimento_execucao_acao SET concluida=TRUE WHERE wa_chave=@Chave AND indice=@Indice;", new { Chave = chave, Indice = indice });
    }
    public async Task ConcluirAsync(string chave)
    {
        await using var cx = await source.OpenConnectionAsync();
        await cx.ExecuteAsync("UPDATE atendimento_execucao SET concluida=TRUE WHERE wa_chave=@Chave;", new { Chave = chave });
    }
}
