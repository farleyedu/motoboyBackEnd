using System;
using System.Collections.Generic;
using System.Text.Json;
using APIBack.Payments.MercadoPago;

namespace APIBack.Payments.Chat
{
    /// <summary>Codigos proprios da cobranca do chat (alem de PagamentoCodigos). Tabela em docs/pagamentos/CONTRATOS.md §8.5.</summary>
    public static class CobrancaChatCodigos
    {
        public const string Desativada = "PAG_COBRANCA_CHAT_DESATIVADA";            // 422 loja desligou a cobranca pelo chat
        public const string AvulsaDesativada = "PAG_COBRANCA_AVULSA_DESATIVADA";     // 422 loja so aceita cobranca de pedido
        public const string ValorDivergente = "PAG_VALOR_DIVERGENTE";                // 422 valor digitado != saldo do pedido (details.saldo)
        public const string PedidoDeOutroCliente = "PAG_PEDIDO_DE_OUTRO_CLIENTE";    // 422 pedido nao e do cliente da conversa
        public const string JanelaFechada = "PAG_JANELA_FECHADA";                    // 422 fora das 24 h e sem modelo aprovado
        public const string WhatsappNaoConfigurado = "PAG_WHATSAPP_NAO_CONFIGURADO"; // 422 sem numero/telefone para enviar
        public const string WhatsappErro = "PAG_WHATSAPP_ERRO";                      // 502 a Meta recusou ou nao respondeu
        public const string EnvioEmAndamento = "PAG_ENVIO_EM_ANDAMENTO";             // 409 outro envio da mesma cobranca esta saindo
        public const string AprovacaoPropria = "PAG_APROVACAO_PROPRIA";              // 403 quem pediu nao aprova o proprio pedido
        public const string ExigeAprovacao = "PAG_EXIGE_APROVACAO";                  // 403 acao reservada ao gerente: use /solicitar-aprovacao
    }

    public static class AcoesAprovacao
    {
        public const string Emitir = "emitir";
        public const string Reembolsar = "reembolsar";
        public const string TrocarVinculo = "trocar_vinculo";

        public static bool Valida(string? a) => a is Emitir or Reembolsar or TrocarVinculo;
    }

    public static class StatusAprovacao
    {
        public const string Pendente = "pendente";
        public const string Aprovada = "aprovada";
        public const string Rejeitada = "rejeitada";
        public const string Cancelada = "cancelada";
        public const string Falhou = "falhou";
    }

    public static class TiposCobranca
    {
        public const string Avulsa = "avulsa";
        public const string Vinculada = "vinculada";
    }

    public static class TiposTarefa
    {
        public const string ConfirmarCliente = "confirmar_cliente";
        public const string AvancarPedido = "avancar_pedido";
        public const string CancelarSucessora = "cancelar_sucessora";
    }

    /// <summary>Permissoes (catalogo de EstablishmentPermissions, entram pelos [RequirePermission] do controller).</summary>
    public static class PermissoesCobranca
    {
        public const string Modulo = "WhatsApp";
        /// <summary>Atendente habilitado pelo gerente: cria, envia, cancela e reemite.</summary>
        public const string Cobrar = "cobrar";
        /// <summary>Gerente: aprova acima do limite, reembolsos e troca de vinculo; nao precisa de aprovacao.</summary>
        public const string Aprovar = "aprovar_cobranca";
    }

    // ---- entrada -------------------------------------------------------------------------------------------------------------

