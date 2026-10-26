using System;
using System.Collections.Generic;
using APIBack.Payments.Conexao;

namespace APIBack.Payments.Gestao
{
    // DTOs da Gestao de Pagamentos (CONTRATOS.md §8.1). Nenhum carrega token ou segredo: so "temX" e ids mascarados.

    public sealed class AplicacaoDto
    {
        public string Ambiente { get; set; } = string.Empty;
        public string? ClientId { get; set; }
        public string? PublicKey { get; set; }
        public string? RedirectUri { get; set; }
        public bool TemClientSecret { get; set; }
        public bool TemWebhookSecret { get; set; }
        public bool Ativo { get; set; }
        /// <summary>banco | configuracao (bootstrap do servidor, so leitura) | nenhuma.</summary>
        public string Origem { get; set; } = "nenhuma";
        public DateTime? AtualizadoEm { get; set; }
    }

    public sealed class ContaZippyDto
    {
        public string Ambiente { get; set; } = string.Empty;
        public string? MpUserIdMascarado { get; set; }
        public string? PublicKey { get; set; }
        public string Estado { get; set; } = "nao_configurada";
        public bool TemToken { get; set; }
        public string? UltimoErro { get; set; }
        public DateTime? ValidadaEm { get; set; }
        public DateTime? AtualizadoEm { get; set; }
    }

    public sealed class AmbienteEstadoDto
    {
        public bool Habilitado { get; set; }
        public bool LojasPodemConectar { get; set; }
        public DateTime? HabilitadoEm { get; set; }
    }

    public sealed class VerificacaoDto
    {
        public Guid Id { get; set; }
        public string Ambiente { get; set; } = string.Empty;
        public bool Aprovada { get; set; }
        public List<EtapaVerificacao> Etapas { get; set; } = new();
        public DateTime CriadoEm { get; set; }
        /// <summary>Credenciais mudaram depois desta verificacao: precisa verificar de novo.</summary>
        public bool Desatualizada { get; set; }
    }

    public sealed class WebhookSaudeDto
    {
        public DateTime? UltimoRecebidoEm { get; set; }
        public int Recebidos24h { get; set; }
        public int Falhas24h { get; set; }
        public int Pendentes { get; set; }
    }

    /// <summary>Uma etapa do assistente de implantacao. Status: ok | pendente | erro | bloqueada (depende de uma anterior).</summary>
    public sealed class EtapaImplantacaoDto
    {
        public int Numero { get; set; }
        public string Chave { get; set; } = string.Empty;
        public string Titulo { get; set; } = string.Empty;
        public string Status { get; set; } = "pendente";
        /// <summary>O que falta, em linguagem simples. Null quando ok.</summary>
        public string? Falta { get; set; }
    }

    public sealed class ConfiguracaoDto
    {
        public string AmbienteAtivo { get; set; } = string.Empty;
        public string Ambiente { get; set; } = string.Empty;
        public bool ChaveMestraConfigurada { get; set; }
        public AplicacaoDto Aplicacao { get; set; } = new();
        public ContaZippyDto ContaZippy { get; set; } = new();
        public AmbienteEstadoDto AmbienteEstado { get; set; } = new();
        public string? UrlCallback { get; set; }
        public string? UrlWebhook { get; set; }
        public VerificacaoDto? UltimaVerificacao { get; set; }
        public WebhookSaudeDto Webhook { get; set; } = new();
        public int LojasConectadas { get; set; }
        public List<EtapaImplantacaoDto> Etapas { get; set; } = new();
        /// <summary>Todas as 8 etapas ok: pronto para receber.</summary>
        public bool Pronto { get; set; }
    }

    public sealed class SalvarAplicacaoRequest
    {
        public string? Ambiente { get; set; }
        public string? ClientId { get; set; }
        /// <summary>Vazio = manter o atual.</summary>
        public string? ClientSecret { get; set; }
        public string? PublicKey { get; set; }
        /// <summary>Vazio = manter o atual.</summary>
        public string? WebhookSecret { get; set; }
        public string? RedirectUri { get; set; }
    }

    public sealed class SalvarContaZippyRequest
    {
        public string? Ambiente { get; set; }
        public string? AccessToken { get; set; }
        public string? PublicKey { get; set; }
    }

    public sealed class AmbienteRequest
    {
        public string? Ambiente { get; set; }
    }

    public sealed class AtualizarAmbienteRequest
    {
        public string? Ambiente { get; set; }
        public bool Habilitado { get; set; }
        public bool LojasPodemConectar { get; set; }
    }

    public sealed record TesteContaZippyDto(bool Ok, string? MpUserIdMascarado);

