using System.Data;
using Dapper;
using Npgsql;

namespace APIBack.Social.Nucleo;

/// <summary>
/// Permissões do Zippy Social (README §5). Entram no catálogo (<c>EstablishmentPermissions.Catalog</c>) mesmo as que ainda não têm
/// rota (<c>publicar</c>, <c>gerenciar_ia</c>). As outras do README chegam com os prompts delas.
/// </summary>
public static class SocialPermissoes
{
    /// <summary>Nome do módulo na UI e nas permissões (<c>SOCIAL</c> no banco).</summary>
    public const string Modulo = "Social";

    public const string Visualizar = "visualizar", Editar = "editar", Publicar = "publicar", GerenciarIa = "gerenciar_ia";

    public static readonly string[] Acoes = { Visualizar, Editar, Publicar, GerenciarIa };
}

public sealed class SocialException(string codigo, string mensagem, int status) : Exception(mensagem)
{
    public string Codigo { get; } = codigo;
    public int Status { get; } = status;
    public object? Detalhes { get; init; }

    public static SocialException Validacao(string mensagem) => new("VALIDACAO", mensagem, 400);
    public static SocialException NaoEncontrado(string mensagem) => new("NAO_ENCONTRADO", mensagem, 404);
    public static SocialException SemPermissao(string mensagem = "Você não pode fazer isso.") => new("SEM_PERMISSAO", mensagem, 403);
    public static SocialException MuitoGrande(string mensagem) => new("MUITO_GRANDE", mensagem, 413);
    public static SocialException SemArmazenamento() =>
        new("ARMAZENAMENTO_INDISPONIVEL", "O armazenamento de arquivos do Social ainda não foi configurado.", 503);
}

/// <summary>
/// Toda leitura e escrita das tabelas <c>social_*</c> passa por aqui: abre transação e marca a loja (<c>zippy.loja</c>) para a
/// row-level security da migration 20261208_01. A consulta ainda filtra por loja; a política é a segunda trava.
/// </summary>
public static class SocialBanco
{
    public static Task<T> NaLoja<T>(NpgsqlDataSource fonte, Guid loja, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> fazer) =>
        Rodar(fonte, "zippy.loja", loja.ToString(), ct, fazer);

    /// <summary>Serviços sem usuário (limpeza). Vê todas as lojas.</summary>
    public static Task<T> ComoSistema<T>(NpgsqlDataSource fonte, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> fazer) =>
        Rodar(fonte, "zippy.sistema", "sim", ct, fazer);

    private static async Task<T> Rodar<T>(NpgsqlDataSource fonte, string chave, string valor, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> fazer)
    {
        await using var c = await fonte.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await c.ExecuteAsync(new CommandDefinition("SELECT set_config(@chave, @valor, true)", new { chave, valor }, tx, cancellationToken: ct));
        var resultado = await fazer(c, tx);
        await tx.CommitAsync(ct);
        return resultado;
    }
}
