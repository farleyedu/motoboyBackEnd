using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIBack.Atendimento
{
    /// <param name="EstabelecimentoId">Destino: a loja que recebe as mensagens deste numero agora (obrigatorio).</param>
    /// <param name="Finalidade"><see cref="FinalidadeTeste"/>; omitido = geral. "gestao" so com a loja de Gestao escolhida pelo operador.</param>
    /// <param name="Principal">Torna este o numero principal (o do contrato antigo e do fallback).</param>
    public sealed record CanalTesteRequest(
        string? PhoneNumberId, string? Numero, string? Token, string? Nome, Guid EstabelecimentoId, string? Finalidade, bool? Principal);

    /// <summary>Campos nulos nao mudam. Trocar <c>EstabelecimentoId</c> move o numero e exige verificar de novo.</summary>
    public sealed record CanalTesteAtualizacao(
        string? Nome, string? Status, Guid? EstabelecimentoId, string? Finalidade, bool? Principal, string? Token);

    public sealed record CanalTesteView(
        Guid Id, Guid EstabelecimentoId, string PhoneNumberId, string Numero, string? Nome, string Status, bool Principal, string Finalidade,
        bool TemToken, DateTime? VerificadoEm, DateTime? UltimoRecebimentoEm, DateTime? UltimoEnvioOkEm, string? UltimoErro, DateTime? UltimoErroEm,
        IReadOnlyList<string> Servicos, string Webhook);

    /// <param name="Credenciais">Ha token para falar com a Meta (do numero, ou o global da Zippy).</param>
    /// <param name="Webhook">Numeros de teste recebem pelo webhook global da Zippy (/wa/webhook); nao tem URL propria.</param>
    public sealed record CanalTesteVerificacao(
        bool Ok, string Mensagem, string? NumeroNaMeta, string? NomeVerificado, string? Codigo, string? Campo,
        bool Credenciais, string Webhook, CanalTesteView Canal);

    /// <summary>Alteracao de um numero de teste aplicada numa unica transacao (destino, servicos, principal e auditoria juntos).</summary>
    public sealed record AlteracaoTeste(
        Guid CanalId, string Acao, int UsuarioId, string? Nome, string? Status, Guid? Destino, IReadOnlyCollection<string>? Servicos,
        string? Finalidade, bool? Principal, string? TokenCifrado, string? DetalheJson);

    public interface ICanaisTesteService
    {
        /// <exception cref="UnauthorizedAccessException">403: so a Gestao.</exception>
        Task<IReadOnlyList<CanalTesteView>> ListarTesteAsync(AtendimentoAtor ator);
        /// <exception cref="ArgumentException">400: corpo ou campo malformado.</exception>
        /// <exception cref="KeyNotFoundException">404: o destino nao existe.</exception>
        /// <exception cref="CanalConflitoException">409: numero ou Phone Number ID ja cadastrado, ou numero de cliente.</exception>
        /// <exception cref="Service.RequestValidationException">422: regra de negocio nao atendida.</exception>
        Task<CanalTesteView> CriarTesteAsync(AtendimentoAtor ator, CanalTesteRequest request);
        Task<CanalTesteView> AtualizarTesteAsync(AtendimentoAtor ator, Guid id, CanalTesteAtualizacao request);
        /// <summary>Consulta a Meta de verdade (credenciais) e informa o webhook. Marca o numero como ativo se a Meta confirmar.</summary>
        Task<CanalTesteVerificacao> VerificarTesteAsync(AtendimentoAtor ator, Guid id);
    }
}