    public sealed class ConexaoLojaGestaoDto
    {
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Estado { get; set; } = "nao_conectada";
        public string? MpUserIdMascarado { get; set; }
        public DateTime? TokenExpiraEm { get; set; }
        public DateTime? ConectadoEm { get; set; }
        public string? UltimoErro { get; set; }
        public bool PixAtivo { get; set; }
        public bool CartaoAtivo { get; set; }
    }

    public sealed class Pagina<T>
    {
        public List<T> Itens { get; set; } = new();
        public int Total { get; set; }
    }

    public sealed class TransacaoGestaoDto
    {
        public Guid Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string? LojaNome { get; set; }
        public string Origem { get; set; } = string.Empty;
        public string ContaTipo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public decimal ValorPago { get; set; }
        public decimal ValorEstornado { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public string? Metodo { get; set; }
        public string? StatusPagamento { get; set; }
        public string? StatusDetalhe { get; set; }
        public long? MpPaymentId { get; set; }
        public DateTime? PagoEm { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    public sealed class TransacoesGestaoDto
    {
        public string Conta { get; set; } = "loja";
        public List<TransacaoGestaoDto> Itens { get; set; } = new();
        public int Total { get; set; }
        public decimal SomaPaga { get; set; }
        public decimal SomaEstornada { get; set; }
    }

    public sealed class ReembolsoGestaoDto
    {
        public Guid Id { get; set; }
        public Guid IntentId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string? LojaNome { get; set; }
        public string ContaTipo { get; set; } = string.Empty;
        public string Origem { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public string Status { get; set; } = string.Empty;
        public string OrigemEstorno { get; set; } = string.Empty;
        public string? Motivo { get; set; }
        public string? ErroMensagem { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime? ConcluidoEm { get; set; }
    }

    public sealed class WebhookGestaoDto
    {
        public Guid Id { get; set; }
        public string Ambiente { get; set; } = string.Empty;
        public string Topico { get; set; } = string.Empty;
        public string? Acao { get; set; }
        public string DataId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Tentativas { get; set; }
        public string? UltimoErro { get; set; }
        public DateTime RecebidoEm { get; set; }
        public DateTime? ProcessadoEm { get; set; }
    }

    public sealed class WebhooksGestaoDto
    {
        public List<WebhookGestaoDto> Itens { get; set; } = new();
        public int Total { get; set; }
        public WebhookSaudeDto Saude { get; set; } = new();
    }

    public sealed class DisputaGestaoDto
    {
        public Guid Id { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal? Valor { get; set; }
        public DateTime? PrazoRespostaEm { get; set; }
        public Guid IntentId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string? LojaNome { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    public sealed class AuditoriaGestaoDto
    {
        public long Id { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string Entidade { get; set; } = string.Empty;
        public string EntidadeId { get; set; } = string.Empty;
        public string Acao { get; set; } = string.Empty;
        public string AtorTipo { get; set; } = string.Empty;
        public int? AtorId { get; set; }
        public string? AtorNome { get; set; }
        public string? Dados { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    public sealed class PoliticasDto
    {
        public int ToleranciaDias { get; set; } = 7;
        public int[] AvisosAntesDias { get; set; } = { 3, 1 };
        public int[] AvisosDepoisDias { get; set; } = { 1, 3, 6 };
        public decimal AlcadaEstornoGerente { get; set; } = 200;
        public decimal AlcadaEstornoConfirmacao { get; set; } = 1000;
        public int AlertaFalhasWebhook { get; set; } = 5;
        public DateTime? AtualizadoEm { get; set; }
    }

    public sealed class ReconciliacaoDto
    {
        public int PagamentosAbertosAntes { get; set; }
        public int PagamentosAbertosDepois { get; set; }
        public int WebhooksPendentesAntes { get; set; }
        public int WebhooksPendentesDepois { get; set; }
        public DateTime ExecutadaEm { get; set; }
    }

    public sealed class ResumoGestaoDto
    {
        public string Ambiente { get; set; } = string.Empty;
        public string Mes { get; set; } = string.Empty;
        /// <summary>Dinheiro da Zippy (mensalidades e extras) confirmado pelo MP no mes.</summary>
        public decimal RecebidoZippyMes { get; set; }
        public decimal EstornadoZippyMes { get; set; }
        public int CobrancasZippyAbertas { get; set; }
        public decimal ValorZippyAberto { get; set; }
        /// <summary>Vendas das lojas no mes (dinheiro DELAS, nunca somado ao da Zippy).</summary>
        public decimal VendasLojasMes { get; set; }
        public int TransacoesLojasMes { get; set; }
        public int LojasConectadas { get; set; }
        public int LojasComProblema { get; set; }
        public int WebhooksComFalha { get; set; }
        public int DisputasAbertas { get; set; }
        public int ReembolsosPendentes { get; set; }
        public bool Pronto { get; set; }
        public bool AmbienteHabilitado { get; set; }
    }
}