    public sealed class CriarCobrancaChatRequest
    {
        public string? Descricao { get; set; }
        /// <summary>Avulsa: obrigatorio. Vinculada: opcional e, se vier, tem de ser igual ao saldo calculado no servidor.</summary>
        public decimal? Valor { get; set; }
        /// <summary>"avulsa" | "vinculada" (opcional: deduzido de pedidoId/prePedidoId).</summary>
        public string? Tipo { get; set; }
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public int? ValidadeMinutos { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    public sealed class EnviarCobrancaChatRequest
    {
        /// <summary>"link" (padrao) | "copia_e_cola" | "ambos". Fora da janela vale o modelo da Meta.</summary>
        public string? Formato { get; set; }
    }

    public sealed class CancelarCobrancaChatRequest
    {
        public string? Motivo { get; set; }
    }

    public sealed class ReemitirCobrancaChatRequest
    {
        public int? ValidadeMinutos { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    public sealed class ReembolsarCobrancaChatRequest
    {
        public decimal? Valor { get; set; }
        public string? Motivo { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    public sealed class SolicitarAprovacaoRequest
    {
        /// <summary>"emitir" | "reembolsar" | "trocar_vinculo".</summary>
        public string? Acao { get; set; }
        /// <summary>Reembolsar: valor (null = saldo pago).</summary>
        public decimal? Valor { get; set; }
        /// <summary>Trocar vinculo: o novo pedido (ou pre-pedido) do mesmo cliente.</summary>
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public string? Motivo { get; set; }
        public string? IdempotencyKey { get; set; }
    }

    public sealed class DecidirAprovacaoRequest
    {
        public string? Motivo { get; set; }
    }

    public sealed class CobrancaChatConfigRequest
    {
        public bool? Habilitada { get; set; }
        public bool? PermitirAvulsa { get; set; }
        /// <summary>Null = sem limite. Para limpar o limite mande <see cref="SemLimite"/> = true.</summary>
        public decimal? LimiteSemAprovacao { get; set; }
        public bool? SemLimite { get; set; }
        public string? TemplateNome { get; set; }
        public string? TemplateIdioma { get; set; }
        public bool? ConfirmarPagamentoCliente { get; set; }
    }

    /// <summary>Quem esta agindo (vem do token). <see cref="PodeAprovar"/> = WhatsApp:aprovar_cobranca ou super admin.</summary>
    public sealed record AtorCobranca(int UsuarioId, string? Nome, bool PodeAprovar, bool PodeReembolsar);

    // ---- saida (DTOs do contrato) --------------------------------------------------------------------------------------------

    public sealed class UsuarioResumoDto
    {
        public int Id { get; set; }
        public string? Nome { get; set; }
    }

    public sealed class CobrancaPixDto
    {
        public string? CopiaECola { get; set; }
        public string? QrCodeBase64 { get; set; }
        /// <summary>Link da pagina Pix publica (o mesmo em todo reenvio). So na resposta de POST, /reemitir e /enviar.</summary>
        public string? LinkPublico { get; set; }
    }

    public sealed class CobrancaAprovacaoDto
    {
        public Guid Id { get; set; }
        public Guid CobrancaId { get; set; }
        public Guid ConversaId { get; set; }
        public string Acao { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal? Valor { get; set; }
        /// <summary>Valor da cobranca (para a fila do gerente).</summary>
        public decimal ValorCobranca { get; set; }
        public string DescricaoCobranca { get; set; } = string.Empty;
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public string? Motivo { get; set; }
        public UsuarioResumoDto SolicitadoPor { get; set; } = new();
        public UsuarioResumoDto? DecididoPor { get; set; }
        public DateTime? DecididoEm { get; set; }
        public string? MotivoDecisao { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    public sealed class CobrancaChatDto
    {
        public Guid Id { get; set; }
        public Guid ConversaId { get; set; }
        public Guid? IntentId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int Versao { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public string Moeda { get; set; } = "BRL";
        public string Tipo { get; set; } = TiposCobranca.Avulsa;
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public int ValidadeMinutos { get; set; }
        public DateTime? ExpiraEm { get; set; }
        public DateTime? EnviadoEm { get; set; }
        /// <summary>Mensagem do chat que levou o link (para o front casar o balao com a mensagem enviada).</summary>
        public Guid? IdMensagem { get; set; }
        public DateTime? PagoEm { get; set; }
        public DateTime? CanceladoEm { get; set; }
        public UsuarioResumoDto? CanceladoPor { get; set; }
        /// <summary>Vinculada: quanto o pedido ainda deve (total - recebido online), calculado no servidor. Avulsa: null.</summary>
        public decimal? SaldoPedido { get; set; }
        public string? Motivo { get; set; }
        public Guid? ReemitidaDe { get; set; }
        /// <summary>Cobranca que substituiu esta (reemissao), quando houver.</summary>
        public Guid? ReemitidaPor { get; set; }
        public UsuarioResumoDto Autor { get; set; } = new();
        /// <summary>So enquanto aguardando|enviado.</summary>
        public CobrancaPixDto? Pix { get; set; }
        public decimal ValorEstornado { get; set; }
        /// <summary>Aprovacao pendente desta cobranca (emitir/reembolsar/trocar_vinculo), se houver.</summary>
        public CobrancaAprovacaoDto? AprovacaoPendente { get; set; }
        public bool PodeEnviar { get; set; }
        public bool PodeCancelar { get; set; }
        public bool PodeReemitir { get; set; }
        public bool PodeReembolsar { get; set; }
        public bool PodeSolicitarAprovacao { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime AtualizadoEm { get; set; }
    }

    public sealed class CobrancaChatListaDto
    {
        public IReadOnlyList<CobrancaChatDto> Itens { get; set; } = Array.Empty<CobrancaChatDto>();
    }

    public sealed class CobrancaHistoricoItemDto
    {
        public string? StatusAnterior { get; set; }
        public string StatusNovo { get; set; } = string.Empty;
        public string AtorTipo { get; set; } = string.Empty;
        public UsuarioResumoDto? Usuario { get; set; }
        public JsonElement? Detalhe { get; set; }
        public DateTime CriadoEm { get; set; }
    }

    public sealed class CobrancaHistoricoDto
    {
        public IReadOnlyList<CobrancaHistoricoItemDto> Itens { get; set; } = Array.Empty<CobrancaHistoricoItemDto>();
    }

    public sealed class CobrancaAprovacaoListaDto
    {
        public IReadOnlyList<CobrancaAprovacaoDto> Itens { get; set; } = Array.Empty<CobrancaAprovacaoDto>();
    }

    public sealed class DecisaoAprovacaoDto
    {
        public CobrancaAprovacaoDto Aprovacao { get; set; } = new();
        public CobrancaChatDto Cobranca { get; set; } = new();
    }

    public sealed class CobrancaChatConfigDto
    {
        public bool Habilitada { get; set; } = true;
        public bool PermitirAvulsa { get; set; } = true;
        public decimal? LimiteSemAprovacao { get; set; }
        public string? TemplateNome { get; set; }
        public string TemplateIdioma { get; set; } = "pt_BR";
        public bool ConfirmarPagamentoCliente { get; set; } = true;
        /// <summary>Do usuario logado: pode aprovar (gerente) e pode reembolsar sem aprovacao (Financeiro:cancelar).</summary>
        public bool PodeAprovar { get; set; }
        public bool PodeReembolsar { get; set; }
        /// <summary>Pix ligado e conta MP conectada na loja (sem isso o POST devolve 422).</summary>
        public bool PixDisponivel { get; set; }
    }

    // ---- linhas do banco -----------------------------------------------------------------------------------------------------

    public sealed class CobrancaChatLinha
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid ConversaId { get; set; }
        public Guid? IdIntent { get; set; }
        public int AutorUsuarioId { get; set; }
        public string? AutorNome { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public decimal Valor { get; set; }
        public string Moeda { get; set; } = "BRL";
        public string Tipo { get; set; } = TiposCobranca.Avulsa;
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public Guid? ClienteId { get; set; }
        public string Status { get; set; } = StatusCobranca.Criando;
        public int Versao { get; set; }
        public int ValidadeMinutos { get; set; }
        public DateTime? ExpiraEm { get; set; }
        public DateTime? EnviadoEm { get; set; }
        public Guid? IdMensagem { get; set; }
        public DateTime? PagoEm { get; set; }
        public DateTime? CanceladoEm { get; set; }
        public int? CanceladoPor { get; set; }
        public string? CanceladoPorNome { get; set; }
        public string? Motivo { get; set; }
        public Guid? ReemitidaDe { get; set; }
        public Guid? ReemitidaPor { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string? LinkTokenCifrado { get; set; }
        public decimal ValorEstornado { get; set; }
        public string? StatusIntent { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public CobrancaChatRegistro Registro() => new()
        {
            Id = Id, IdEstabelecimento = IdEstabelecimento, ConversaId = ConversaId, IdIntent = IdIntent, PedidoId = PedidoId, Status = Status, Versao = Versao
        };
    }

    public sealed class CobrancaAprovacaoLinha
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid IdCobranca { get; set; }
        public Guid ConversaId { get; set; }
        public string Acao { get; set; } = string.Empty;
        public string Status { get; set; } = StatusAprovacao.Pendente;
        public decimal? Valor { get; set; }
        public decimal ValorCobranca { get; set; }
        public string DescricaoCobranca { get; set; } = string.Empty;
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public string? Motivo { get; set; }
        public int SolicitadoPor { get; set; }
        public string? SolicitadoPorNome { get; set; }
        public int? DecididoPor { get; set; }
        public string? DecididoPorNome { get; set; }
        public DateTime? DecididoEm { get; set; }
        public string? MotivoDecisao { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Conversa resolvida dentro da loja (o id da rota pode ser o do grupo).</summary>
    public sealed class ConversaCobranca
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid? IdCliente { get; set; }
        public Guid IdGrupo { get; set; }
        public Guid? IdCanal { get; set; }
        public string? Estado { get; set; }
        public DateTime? Janela24hFim { get; set; }
    }

    /// <summary>Pedido (tabela pedido) ou pre-pedido (cardapio_pedido_publico) alvo de uma cobranca vinculada.</summary>
    public sealed class AlvoCobranca
    {
        public int? PedidoId { get; set; }
        public Guid? PrePedidoId { get; set; }
        public Guid IdEstabelecimento { get; set; }
        public Guid? ClienteId { get; set; }
        public Guid? ConversaId { get; set; }
        /// <summary>Pedido: status_pedido (int). Pre-pedido: status textual.</summary>
        public string Status { get; set; } = string.Empty;
        public string? StatusPagamento { get; set; }
        public decimal? Total { get; set; }
        /// <summary>Ja recebido online (Pix/cartao) por qualquer intencao ligada ao pedido, liquido de estornos.</summary>
        public decimal Recebido { get; set; }
        public string Codigo { get; set; } = string.Empty;
    }
}
